using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpineSupportTool.Models;

namespace SpineSupportTool.Services;

public class BundleExtractorService
{
    private readonly ConfigService _configService;

    public event Action<string>? LogReceived;
    public event Action<int, int, double>? ProgressChanged; // (current, total, percentage)

    public BundleExtractorService(ConfigService configService)
    {
        _configService = configService;
    }

    private void Log(string message) => LogReceived?.Invoke($"[번들추출] {message}");

    public List<BundleItem> ScanBundles()
    {
        var list = new List<BundleItem>();
        var streamPath = _configService.Config.StreamingAssetsPath;

        if (!Directory.Exists(streamPath))
        {
            Log($"경고: 스트리밍 에셋 폴더가 없습니다: {streamPath}");
            return list;
        }

        var files = Directory.GetFiles(streamPath, "gamescene*.bundle", SearchOption.TopDirectoryOnly);
        foreach (var file in files.OrderBy(f => f))
        {
            list.Add(BundleItem.FromFile(file));
        }

        Log($"발견된 gamescene 에셋 번들: {list.Count}개");
        return list;
    }

    public async Task<bool> ExtractBundlesAsync(IEnumerable<BundleItem> selectedBundles, string targetDir, CancellationToken ct = default)
    {
        var cli = _configService.Config.AssetStudioCliPath;
        if (!File.Exists(cli))
        {
            Log($"오류: AssetStudioModCLI.exe가 없습니다: {cli}");
            return false;
        }

        var items = selectedBundles.Where(b => b.IsSelected).ToList();
        if (items.Count == 0)
        {
            Log("선택된 번들이 없습니다.");
            return false;
        }

        Directory.CreateDirectory(targetDir);
        Log($"총 {items.Count}개 번들 추출 시작 -> {targetDir}");

        int successCount = 0;
        int current = 0;
        int total = items.Count;

        ProgressChanged?.Invoke(0, total, 0.0);

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var bundleOutDir = Path.Combine(targetDir, item.SceneTag, "원본");
            Directory.CreateDirectory(bundleOutDir);

            Log($"번들 추출 중 [{item.SceneTag}] ({current + 1}/{total}): {item.FileName}...");

            var psi = new ProcessStartInfo(cli)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add(item.FilePath);
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("tex2d,textAsset");
            psi.ArgumentList.Add("-g");
            psi.ArgumentList.Add("type");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(bundleOutDir);
            psi.ArgumentList.Add("--log-level");
            psi.ArgumentList.Add("warning");

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null) continue;

                var stdOutTask = proc.StandardOutput.ReadToEndAsync(ct);
                var stdErrTask = proc.StandardError.ReadToEndAsync(ct);

                await proc.WaitForExitAsync(ct);

                if (proc.ExitCode == 0)
                {
                    FlattenAssetStudioOutput(bundleOutDir);
                    successCount++;
                    Log($"완료: {item.SceneTag} 에셋 추출 성공 ({bundleOutDir})");
                }
                else
                {
                    var err = await stdErrTask;
                    Log($"오류 ({item.SceneTag}): 코드 {proc.ExitCode} - {err}");
                }
            }
            catch (OperationCanceledException)
            {
                Log("사용자에 의해 번들 추출이 취소되었습니다.");
                throw;
            }
            catch (Exception ex)
            {
                Log($"예외 발생 ({item.SceneTag}): {ex.Message}");
            }
            finally
            {
                current++;
                double pct = (double)current / total * 100.0;
                ProgressChanged?.Invoke(current, total, pct);
            }
        }

        Log($"번들 추출 완료: {successCount}/{items.Count} 성공");
        return successCount > 0;
    }

    private void FlattenAssetStudioOutput(string dir)
    {
        try
        {
            var subDirs = Directory.GetDirectories(dir);
            foreach (var sub in subDirs)
            {
                var files = Directory.GetFiles(sub, "*.*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var fi = new FileInfo(file);
                    var dest = Path.Combine(dir, fi.Name);
                    if (!File.Exists(dest))
                    {
                        File.Move(file, dest);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"에셋 폴더 정리 중 알림: {ex.Message}");
        }
    }
}
