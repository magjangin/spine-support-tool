using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SpineSupportTool.Models;

namespace SpineSupportTool.Services;

public class BmsSyncService
{
    private readonly ConfigService _configService;

    public event Action<string>? LogReceived;
    public event Action<int, int, double>? ProgressChanged; // (current, total, percentage)

    public BmsSyncService(ConfigService configService)
    {
        _configService = configService;
    }

    private void Log(string message) => LogReceived?.Invoke($"[BMS동기화] {message}");

    public async Task<int> SyncRenderedMonstersToBmsAsync(string renderedDir, string bmsDir, CancellationToken ct = default)
    {
        if (!Directory.Exists(renderedDir))
        {
            Log($"오류: 렌더링 폴더가 없습니다: {renderedDir}");
            return 0;
        }

        Directory.CreateDirectory(bmsDir);
        Log($"BMS 노트 이미지 동기화 시작: {renderedDir} -> {bmsDir}");

        int copiedCount = 0;

        await Task.Run(() =>
        {
            var pngFiles = Directory.GetFiles(renderedDir, "*.png", SearchOption.AllDirectories);
            int total = pngFiles.Length;
            Log($"발견된 렌더 이미지: {total}개");

            if (total == 0)
            {
                ProgressChanged?.Invoke(0, 0, 100.0);
                return;
            }

            ProgressChanged?.Invoke(0, total, 0.0);

            for (int i = 0; i < pngFiles.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = pngFiles[i];
                var fileName = Path.GetFileName(file);

                var match = Regex.Match(fileName, @"^([0-9]{2})([0-9]{2})(_[a-zA-Z0-9_]+)?\.png$", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var sceneNum = match.Groups[1].Value;
                    var monsterCode = match.Groups[1].Value + match.Groups[2].Value;

                    string sceneFolder = $"장면{sceneNum}";
                    string targetFolder;

                    int subNum = int.Parse(match.Groups[2].Value);
                    if (subNum <= 3)
                    {
                        targetFolder = Path.Combine(bmsDir, sceneFolder, "보스", monsterCode);
                    }
                    else
                    {
                        targetFolder = Path.Combine(bmsDir, sceneFolder, monsterCode);
                    }

                    Directory.CreateDirectory(targetFolder);
                    var destFile = Path.Combine(targetFolder, fileName);

                    File.Copy(file, destFile, overwrite: true);
                    copiedCount++;
                }
                else
                {
                    var relPath = Path.GetRelativePath(renderedDir, file);
                    var destFile = Path.Combine(bmsDir, relPath);
                    var dir = Path.GetDirectoryName(destFile);
                    if (dir != null) Directory.CreateDirectory(dir);

                    File.Copy(file, destFile, overwrite: true);
                    copiedCount++;
                }

                double pct = (double)(i + 1) / total * 100.0;
                ProgressChanged?.Invoke(i + 1, total, pct);
            }
        }, ct);

        Log($"BMS 노트 스킨 동기화 완료! 총 {copiedCount}개 파일 정리됨.");
        return copiedCount;
    }
}
