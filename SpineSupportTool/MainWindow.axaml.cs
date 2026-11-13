using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SpineSupportTool.Models;
using SpineSupportTool.Services;

namespace SpineSupportTool;

public partial class MainWindow : Window
{
    private const int MaxLogLines = 1000;
    private const int MaxLogLineLength = 2000;
    private const string ReadyStatus = "준비 완료";

    private readonly ConfigService _configService;
    private readonly SpineHwaService _spineHwaService;
    private readonly BundleExtractorService _bundleService;
    private readonly BmsSyncService _bmsService;

    private readonly ObservableCollection<BundleItem> _bundles = new();
    private readonly ConcurrentQueue<string> _pendingLogs = new();
    private readonly Queue<string> _logLines = new();
    private readonly StringBuilder _logBuilder = new();
    private readonly DispatcherTimer _uiFlushTimer;
    private readonly Button[] _taskButtons;

    // 백그라운드 스레드가 마지막 진행률만 기록하고, UI 타이머가 100ms마다 한 번 반영한다.
    private readonly object _progressLock = new();
    private (int Current, int Total, double Percentage)? _pendingProgress;

    private CancellationTokenSource? _currentCts;

    public MainWindow()
    {
        InitializeComponent();

        _configService = new ConfigService();
        _spineHwaService = new SpineHwaService(_configService);
        _bundleService = new BundleExtractorService(_configService);
        _bmsService = new BmsSyncService();

        foreach (var service in new ServiceBase[] { _spineHwaService, _bundleService, _bmsService })
        {
            service.LogReceived += AppendLog;
            service.ProgressChanged += QueueProgress;
        }

        _taskButtons = new[] { BtnDownloadRelease, BtnBuildLocal, BtnTestCli, BtnStartExtractBundles, BtnStartBatchRender, BtnSyncBms };
        LstBundles.ItemsSource = _bundles;

        // 로그/진행률을 100ms 간격으로 묶어서 반영해 대량 출력 시 UI 스레드 포화를 막는다.
        _uiFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiFlushTimer.Tick += (_, _) => FlushPendingUi();
        _uiFlushTimer.Start();

        Loaded += (_, _) => InitializeApp();
        Closing += OnWindowClosing;
    }

    private void InitializeApp()
    {
        LoadConfigToUi(_configService.Config);
        RefreshSpineHwaStatus();
        RefreshBundles();
        if (_configService.LastError != null) AppendLog($"[설정] {_configService.LastError}");
        AppendLog("[시스템] Spine Support Tool 초기화 완료.");
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // 창을 닫을 때 실행 중인 외부 프로세스(sv-cli, AssetStudio 등) 트리를 정리한다.
        _currentCts?.Cancel();
        _uiFlushTimer.Stop();
    }

    #region Config <-> UI

    private void LoadConfigToUi(AppConfig cfg)
    {
        CfgMuseDashPath.Text = cfg.MuseDashPath;
        CfgAssetStudioCli.Text = cfg.AssetStudioCliPath;
        CfgSpineHwaPath.Text = cfg.SpineHwaPath;
        CfgWorkOutputDir.Text = cfg.WorkOutputDir;
        CfgBmsNoteSkinDir.Text = cfg.BmsNoteSkinDir;
        NumParallelJobs.Value = cfg.MaxParallelJobs;
        TxtGithubUrl.Text = cfg.GithubRepoUrl;
        TxtBmsTargetDir.Text = cfg.BmsNoteSkinDir;
        UpdateConfigDerivedPaths(cfg.WorkOutputDir);
        UpdateParallelHint();
    }

    private void UpdateConfigDerivedPaths(string? workDir)
    {
        var ext = AppConfig.GetExtractedDir(workDir);
        var render = AppConfig.GetRenderDir(workDir);
        TxtBundleTargetDir.Text = ext;
        TxtRenderInputDir.Text = ext;
        TxtRenderOutputDir.Text = render;
        TxtBmsSourceDir.Text = render;
    }

    private void UpdateParallelHint()
    {
        int requested = (int)(NumParallelJobs.Value ?? 0);
        TxtParallelHint.Text = $"(0 = 자동, 현재 적용값: {AppConfig.ResolveParallelJobs(requested)}개 / CPU {Environment.ProcessorCount}스레드)";
    }

    #endregion

    #region Status / Log / Progress

    private void RefreshSpineHwaStatus()
    {
        SetStatus(TxtViewerStatus, _spineHwaService.IsViewerInstalled(),
            "✅ 설치됨 (실행 가능)", "❌ 미설치 (bin\\SpineViewer.exe 없음)", Brushes.LightGreen, Brushes.IndianRed);
        SetStatus(TxtCliStatus, _spineHwaService.IsCliInstalled(),
            "✅ 준비 완료 (호출 가능)", "❌ 미설치 (bin\\sv-cli.exe 없음)", Brushes.LightGreen, Brushes.IndianRed);
        SetStatus(TxtSourceStatus, _spineHwaService.HasLocalSource(),
            "✅ 감지됨 (dist.ps1 컴파일 가능)", "⚠️ 소스 없음 (바이너리 모드로 사용)", Brushes.DodgerBlue, Brushes.Goldenrod);
    }

    private static void SetStatus(TextBlock target, bool ok, string okText, string failText, IBrush okBrush, IBrush failBrush)
    {
        target.Text = ok ? okText : failText;
        target.Foreground = ok ? okBrush : failBrush;
    }

    private void RefreshBundles()
    {
        _bundles.Clear();
        foreach (var item in _bundleService.ScanBundles())
        {
            _bundles.Add(item);
        }
    }

    /// <summary>스레드 안전: 어느 스레드에서든 호출 가능.</summary>
    public void AppendLog(string message)
    {
        if (message.Length > MaxLogLineLength)
        {
            message = message[..MaxLogLineLength] + $" …(+{message.Length - MaxLogLineLength}자 생략)";
        }
        _pendingLogs.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    /// <summary>스레드 안전: 최신 진행률만 보관하고 UI 반영은 타이머가 담당한다.</summary>
    private void QueueProgress(int current, int total, double percentage)
    {
        lock (_progressLock) _pendingProgress = (current, total, percentage);
    }

    private void FlushPendingUi()
    {
        FlushProgress();
        FlushLogs();
    }

    private void FlushProgress()
    {
        (int Current, int Total, double Percentage)? p;
        lock (_progressLock)
        {
            p = _pendingProgress;
            _pendingProgress = null;
        }
        if (p is not { } v || _currentCts == null) return;

        PrgTask.Value = Math.Clamp(v.Percentage, 0.0, 100.0);
        TxtProgressPercent.Text = $"{v.Percentage:F1}%";
        TxtStatusDetail.Text = v.Total > 0 ? $"({v.Current}/{v.Total})" : "";
    }

    private void FlushLogs()
    {
        if (_pendingLogs.IsEmpty) return;

        while (_pendingLogs.TryDequeue(out var line))
        {
            _logLines.Enqueue(line);
        }
        while (_logLines.Count > MaxLogLines)
        {
            _logLines.Dequeue();
        }

        _logBuilder.Clear();
        foreach (var line in _logLines)
        {
            if (_logBuilder.Length > 0) _logBuilder.AppendLine();
            _logBuilder.Append(line);
        }
        TxtLogConsole.Text = _logBuilder.ToString();
        TxtLogConsole.CaretIndex = TxtLogConsole.Text.Length;
    }

    #endregion

    #region Task orchestration

    /// <summary>
    /// 장시간 작업 공통 실행기: 버튼 잠금, 취소 토큰 수명, 취소/예외 처리, 상태 표시를 한 곳에서 관리한다.
    /// </summary>
    /// <param name="work">작업 본문. 완료 시 상태 표시줄에 표시할 문구를 반환한다.</param>
    private async Task RunTaskAsync(string busyStatus, string logTag, Func<CancellationToken, Task<string>> work)
    {
        if (_currentCts != null) return; // 이미 실행 중

        using var cts = new CancellationTokenSource();
        _currentCts = cts;
        SetBusy(true, busyStatus);

        string finalStatus;
        try
        {
            finalStatus = await work(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            AppendLog($"[{logTag}] 작업이 사용자에 의해 취소되었습니다.");
            finalStatus = "작업 취소됨";
        }
        catch (Exception ex)
        {
            AppendLog($"[{logTag}] 예외 발생: {ex.Message}");
            finalStatus = "오류 발생";
        }
        finally
        {
            _currentCts = null;
        }

        SetBusy(false, finalStatus);
    }

    private void SetBusy(bool isBusy, string status)
    {
        TxtStatusBar.Text = status;
        PnlProgress.IsVisible = isBusy;
        BtnCancelTask.IsEnabled = isBusy;

        // 실행 중 중복 클릭 방지
        foreach (var button in _taskButtons) button.IsEnabled = !isBusy;

        lock (_progressLock) _pendingProgress = null;
        TxtStatusDetail.Text = "";
        PrgTask.Value = 0;
        TxtProgressPercent.Text = "0.0%";
    }

    #endregion

    #region Event Handlers - Header & Top

    private void OnLaunchViewerClick(object? sender, RoutedEventArgs e) => _spineHwaService.LaunchViewer();

    private void OnOpenWorkDirClick(object? sender, RoutedEventArgs e) =>
        OpenFolderInExplorer(_configService.Config.WorkOutputDir);

    private void OnCancelTaskClick(object? sender, RoutedEventArgs e)
    {
        if (_currentCts is { IsCancellationRequested: false } cts)
        {
            AppendLog("[시스템] 사용자가 작업 취소를 요청했습니다...");
            cts.Cancel();
            BtnCancelTask.IsEnabled = false;
        }
    }

    #endregion

    #region Event Handlers - spine-hwa Tab

    private void OnRefreshStatusClick(object? sender, RoutedEventArgs e)
    {
        RefreshSpineHwaStatus();
        AppendLog("[spine-hwa] 상태를 새로고침했습니다.");
    }

    private async void OnTestCliClick(object? sender, RoutedEventArgs e)
    {
        await RunTaskAsync("sv-cli 테스트 실행 중...", "spine-hwa", async ct =>
        {
            var output = await _spineHwaService.GetCliHelpAsync(ct);
            if (output == null) return "sv-cli 없음";
            AppendLog($"sv-cli 도움말 출력:{Environment.NewLine}{output}");
            return ReadyStatus;
        });
    }

    private async void OnDownloadReleaseClick(object? sender, RoutedEventArgs e)
    {
        var url = TxtGithubUrl.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(url))
        {
            AppendLog("오류: 올바른 GitHub URL을 입력하세요.");
            return;
        }

        await RunTaskAsync("GitHub 릴리즈 다운로드 및 설치 중...", "spine-hwa", async ct =>
        {
            var success = await _spineHwaService.DownloadFromGitHubReleaseAsync(url, _configService.Config.SpineHwaPath, ct);
            RefreshSpineHwaStatus();
            return success ? "설치 성공" : "설치 실패";
        });
    }

    private async void OnBuildLocalClick(object? sender, RoutedEventArgs e)
    {
        await RunTaskAsync("로컬 Rust 릴리즈 컴파일 중...", "spine-hwa", async ct =>
        {
            var success = await _spineHwaService.BuildLocalSourceAsync(ct);
            RefreshSpineHwaStatus();
            return success ? "컴파일 및 배포 완료" : "컴파일 실패";
        });
    }

    #endregion

    #region Event Handlers - Bundle Extractor Tab

    private void OnRefreshBundlesClick(object? sender, RoutedEventArgs e) => RefreshBundles();

    private void OnSelectAllBundlesClick(object? sender, RoutedEventArgs e) => SelectBundles(_ => true);

    private void OnDeselectAllBundlesClick(object? sender, RoutedEventArgs e) => SelectBundles(_ => false);

    private void OnSelectMainScenesClick(object? sender, RoutedEventArgs e) =>
        SelectBundles(b => b.IsMainScene && !b.IsFoolVariant);

    private void SelectBundles(Func<BundleItem, bool> predicate)
    {
        foreach (var b in _bundles) b.IsSelected = predicate(b);
    }

    private async void OnStartExtractBundlesClick(object? sender, RoutedEventArgs e)
    {
        var selected = _bundles.Where(b => b.IsSelected).ToList();
        if (selected.Count == 0)
        {
            AppendLog("추출할 번들이 선택되지 않았습니다.");
            return;
        }

        var targetDir = _configService.Config.ExtractedOutputDir;
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            AppendLog("오류: 환경 설정에서 작업 기본 출력 폴더를 먼저 지정하세요.");
            return;
        }

        await RunTaskAsync($"번들 추출 중 ({selected.Count}개)...", "번들추출", async ct =>
        {
            var ok = await _bundleService.ExtractBundlesAsync(selected, targetDir, ct);
            return ok ? "번들 추출 완료" : "추출 완료 (오류 포함)";
        });
    }

    private void OnOpenExtractedDirClick(object? sender, RoutedEventArgs e) =>
        OpenFolderInExplorer(_configService.Config.ExtractedOutputDir);

    #endregion

    #region Event Handlers - Batch Render Tab

    private async void OnBrowseRenderInputDirClick(object? sender, RoutedEventArgs e) =>
        await BrowseFolderIntoAsync(TxtRenderInputDir, "렌더링할 스켈레톤 폴더 선택");

    private async void OnBrowseRenderOutputDirClick(object? sender, RoutedEventArgs e) =>
        await BrowseFolderIntoAsync(TxtRenderOutputDir, "렌더링 결과 저장 폴더 선택");

    private async void OnStartBatchRenderClick(object? sender, RoutedEventArgs e)
    {
        var inDir = TxtRenderInputDir.Text?.Trim();
        var outDir = TxtRenderOutputDir.Text?.Trim();

        if (string.IsNullOrEmpty(inDir) || string.IsNullOrEmpty(outDir))
        {
            AppendLog("오류: 렌더 입력 및 출력 폴더를 지정해주세요.");
            return;
        }

        double scale = CmbScale.SelectedIndex switch
        {
            1 => 1.0,
            2 => 0.25,
            _ => 0.5,
        };

        var skin = TxtRenderSkin.Text?.Trim();
        if (string.IsNullOrEmpty(skin)) skin = "all";

        await RunTaskAsync("Spine 몬스터/노트 배치 렌더링 중...", "sv-cli", async ct =>
        {
            var ok = await _spineHwaService.RenderMonstersAsync(inDir, outDir, scale, skin, ct);
            return ok ? "렌더링 완료" : "렌더링 완료 (확인 필요)";
        });
    }

    private void OnOpenRenderOutputDirClick(object? sender, RoutedEventArgs e) =>
        OpenFolderInExplorer(TxtRenderOutputDir.Text);

    #endregion

    #region Event Handlers - BMS Sync Tab

    private async void OnBrowseBmsSourceDirClick(object? sender, RoutedEventArgs e) =>
        await BrowseFolderIntoAsync(TxtBmsSourceDir, "렌더링된 소스 폴더 선택");

    private async void OnBrowseBmsTargetDirClick(object? sender, RoutedEventArgs e) =>
        await BrowseFolderIntoAsync(TxtBmsTargetDir, "BMS 에디터 노트 스킨 타깃 폴더 선택");

    private async void OnSyncBmsClick(object? sender, RoutedEventArgs e)
    {
        var src = TxtBmsSourceDir.Text?.Trim();
        var dst = TxtBmsTargetDir.Text?.Trim();

        if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst))
        {
            AppendLog("오류: 소스 및 대상 폴더를 지정해주세요.");
            return;
        }

        await RunTaskAsync("BMS 에디터 규격 폴더로 동기화 중...", "BMS동기화", async ct =>
        {
            var result = await _bmsService.SyncRenderedMonstersToBmsAsync(src, dst, ct);
            return $"BMS 동기화 완료 (복사 {result.Copied} / 변경 없음 {result.Unchanged}{(result.Failed > 0 ? $" / 실패 {result.Failed}" : "")})";
        });
    }

    private void OnOpenBmsDirClick(object? sender, RoutedEventArgs e) => OpenFolderInExplorer(TxtBmsTargetDir.Text);

    #endregion

    #region Event Handlers - Settings Tab

    private async void OnBrowseMuseDashPathClick(object? sender, RoutedEventArgs e) =>
        await BrowseFolderIntoAsync(CfgMuseDashPath, "뮤즈대쉬(Muse Dash) 설치 폴더 선택");

    private async void OnBrowseAssetStudioCliClick(object? sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync("AssetStudioModCLI.exe 선택", "실행 파일 (*.exe)", "*.exe");
        if (file != null) CfgAssetStudioCli.Text = file;
    }

    private async void OnBrowseSpineHwaPathClick(object? sender, RoutedEventArgs e) =>
        await BrowseFolderIntoAsync(CfgSpineHwaPath, "spine-hwa 프로젝트/설치 폴더 선택");

    private async void OnBrowseWorkOutputDirClick(object? sender, RoutedEventArgs e)
    {
        if (await BrowseFolderIntoAsync(CfgWorkOutputDir, "작업 기본 출력 폴더 선택") is { } path)
        {
            UpdateConfigDerivedPaths(path);
        }
    }

    private async void OnBrowseBmsNoteSkinDirClick(object? sender, RoutedEventArgs e)
    {
        if (await BrowseFolderIntoAsync(CfgBmsNoteSkinDir, "BMS 에디터 노트 스킨 폴더 선택") is { } path)
        {
            TxtBmsTargetDir.Text = path;
        }
    }

    private void OnParallelJobsChanged(object? sender, NumericUpDownValueChangedEventArgs e) => UpdateParallelHint();

    private void OnSaveConfigClick(object? sender, RoutedEventArgs e)
    {
        var cfg = _configService.Config;
        cfg.MuseDashPath = CfgMuseDashPath.Text?.Trim() ?? cfg.MuseDashPath;
        cfg.AssetStudioCliPath = CfgAssetStudioCli.Text?.Trim() ?? cfg.AssetStudioCliPath;
        cfg.SpineHwaPath = CfgSpineHwaPath.Text?.Trim() ?? cfg.SpineHwaPath;
        cfg.WorkOutputDir = CfgWorkOutputDir.Text?.Trim() ?? cfg.WorkOutputDir;
        cfg.BmsNoteSkinDir = CfgBmsNoteSkinDir.Text?.Trim() ?? cfg.BmsNoteSkinDir;
        cfg.MaxParallelJobs = (int)(NumParallelJobs.Value ?? 0);
        var url = TxtGithubUrl.Text?.Trim();
        if (!string.IsNullOrEmpty(url)) cfg.GithubRepoUrl = url;

        bool saved = _configService.Save();
        RefreshSpineHwaStatus();
        RefreshBundles();
        UpdateConfigDerivedPaths(cfg.WorkOutputDir);
        AppendLog(saved
            ? "[설정] 환경 설정이 성공적으로 저장되었습니다."
            : $"[설정] {_configService.LastError}");
    }

    private void OnResetConfigClick(object? sender, RoutedEventArgs e)
    {
        LoadConfigToUi(new AppConfig());
        AppendLog("[설정] 기본값으로 복원했습니다 (저장 버튼을 눌러 적용).");
    }

    #endregion

    private void OnClearLogClick(object? sender, RoutedEventArgs e)
    {
        while (_pendingLogs.TryDequeue(out _)) { }
        _logLines.Clear();
        TxtLogConsole.Text = "";
    }

    #region Helpers

    private async Task<string?> BrowseFolderIntoAsync(TextBox target, string title)
    {
        var path = await PickFolderAsync(title);
        if (path != null) target.Text = path;
        return path;
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });
            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        }
        catch (Exception ex)
        {
            AppendLog($"폴더 선택 오류: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> PickFileAsync(string title, string filterName, params string[] patterns)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(filterName) { Patterns = patterns } }
            });
            return files.Count > 0 ? files[0].Path.LocalPath : null;
        }
        catch (Exception ex)
        {
            AppendLog($"파일 선택 오류: {ex.Message}");
            return null;
        }
    }

    private void OpenFolderInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(path);
            using var _ = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"폴더 열기 실패: {ex.Message}");
        }
    }

    #endregion
}
