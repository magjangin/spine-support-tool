using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SpineSupportTool.Services;

public sealed record BmsSyncResult(int Copied, int Unchanged, int Failed)
{
    public int Total => Copied + Unchanged + Failed;
}

public partial class BmsSyncService : ServiceBase
{
    public BmsSyncService() : base("BMS동기화")
    {
    }

    /// <summary>
    /// 렌더된 PNG를 BMS 에디터 노트 스킨 트리로 복사한다.
    /// - NNMM*.png → 장면NN\NNMM\ (MM이 01~03이면 장면NN\보스\NNMM\)
    /// - 규칙에 맞지 않는 파일은 상대 경로를 유지해 복사
    /// 크기와 수정 시각이 같은 대상 파일은 건너뛰어 재동기화를 빠르게 한다.
    /// </summary>
    public async Task<BmsSyncResult> SyncRenderedMonstersToBmsAsync(string renderedDir, string bmsDir, CancellationToken ct = default)
    {
        if (!Directory.Exists(renderedDir))
        {
            Log($"오류: 렌더링 폴더가 없습니다: {renderedDir}");
            return new BmsSyncResult(0, 0, 0);
        }

        var srcFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(renderedDir));
        var dstFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bmsDir));
        if (IsSameOrNested(srcFull, dstFull))
        {
            Log("오류: 렌더 소스 폴더와 BMS 스킨 폴더는 서로 같거나 포함 관계일 수 없습니다.");
            return new BmsSyncResult(0, 0, 0);
        }

        Directory.CreateDirectory(dstFull);
        Log($"BMS 노트 이미지 동기화 시작: {srcFull} -> {dstFull}");

        var result = await Task.Run(() => SyncCore(srcFull, dstFull, ct), ct).ConfigureAwait(false);

        Log($"BMS 노트 스킨 동기화 완료! 복사 {result.Copied}개, 변경 없음 {result.Unchanged}개, 실패 {result.Failed}개");
        return result;
    }

    private BmsSyncResult SyncCore(string srcRoot, string dstRoot, CancellationToken ct)
    {
        var pngFiles = Directory.GetFiles(srcRoot, "*.png", SearchOption.AllDirectories);
        int total = pngFiles.Length;
        Log($"발견된 렌더 이미지: {total}개");
        ReportProgress(0, total);
        if (total == 0) return new BmsSyncResult(0, 0, 0);

        int copied = 0, unchanged = 0, failed = 0;
        int lastPct = -1;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = pngFiles[i];
            try
            {
                var dest = GetDestinationPath(srcRoot, dstRoot, file);
                if (IsUpToDate(file, dest))
                {
                    unchanged++;
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, overwrite: true);
                    copied++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                Log($"복사 실패: {Path.GetFileName(file)} - {ex.Message}");
            }

            // 파일마다 이벤트를 쏘지 않고 1% 단위로만 보고
            int pct = (i + 1) * 100 / total;
            if (pct != lastPct)
            {
                lastPct = pct;
                ReportProgress(i + 1, total);
            }
        }

        return new BmsSyncResult(copied, unchanged, failed);
    }

    internal static string GetDestinationPath(string srcRoot, string dstRoot, string file)
    {
        var fileName = Path.GetFileName(file);
        var match = MonsterNameRegex().Match(fileName);
        if (!match.Success)
        {
            return Path.Combine(dstRoot, Path.GetRelativePath(srcRoot, file));
        }

        var sceneNum = match.Groups[1].Value;
        var subNum = match.Groups[2].Value;
        var monsterCode = sceneNum + subNum;
        var sceneFolder = $"장면{sceneNum}";

        // 각 장면의 01~03번은 보스 기물
        return subNum is "01" or "02" or "03"
            ? Path.Combine(dstRoot, sceneFolder, "보스", monsterCode, fileName)
            : Path.Combine(dstRoot, sceneFolder, monsterCode, fileName);
    }

    /// <summary>File.Copy는 수정 시각을 보존하므로 크기+수정 시각이 같으면 이전 동기화 결과로 간주한다.</summary>
    private static bool IsUpToDate(string src, string dest)
    {
        var d = new FileInfo(dest);
        if (!d.Exists) return false;
        var s = new FileInfo(src);
        return s.Length == d.Length && s.LastWriteTimeUtc == d.LastWriteTimeUtc;
    }

    private static bool IsSameOrNested(string a, string b)
    {
        static bool IsUnder(string parent, string child) =>
            child.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return IsUnder(a, b) || IsUnder(b, a);
    }

    [GeneratedRegex(@"^([0-9]{2})([0-9]{2})(_[a-zA-Z0-9_]+)?\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex MonsterNameRegex();
}
