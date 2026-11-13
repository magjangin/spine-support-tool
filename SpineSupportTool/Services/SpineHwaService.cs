using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpineSupportTool.Services;

public class SpineHwaService : ServiceBase
{
    /// <summary>sv-cli가 스켈레톤으로 인식하는 확장자 (spine-hwa headless.rs collect_inputs와 동일).</summary>
    private static readonly string[] SkeletonExtensions = { ".skel", ".json" };

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly ConfigService _configService;

    public SpineHwaService(ConfigService configService) : base("spine-hwa")
    {
        _configService = configService;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SpineSupportTool");
        return client;
    }

    public bool IsViewerInstalled() => File.Exists(_configService.Config.SpineViewerExe);

    public bool IsCliInstalled() => File.Exists(_configService.Config.SvCliExe);

    public bool HasLocalSource() =>
        !string.IsNullOrWhiteSpace(_configService.Config.SpineHwaPath)
        && File.Exists(Path.Combine(_configService.Config.SpineHwaPath, "Cargo.toml"));

    public void LaunchViewer(string? targetFile = null)
    {
        var exe = _configService.Config.SpineViewerExe;
        if (!File.Exists(exe))
        {
            Log($"오류: SpineViewer.exe가 없습니다: {exe}");
            return;
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)
        };

        bool hasTarget = !string.IsNullOrWhiteSpace(targetFile) && File.Exists(targetFile);
        if (hasTarget) psi.ArgumentList.Add(targetFile!);

        try
        {
            using var _ = Process.Start(psi);
            Log($"SpineViewer.exe 실행됨 (대상: {(hasTarget ? targetFile : "대시보드")})");
        }
        catch (Exception ex)
        {
            Log($"SpineViewer.exe 실행 실패: {ex.Message}");
        }
    }

    /// <summary>sv-cli 동작 확인용 도움말 출력. 응답이 없으면 10초 후 종료한다.</summary>
    public async Task<string?> GetCliHelpAsync(CancellationToken ct = default)
    {
        var cli = _configService.Config.SvCliExe;
        if (!File.Exists(cli))
        {
            Log($"오류: sv-cli.exe를 찾을 수 없습니다: {cli}");
            return null;
        }

        var result = await ProcessRunner.RunAsync(cli, new[] { "--help" }, Path.GetDirectoryName(cli),
            timeout: TimeSpan.FromSeconds(10), outputEncoding: Encoding.UTF8, ct: ct).ConfigureAwait(false);
        // sv-cli는 사용법을 stderr로, 오류 JSON을 stdout으로 출력한다.
        return string.Join(Environment.NewLine, new[] { result.StdErr.Trim(), result.StdOut.Trim() }.Where(s => s.Length > 0));
    }

    public async Task<bool> BuildLocalSourceAsync(CancellationToken ct = default)
    {
        var root = _configService.Config.SpineHwaPath;
        var distScript = Path.Combine(root, "dist.ps1");

        if (!File.Exists(distScript))
        {
            Log($"오류: dist.ps1 스크립트를 찾을 수 없습니다: {distScript}");
            return false;
        }

        Log("로컬 Rust 릴리즈 빌드 & 배포 시작 (dist.ps1)...");
        ReportProgress(0, 100, 0.0);

        // 빌드 단계 수를 알 수 없으므로 출력 줄마다 90%까지 점근적으로 증가시킨다.
        double fakeProgress = 0.0;
        void OnLine(string line)
        {
            Log(line);
            fakeProgress += (90.0 - fakeProgress) * 0.05;
            ReportProgress((int)fakeProgress, 100, fakeProgress);
        }

        try
        {
            var result = await ProcessRunner.RunAsync("powershell.exe",
                new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", distScript },
                workingDirectory: root,
                onStdOutLine: OnLine,
                onStdErrLine: line => Log($"[STDERR] {line}"),
                ct: ct).ConfigureAwait(false);

            if (result.Success)
            {
                ReportProgress(100, 100, 100.0);
                Log("빌드 완료! bin/ 폴더에 최신 바이너리가 배포되었습니다.");
                return true;
            }

            Log($"빌드 실패: 프로세스가 코드 {result.ExitCode}로 종료되었습니다.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"빌드 실행 오류: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> DownloadFromGitHubReleaseAsync(string repoUrl, string installPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            Log("오류: spine-hwa 설치 경로가 비어 있습니다. 환경 설정에서 지정하세요.");
            return false;
        }

        if (!TryParseGitHubRepo(repoUrl, out var owner, out var repo))
        {
            Log("오류: 올바른 GitHub 저장소 URL 형식이 아닙니다. (예: https://github.com/owner/repo)");
            return false;
        }

        var tempZip = Path.Combine(Path.GetTempPath(), $"spine-hwa-{Guid.NewGuid():N}.zip");
        try
        {
            var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            Log($"GitHub 리포지토리 최신 릴리즈 확인 중: {owner}/{repo}...");
            ReportProgress(0, 100, 0.0);

            var (zipUrl, totalBytes) = await FindReleaseZipAsync(apiUrl, ct).ConfigureAwait(false);
            if (zipUrl == null)
            {
                return false;
            }

            Log($"다운로드 시작: {zipUrl} (크기: {totalBytes / (1024.0 * 1024.0):F2} MB)");
            await DownloadFileAsync(zipUrl, tempZip, totalBytes, ct).ConfigureAwait(false);

            Log("압축 해제 및 설치 중...");
            ReportProgress(95, 100, 95.0);
            Directory.CreateDirectory(installPath);
            // 압축 해제는 동기 API이므로 스레드 풀에서 실행해 UI 스레드를 막지 않는다.
            await Task.Run(() => ZipFile.ExtractToDirectory(tempZip, installPath, overwriteFiles: true), ct).ConfigureAwait(false);

            ReportProgress(100, 100, 100.0);
            Log($"설치 완료! 위치: {installPath}");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"GitHub 다운로드/설치 실패: {ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(tempZip); } catch { /* 임시 파일 정리 실패 무시 */ }
        }
    }

    private async Task<(string? Url, long Size)> FindReleaseZipAsync(string apiUrl, CancellationToken ct)
    {
        using var response = await HttpClient.GetAsync(apiUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Log($"GitHub API 응답 오류: {(int)response.StatusCode} {response.StatusCode} ({apiUrl})");
            return (null, 0);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                long size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0;
                if (!string.IsNullOrEmpty(url)) return (url, size);
            }
        }

        Log("오류: 릴리즈에서 다운로드할 .zip 에셋을 찾을 수 없습니다.");
        return (null, 0);
    }

    private async Task DownloadFileAsync(string url, string destPath, long totalBytes, CancellationToken ct)
    {
        using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (totalBytes <= 0) totalBytes = response.Content.Headers.ContentLength ?? 0;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int lastReportedPct = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            totalRead += read;

            if (totalBytes <= 0) continue;
            // 80KB 청크마다 이벤트를 쏘지 않고 1% 단위로만 보고한다 (0~94%: 이후는 압축 해제 단계).
            int pct = (int)(totalRead * 94 / totalBytes);
            if (pct != lastReportedPct)
            {
                lastReportedPct = pct;
                ReportProgress((int)(totalRead / 1024), (int)(totalBytes / 1024), pct);
            }
        }
    }

    internal static bool TryParseGitHubRepo(string? repoUrl, out string owner, out string repo)
    {
        owner = repo = string.Empty;
        if (string.IsNullOrWhiteSpace(repoUrl)) return false;

        var trimmed = repoUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^4];

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) return false;
            owner = segments[0];
            repo = segments[1];
        }
        else
        {
            // "owner/repo" 축약형 허용
            var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;
            owner = parts[0];
            repo = parts[1];
        }
        return owner.Length > 0 && repo.Length > 0;
    }

    /// <summary>
    /// 입력 폴더의 스켈레톤을 sv-cli로 일괄 렌더링한다.
    /// sv-cli는 폴더 입력 시 스켈레톤마다 자식 프로세스를 "순차" 실행하므로, 여기서 스켈레톤 단위로 직접
    /// 병렬 실행한다(--root로 출력 폴더 구조는 폴더 입력과 동일하게 유지). 각 실행은 마지막 줄에 JSON 결과를 출력한다.
    /// </summary>
    public async Task<bool> RenderMonstersAsync(string inputDir, string outputDir, double scale = 0.5, string skin = "all", CancellationToken ct = default)
    {
        var cli = _configService.Config.SvCliExe;
        if (!File.Exists(cli))
        {
            Log($"오류: sv-cli.exe가 없습니다: {cli}");
            return false;
        }

        if (!Directory.Exists(inputDir))
        {
            Log($"오류: 입력 폴더가 없습니다: {inputDir}");
            return false;
        }

        var root = Path.GetFullPath(inputDir);
        var outRoot = Path.GetFullPath(outputDir);
        var cliDir = Path.GetDirectoryName(cli);

        // 대용량 트리 열거는 스레드 풀에서 수행해 UI를 막지 않는다.
        var skeletons = await Task.Run(() => Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => SkeletonExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList(), ct).ConfigureAwait(false);

        if (skeletons.Count == 0)
        {
            Log($"오류: 입력 폴더에서 스켈레톤(.skel/.json)을 찾지 못했습니다: {root}");
            return false;
        }

        Directory.CreateDirectory(outRoot);
        int parallel = Math.Min(_configService.Config.EffectiveParallelJobs, skeletons.Count);
        var scaleArg = scale.ToString("0.0##", CultureInfo.InvariantCulture);

        Log($"몬스터/노트 일괄 렌더링 시작 (스켈레톤 {skeletons.Count}개, 동시 {parallel}개)");
        Log($"  - 입력: {root}");
        Log($"  - 출력: {outRoot}");
        Log($"  - 옵션: scale={scaleArg}, skin={skin}");

        int total = skeletons.Count;
        int completed = 0;
        int pngCount = 0;
        var failures = new ConcurrentBag<string>();
        var sw = Stopwatch.StartNew();
        ReportProgress(0, total);

        try
        {
            await Parallel.ForEachAsync(skeletons, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct },
                async (skel, token) =>
                {
                    var rel = Path.GetRelativePath(root, skel);
                    try
                    {
                        var result = await ProcessRunner.RunAsync(cli,
                            new[] { "frame", skel, "--root", root, "--skin", skin, "--scale", scaleArg, "--out", outRoot },
                            workingDirectory: cliDir,
                            outputEncoding: Encoding.UTF8,
                            ct: token).ConfigureAwait(false);

                        var summary = ParseCliSummary(result);
                        Interlocked.Add(ref pngCount, summary.Rendered);
                        foreach (var reason in summary.Errors) failures.Add($"{rel}: {reason}");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{rel}: {ex.Message}");
                    }

                    int done = Interlocked.Increment(ref completed);
                    ReportProgress(done, total);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log("렌더링 작업이 사용자에 의해 취소되었습니다.");
            throw;
        }

        Log($"렌더링 완료: PNG {pngCount}개 생성, 스켈레톤 {total}개 처리, 실패/스킵 {failures.Count}건 ({sw.Elapsed.TotalSeconds:F1}초)");
        const int maxShown = 30;
        foreach (var f in failures.Order(StringComparer.Ordinal).Take(maxShown))
        {
            Log($"  ⚠ {f}");
        }
        if (failures.Count > maxShown) Log($"  ... 외 {failures.Count - maxShown}건");

        return pngCount > 0;
    }

    internal readonly record struct CliSummary(int Rendered, IReadOnlyList<string> Errors);

    /// <summary>sv-cli 출력의 마지막 JSON 줄({ok, count, results, skipped} 또는 {ok:false, error})을 요약한다.</summary>
    internal static CliSummary ParseCliSummary(ProcessResult result)
    {
        var lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines.Reverse())
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var rootEl = doc.RootElement;
                var errors = new List<string>();

                if (rootEl.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    int count = rootEl.TryGetProperty("count", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
                    if (rootEl.TryGetProperty("skipped", out var skipped) && skipped.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in skipped.EnumerateArray())
                        {
                            errors.Add(s.TryGetProperty("reason", out var r) ? r.GetString() ?? "?" : s.ToString());
                        }
                    }
                    return new CliSummary(count, errors);
                }

                errors.Add(rootEl.TryGetProperty("error", out var err) ? err.GetString() ?? "알 수 없는 오류" : line);
                return new CliSummary(0, errors);
            }
            catch (JsonException)
            {
                // JSON이 아닌 줄은 건너뛴다.
            }
        }

        var stderr = result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return new CliSummary(0, new[] { $"결과 JSON 없음 (코드 {result.ExitCode}){(stderr != null ? $": {stderr}" : "")}" });
    }
}
