using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SpineSupportTool.Models;

namespace SpineSupportTool.Services;

public class BundleExtractorService : ServiceBase
{
    private readonly ConfigService _configService;

    public BundleExtractorService(ConfigService configService) : base("번들추출")
    {
        _configService = configService;
    }

    public List<BundleItem> ScanBundles()
    {
        var streamPath = _configService.Config.StreamingAssetsPath;
        if (!Directory.Exists(streamPath))
        {
            Log($"경고: 스트리밍 에셋 폴더가 없습니다: {streamPath}");
            return new List<BundleItem>();
        }

        var list = Directory.EnumerateFiles(streamPath, "gamescene*.bundle", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(BundleItem.FromFile)
            .ToList();

        Log($"발견된 gamescene 에셋 번들: {list.Count}개");
        return list;
    }

    /// <summary>
    /// 선택된 번들을 AssetStudioModCLI로 추출한다.
    /// 번들마다 독립 프로세스이므로 장면 태그 단위로 병렬 실행한다(같은 태그는 같은 출력 폴더를 쓰므로 순차).
    /// </summary>
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

        var groups = items.GroupBy(b => b.SceneTag, StringComparer.OrdinalIgnoreCase).ToList();
        int parallel = Math.Min(_configService.Config.EffectiveParallelJobs, groups.Count);

        Directory.CreateDirectory(targetDir);
        Log($"총 {items.Count}개 번들 추출 시작 (동시 {parallel}개) -> {targetDir}");

        int total = items.Count;
        int completed = 0;
        int successCount = 0;
        ReportProgress(0, total);

        try
        {
            await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct },
                async (group, token) =>
                {
                    var bundleOutDir = Path.Combine(targetDir, group.Key, "원본");
                    Directory.CreateDirectory(bundleOutDir);

                    foreach (var item in group)
                    {
                        token.ThrowIfCancellationRequested();
                        if (await ExtractOneAsync(cli, item, bundleOutDir, token).ConfigureAwait(false))
                        {
                            Interlocked.Increment(ref successCount);
                        }
                        ReportProgress(Interlocked.Increment(ref completed), total);
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log("사용자에 의해 번들 추출이 취소되었습니다.");
            throw;
        }

        Log($"번들 추출 완료: {successCount}/{total} 성공");
        return successCount > 0;
    }

    private async Task<bool> ExtractOneAsync(string cli, BundleItem item, string bundleOutDir, CancellationToken ct)
    {
        Log($"추출 중 [{item.SceneTag}]: {item.FileName}");
        try
        {
            var result = await ProcessRunner.RunAsync(cli,
                new[] { item.FilePath, "-t", "tex2d,textAsset", "-g", "type", "-o", bundleOutDir, "--log-level", "warning" },
                ct: ct).ConfigureAwait(false);

            if (!result.Success)
            {
                var detail = FirstNonEmpty(result.StdErr, result.StdOut);
                Log($"오류 ({item.SceneTag}): 코드 {result.ExitCode} - {detail}");
                return false;
            }

            int moved = FlattenAssetStudioOutput(bundleOutDir);
            Log($"완료 [{item.SceneTag}]: 파일 {moved}개 정리 ({bundleOutDir})");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"예외 발생 ({item.SceneTag}): {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// AssetStudio의 TextAsset\, Texture2D\ 분리 폴더를 상위 폴더로 평탄화하고 빈 폴더를 정리한다.
    /// </summary>
    private int FlattenAssetStudioOutput(string dir)
    {
        int moved = 0;
        try
        {
            foreach (var sub in Directory.GetDirectories(dir))
            {
                foreach (var file in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories).ToList())
                {
                    File.Move(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
                    moved++;
                }

                try
                {
                    if (!Directory.EnumerateFileSystemEntries(sub, "*", SearchOption.AllDirectories)
                            .Any(e => File.Exists(e)))
                    {
                        Directory.Delete(sub, recursive: true);
                    }
                }
                catch
                {
                    // 정리 실패(잠금 등)는 무시: 다음 추출 시 다시 정리된다.
                }
            }
        }
        catch (Exception ex)
        {
            Log($"에셋 폴더 정리 중 알림: {ex.Message}");
        }
        return moved;
    }

    private static string FirstNonEmpty(params string[] texts) =>
        texts.Select(t => t.Trim()).FirstOrDefault(t => t.Length > 0) ?? "(출력 없음)";
}
