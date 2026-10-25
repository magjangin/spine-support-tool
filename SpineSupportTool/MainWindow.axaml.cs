using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SpineSupportTool.Models;
using SpineSupportTool.Services;

namespace SpineSupportTool;

public partial class MainWindow : Window
{
    private readonly ConfigService _configService;
    private readonly SpineHwaService _spineHwaService;
    private readonly BundleExtractorService _bundleService;
    private readonly BmsSyncService _bmsService;

    private readonly ObservableCollection<BundleItem> _bundles = new();
    private readonly StringBuilder _logBuffer = new();
    private CancellationTokenSource? _currentCts;

    public MainWindow()
    {
        InitializeComponent();

        _configService = new ConfigService();
        _spineHwaService = new SpineHwaService(_configService);
        _bundleService = new BundleExtractorService(_configService);
        _bmsService = new BmsSyncService(_configService);

        _spineHwaService.LogReceived += AppendLog;
        _bundleService.LogReceived += AppendLog;
        _bmsService.LogReceived += AppendLog;

        _spineHwaService.ProgressChanged += UpdateProgress;
        _bundleService.ProgressChanged += UpdateProgress;
        _bmsService.ProgressChanged += UpdateProgress;

        LstBundles.ItemsSource = _bundles;

        Loaded += (_, _) => InitializeApp();
    }

    private void InitializeApp()
    {
        LoadConfigToUi();
        RefreshSpineHwaStatus();
        RefreshBundles();
        AppendLog("[시스템] Spine Support Tool 초기화 완료.");
    }

    private void LoadConfigToUi()
    {
        var cfg = _configService.Config;
        CfgMuseDashPath.Text = cfg.MuseDashPath;
        CfgAssetStudioCli.Text = cfg.AssetStudioCliPath;
        CfgSpineHwaPath.Text = cfg.SpineHwaPath;
        CfgWorkOutputDir.Text = cfg.WorkOutputDir;
        CfgBmsNoteSkinDir.Text = cfg.BmsNoteSkinDir;

        TxtBundleTargetDir.Text = cfg.ExtractedOutputDir;
        TxtRenderInputDir.Text = cfg.ExtractedOutputDir;
        TxtRenderOutputDir.Text = cfg.RenderOutputDir;
        TxtBmsSourceDir.Text = cfg.RenderOutputDir;
        TxtBmsTargetDir.Text = cfg.BmsNoteSkinDir;
        TxtGithubUrl.Text = cfg.GithubRepoUrl;
    }

    private void RefreshSpineHwaStatus()
    {
        bool viewerOk = _spineHwaService.IsViewerInstalled();
        TxtViewerStatus.Text = viewerOk ? "✅ 설치됨 (실행 가능)" : "❌ 미설치 (bin\\SpineViewer.exe 없음)";
        TxtViewerStatus.Foreground = viewerOk ? Avalonia.Media.Brushes.LightGreen : Avalonia.Media.Brushes.IndianRed;

        bool cliOk = _spineHwaService.IsCliInstalled();
        TxtCliStatus.Text = cliOk ? "✅ 준비 완료 (호출 가능)" : "❌ 미설치 (bin\\sv-cli.exe 없음)";
        TxtCliStatus.Foreground = cliOk ? Avalonia.Media.Brushes.LightGreen : Avalonia.Media.Brushes.IndianRed;

        bool srcOk = _spineHwaService.HasLocalSource();
        TxtSourceStatus.Text = srcOk ? "✅ 감지됨 (dist.ps1 컴파일 가능)" : "⚠️ 소스 없음 (바이너리 모드로 사용)";
        TxtSourceStatus.Foreground = srcOk ? Avalonia.Media.Brushes.DodgerBlue : Avalonia.Media.Brushes.Goldenrod;
    }

    private void RefreshBundles()
    {
        _bundles.Clear();
        var list = _bundleService.ScanBundles();
        foreach (var item in list)
        {
            _bundles.Add(item);
        }
        TxtBundleTargetDir.Text = _configService.Config.ExtractedOutputDir;
    }

    public void AppendLog(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _logBuffer.AppendLine(line);
            TxtLogConsole.Text = _logBuffer.ToString();
            TxtLogConsole.CaretIndex = TxtLogConsole.Text.Length;
        });
    }

    private void SetBusy(bool isBusy, string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TxtStatusBar.Text = status;
            PnlProgress.IsVisible = isBusy;
            if (!isBusy)
            {
                TxtStatusDetail.Text = "";
                PrgTask.Value = 0;
                TxtProgressPercent.Text = "0.0%";
            }
        });
    }

    private void UpdateProgress(int current, int total, double percentage)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PnlProgress.IsVisible = true;
            PrgTask.Value = Math.Clamp(percentage, 0.0, 100.0);
            TxtProgressPercent.Text = $"{percentage:F1}%";
            TxtStatusDetail.Text = total > 0 ? $"({current}/{total})" : "";
        });
    }

    #region Event Handlers - Header & Top

    private void OnLaunchViewerClick(object? sender, RoutedEventArgs e)
    {
        _spineHwaService.LaunchViewer();
    }

    private void OnOpenWorkDirClick(object? sender, RoutedEventArgs e)
    {
        OpenFolderInExplorer(_configService.Config.WorkOutputDir);
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
        var cli = _configService.Config.SvCliExe;
        if (!File.Exists(cli))
        {
            AppendLog($"오류: sv-cli.exe를 찾을 수 없습니다: {cli}");
            return;
        }

        SetBusy(true, "sv-cli 테스트 실행 중...");
        await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(cli)
                {
                    Arguments = "--help",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var output = p.StandardOutput.ReadToEnd();
                    AppendLog($"sv-cli 도움말 출력:\n{output}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"sv-cli 테스트 실패: {ex.Message}");
            }
        });
        SetBusy(false, "준비 완료");
    }

    private async void OnDownloadReleaseClick(object? sender, RoutedEventArgs e)
    {
        var url = TxtGithubUrl.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(url))
        {
            AppendLog("오류: 올바른 GitHub URL을 입력하세요.");
            return;
        }

        _currentCts = new CancellationTokenSource();
        SetBusy(true, "GitHub 릴리즈 다운로드 및 설치 중...");
        try
        {
            var success = await _spineHwaService.DownloadFromGitHubReleaseAsync(url, _configService.Config.SpineHwaPath, _currentCts.Token);
            RefreshSpineHwaStatus();
            SetBusy(false, success ? "설치 성공" : "설치 실패");
        }
        catch (Exception ex)
        {
            AppendLog($"설치 중 오류: {ex.Message}");
            SetBusy(false, "오류 발생");
        }
    }

    private async void OnBuildLocalClick(object? sender, RoutedEventArgs e)
    {
        _currentCts = new CancellationTokenSource();
        SetBusy(true, "로컬 Rust 릴리즈 컴파일 중...");
        try
        {
            var success = await _spineHwaService.BuildLocalSourceAsync(_currentCts.Token);
            RefreshSpineHwaStatus();
            SetBusy(false, success ? "컴파일 및 배포 완료" : "컴파일 실패");
        }
        catch (Exception ex)
        {
            AppendLog($"빌드 실패: {ex.Message}");
            SetBusy(false, "빌드 실패");
        }
    }

    #endregion

    #region Event Handlers - Bundle Extractor Tab

    private void OnRefreshBundlesClick(object? sender, RoutedEventArgs e)
    {
        RefreshBundles();
    }

    private void OnSelectAllBundlesClick(object? sender, RoutedEventArgs e)
    {
        foreach (var b in _bundles) b.IsSelected = true;
    }

    private void OnDeselectAllBundlesClick(object? sender, RoutedEventArgs e)
    {
        foreach (var b in _bundles) b.IsSelected = false;
    }

    private void OnSelectMainScenesClick(object? sender, RoutedEventArgs e)
    {
        foreach (var b in _bundles)
        {
            b.IsSelected = b.SceneTag.StartsWith("장면") && !b.SceneTag.Contains("fool");
        }
    }

    private async void OnStartExtractBundlesClick(object? sender, RoutedEventArgs e)
    {
        var selected = _bundles.Where(b => b.IsSelected).ToList();
        if (selected.Count == 0)
        {
            AppendLog("추출할 번들이 선택되지 않았습니다.");
            return;
        }

        _currentCts = new CancellationTokenSource();
        SetBusy(true, $"번들 추출 중 ({selected.Count}개)...");
        try
        {
            var targetDir = _configService.Config.ExtractedOutputDir;
            var ok = await _bundleService.ExtractBundlesAsync(selected, targetDir, _currentCts.Token);
            SetBusy(false, ok ? "번들 추출 완료" : "추출 완료 (오류 포함)");
        }
        catch (Exception ex)
        {
            AppendLog($"번들 추출 예외: {ex.Message}");
            SetBusy(false, "추출 중단");
        }
    }

    private void OnOpenExtractedDirClick(object? sender, RoutedEventArgs e)
    {
        OpenFolderInExplorer(_configService.Config.ExtractedOutputDir);
    }

    #endregion

    #region Event Handlers - Batch Render Tab

    private async void OnBrowseRenderInputDirClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("렌더링할 스켈레톤 폴더 선택");
        if (path != null) TxtRenderInputDir.Text = path;
    }

    private async void OnBrowseRenderOutputDirClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("렌더링 결과 저장 폴더 선택");
        if (path != null) TxtRenderOutputDir.Text = path;
    }

    private async void OnStartBatchRenderClick(object? sender, RoutedEventArgs e)
    {
        var inDir = TxtRenderInputDir.Text?.Trim();
        var outDir = TxtRenderOutputDir.Text?.Trim();

        if (string.IsNullOrEmpty(inDir) || string.IsNullOrEmpty(outDir))
        {
            AppendLog("오류: 렌더 입력 및 출력 폴더를 지정해주세요.");
            return;
        }

        double scale = 0.5;
        if (CmbScale.SelectedIndex == 1) scale = 1.0;
        else if (CmbScale.SelectedIndex == 2) scale = 0.25;

        var skin = TxtRenderSkin.Text?.Trim();
        if (string.IsNullOrEmpty(skin)) skin = "all";

        _currentCts = new CancellationTokenSource();
        SetBusy(true, "Spine 몬스터/노트 배치 렌더링 중...");
        try
        {
            var ok = await _spineHwaService.RenderMonstersAsync(inDir, outDir, scale, skin, _currentCts.Token);
            SetBusy(false, ok ? "렌더링 완료" : "렌더링 완료 (확인 필요)");
        }
        catch (Exception ex)
        {
            AppendLog($"렌더링 예외: {ex.Message}");
            SetBusy(false, "렌더링 오류");
        }
    }

    private void OnOpenRenderOutputDirClick(object? sender, RoutedEventArgs e)
    {
        OpenFolderInExplorer(TxtRenderOutputDir.Text);
    }

    #endregion

    #region Event Handlers - BMS Sync Tab

    private async void OnBrowseBmsSourceDirClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("렌더링된 소스 폴더 선택");
        if (path != null) TxtBmsSourceDir.Text = path;
    }

    private async void OnBrowseBmsTargetDirClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("BMS 에디터 노트 스킨 타깃 폴더 선택");
        if (path != null) TxtBmsTargetDir.Text = path;
    }

    private async void OnSyncBmsClick(object? sender, RoutedEventArgs e)
    {
        var src = TxtBmsSourceDir.Text?.Trim();
        var dst = TxtBmsTargetDir.Text?.Trim();

        if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst))
        {
            AppendLog("오류: 소스 및 대상 폴더를 지정해주세요.");
            return;
        }

        _currentCts = new CancellationTokenSource();
        SetBusy(true, "BMS 에디터 규격 폴더로 동기화 중...");
        try
        {
            int count = await _bmsService.SyncRenderedMonstersToBmsAsync(src, dst, _currentCts.Token);
            SetBusy(false, $"BMS 동기화 완료 ({count}개)");
        }
        catch (Exception ex)
        {
            AppendLog($"동기화 예외: {ex.Message}");
            SetBusy(false, "동기화 오류");
        }
    }

    private void OnOpenBmsDirClick(object? sender, RoutedEventArgs e)
    {
        OpenFolderInExplorer(TxtBmsTargetDir.Text);
    }

    #endregion

    #region Event Handlers - Settings Tab

    private void OnSaveConfigClick(object? sender, RoutedEventArgs e)
    {
        var cfg = _configService.Config;
        cfg.MuseDashPath = CfgMuseDashPath.Text?.Trim() ?? cfg.MuseDashPath;
        cfg.AssetStudioCliPath = CfgAssetStudioCli.Text?.Trim() ?? cfg.AssetStudioCliPath;
        cfg.SpineHwaPath = CfgSpineHwaPath.Text?.Trim() ?? cfg.SpineHwaPath;
        cfg.WorkOutputDir = CfgWorkOutputDir.Text?.Trim() ?? cfg.WorkOutputDir;
        cfg.BmsNoteSkinDir = CfgBmsNoteSkinDir.Text?.Trim() ?? cfg.BmsNoteSkinDir;

        _configService.Save();
        RefreshSpineHwaStatus();
        RefreshBundles();
        AppendLog("[설정] 환경 설정이 성공적으로 저장되었습니다.");
    }

    private void OnResetConfigClick(object? sender, RoutedEventArgs e)
    {
        var cfg = new AppConfig();
        CfgMuseDashPath.Text = cfg.MuseDashPath;
        CfgAssetStudioCli.Text = cfg.AssetStudioCliPath;
        CfgSpineHwaPath.Text = cfg.SpineHwaPath;
        CfgWorkOutputDir.Text = cfg.WorkOutputDir;
        CfgBmsNoteSkinDir.Text = cfg.BmsNoteSkinDir;
        AppendLog("[설정] 기본값으로 복원했습니다 (저장 버튼을 눌러 적용).");
    }

    #endregion

    private void OnClearLogClick(object? sender, RoutedEventArgs e)
    {
        _logBuffer.Clear();
        TxtLogConsole.Text = "";
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

            if (folders.Count > 0)
            {
                return folders[0].Path.LocalPath;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"폴더 선택 오류: {ex.Message}");
        }
        return null;
    }

    private void OpenFolderInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog($"폴더 열기 실패: {ex.Message}");
        }
    }
}
