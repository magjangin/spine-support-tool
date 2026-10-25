using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SpineSupportTool.Models;

namespace SpineSupportTool.Services;

public class SpineHwaService
{
    private readonly ConfigService _configService;
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    public event Action<string>? LogReceived;
    public event Action<int, int, double>? ProgressChanged; // (current, total, percentage)

    public SpineHwaService(ConfigService configService)
    {
        _configService = configService;
    }

    private void Log(string message) => LogReceived?.Invoke($"[spine-hwa] {message}");

    public bool IsViewerInstalled()
    {
        return File.Exists(_configService.Config.SpineViewerExe);
    }

    public bool IsCliInstalled()
    {
        return File.Exists(_configService.Config.SvCliExe);
    }

    public bool HasLocalSource()
    {
        var cargoToml = Path.Combine(_configService.Config.SpineHwaPath, "Cargo.toml");
        return File.Exists(cargoToml);
    }

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

        if (!string.IsNullOrWhiteSpace(targetFile) && File.Exists(targetFile))
        {
            psi.ArgumentList.Add(targetFile);
        }

        Process.Start(psi);
        Log($"SpineViewer.exe 실행됨 (대상: {targetFile ?? "대시보드"})");
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
        ProgressChanged?.Invoke(0, 100, 0.0);

        var psi = new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-ExecutionPolicy Bypass -File .\\dist.ps1",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            double fakeProgress = 10.0;
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    Log(e.Data);
                    if (fakeProgress < 90.0) fakeProgress += 3.0;
                    ProgressChanged?.Invoke((int)fakeProgress, 100, fakeProgress);
                }
            };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Log($"[STDERR] {e.Data}"); };

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode == 0)
            {
                ProgressChanged?.Invoke(100, 100, 100.0);
                Log("빌드 완료! bin/ 폴더에 최신 바이너리가 배포되었습니다.");
                return true;
            }
            else
            {
                Log($"빌드 실패: 프로세스가 코드 {proc.ExitCode}로 종료되었습니다.");
                return false;
            }
        }
        catch (Exception ex)
        {
            Log($"빌드 실행 오류: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> DownloadFromGitHubReleaseAsync(string repoUrl, string installPath, CancellationToken ct = default)
    {
        try
        {
            Log($"GitHub 리포지토리 최신 릴리즈 확인 중: {repoUrl}...");
            ProgressChanged?.Invoke(0, 100, 0.0);

            var parts = repoUrl.TrimEnd('/').Split('/');
            if (parts.Length < 2)
            {
                Log("오류: 올바른 GitHub 저장소 URL 형식이 아닙니다.");
                return false;
            }

            var owner = parts[^2];
            var repo = parts[^1];
            var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";

            var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Add("User-Agent", "SpineSupportTool");

            var response = await HttpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log($"GitHub API 응답 오류: {response.StatusCode} ({apiUrl})");
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? zipUrl = null;
            long totalBytes = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = asset.GetProperty("browser_download_url").GetString();
                        if (asset.TryGetProperty("size", out var sizeProp))
                        {
                            totalBytes = sizeProp.GetInt64();
                        }
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(zipUrl))
            {
                Log("오류: 릴리즈에서 다운로드할 .zip 에셋을 찾을 수 없습니다.");
                return false;
            }

            Log($"다운로드 시작: {zipUrl} (크기: {totalBytes / (1024.0 * 1024.0):F2} MB)");
            Directory.CreateDirectory(installPath);
            var tempZip = Path.Combine(Path.GetTempPath(), $"spine-hwa-{Guid.NewGuid():N}.zip");

            using (var downloadResponse = await HttpClient.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                downloadResponse.EnsureSuccessStatusCode();
                if (totalBytes <= 0 && downloadResponse.Content.Headers.ContentLength.HasValue)
                {
                    totalBytes = downloadResponse.Content.Headers.ContentLength.Value;
                }

                using var downloadStream = await downloadResponse.Content.ReadAsStreamAsync(ct);
                using var fileStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long totalRead = 0;
                int read;

                while ((read = await downloadStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    totalRead += read;

                    if (totalBytes > 0)
                    {
                        double pct = (double)totalRead / totalBytes * 100.0;
                        ProgressChanged?.Invoke((int)(totalRead / 1024), (int)(totalBytes / 1024), pct);
                    }
                }
            }

            Log("압축 해제 및 설치 중...");
            ProgressChanged?.Invoke(95, 100, 95.0);
            ZipFile.ExtractToDirectory(tempZip, installPath, overwriteFiles: true);

            try { File.Delete(tempZip); } catch { }

            ProgressChanged?.Invoke(100, 100, 100.0);
            Log($"설치 완료! 위치: {installPath}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"GitHub 다운로드/설치 실패: {ex.Message}");
            return false;
        }
    }

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

        Directory.CreateDirectory(outputDir);

        // 입력 폴더 내 스켈레톤 파일(.skel, .json) 개수 파악
        var skelFiles = Directory.GetFiles(inputDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".skel", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int totalSkel = Math.Max(1, skelFiles.Count);
        Log($"몬스터/노트 일괄 렌더링 시작 (대상 스켈레톤 약 {skelFiles.Count}개):");
        Log($"  - 입력: {inputDir}");
        Log($"  - 출력: {outputDir}");
        Log($"  - 옵션: scale={scale}, skin={skin}");

        ProgressChanged?.Invoke(0, totalSkel, 0.0);

        var psi = new ProcessStartInfo(cli)
        {
            WorkingDirectory = Path.GetDirectoryName(cli),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("frame");
        psi.ArgumentList.Add(Path.GetFullPath(inputDir));
        psi.ArgumentList.Add("--skin");
        psi.ArgumentList.Add(skin);
        psi.ArgumentList.Add("--scale");
        psi.ArgumentList.Add(scale.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(Path.GetFullPath(outputDir));

        int renderedCount = 0;

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    Log(e.Data);
                    // sv-cli는 파일 렌더링 시마다 로그를 출력함
                    if (e.Data.Contains("saved", StringComparison.OrdinalIgnoreCase) || 
                        e.Data.Contains("frame", StringComparison.OrdinalIgnoreCase) ||
                        e.Data.Contains("png", StringComparison.OrdinalIgnoreCase) ||
                        e.Data.Contains("ok", StringComparison.OrdinalIgnoreCase))
                    {
                        renderedCount++;
                        double pct = Math.Min(99.0, (double)renderedCount / totalSkel * 100.0);
                        ProgressChanged?.Invoke(renderedCount, totalSkel, pct);
                    }
                }
            };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Log($"[STDERR] {e.Data}"); };

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode == 0)
            {
                ProgressChanged?.Invoke(totalSkel, totalSkel, 100.0);
                Log("렌더링 작업이 완료되었습니다!");
                return true;
            }
            else
            {
                Log($"렌더링 중 오류(코드 {proc.ExitCode})가 발생했습니다.");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            Log("렌더링 작업이 사용자에 의해 취소되었습니다.");
            return false;
        }
        catch (Exception ex)
        {
            Log($"렌더링 프로세스 오류: {ex.Message}");
            return false;
        }
    }
}
