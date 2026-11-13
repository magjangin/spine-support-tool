using System;
using System.IO;
using System.Text.Json.Serialization;

namespace SpineSupportTool.Models;

public class AppConfig
{
    /// <summary>작업 폴더 아래 번들 추출 결과가 모이는 하위 폴더 이름.</summary>
    public const string ExtractedFolderName = "gamescene_추출";

    /// <summary>작업 폴더 아래 sv-cli 렌더 결과가 모이는 하위 폴더 이름.</summary>
    public const string RenderFolderName = "몬스터_렌더_sv";

    private static readonly string DocumentsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public string MuseDashPath { get; set; } = string.Empty;
    public string AssetStudioCliPath { get; set; } = string.Empty;
    public string SpineHwaPath { get; set; } = Path.Combine(DocumentsDir, "spine-hwa");
    public string WorkOutputDir { get; set; } = Path.Combine(DocumentsDir, "SpineSupportTool", "Workspace");
    public string BmsNoteSkinDir { get; set; } = Path.Combine(DocumentsDir, "SpineSupportTool", "BmsSkin");
    public string GithubRepoUrl { get; set; } = "https://github.com/magjangin/spine-hwa";

    /// <summary>번들 추출/렌더링 동시 실행 프로세스 수. 0 이하이면 CPU 코어 수 기준 자동.</summary>
    public int MaxParallelJobs { get; set; }

    [JsonIgnore]
    public int EffectiveParallelJobs => ResolveParallelJobs(MaxParallelJobs);

    [JsonIgnore]
    public string StreamingAssetsPath => CombineIfSet(MuseDashPath, @"MuseDash_Data\StreamingAssets\aa\StandaloneWindows64");

    [JsonIgnore]
    public string ExtractedOutputDir => GetExtractedDir(WorkOutputDir);

    [JsonIgnore]
    public string RenderOutputDir => GetRenderDir(WorkOutputDir);

    [JsonIgnore]
    public string SpineViewerExe => CombineIfSet(SpineHwaPath, @"bin\SpineViewer.exe");

    [JsonIgnore]
    public string SvCliExe => CombineIfSet(SpineHwaPath, @"bin\sv-cli.exe");

    public static string GetExtractedDir(string? workDir) => CombineIfSet(workDir, ExtractedFolderName);

    public static string GetRenderDir(string? workDir) => CombineIfSet(workDir, RenderFolderName);

    /// <summary>요청 값이 0 이하이면 코어 수의 절반(1~6)을 사용한다. 외부 프로세스는 각자 멀티스레드를 쓰므로 코어 전부를 쓰지 않는다.</summary>
    public static int ResolveParallelJobs(int requested) =>
        requested > 0 ? Math.Min(requested, 32) : Math.Clamp(Environment.ProcessorCount / 2, 1, 6);

    private static string CombineIfSet(string? root, string relative) =>
        string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine(root, relative);
}
