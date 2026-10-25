using System;
using System.IO;
using System.Text.Json.Serialization;

namespace SpineSupportTool.Models;

public class AppConfig
{
    public string MuseDashPath { get; set; } = string.Empty;
    public string AssetStudioCliPath { get; set; } = string.Empty;
    public string SpineHwaPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "spine-hwa");
    public string WorkOutputDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SpineSupportTool", "Workspace");
    public string BmsNoteSkinDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SpineSupportTool", "BmsSkin");
    public string GithubRepoUrl { get; set; } = "https://github.com/magjangin/spine-hwa";

    [JsonIgnore]
    public string StreamingAssetsPath => !string.IsNullOrWhiteSpace(MuseDashPath)
        ? Path.Combine(MuseDashPath, @"MuseDash_Data\StreamingAssets\aa\StandaloneWindows64")
        : string.Empty;

    [JsonIgnore]
    public string ExtractedOutputDir => !string.IsNullOrWhiteSpace(WorkOutputDir)
        ? Path.Combine(WorkOutputDir, "gamescene_추출")
        : string.Empty;

    [JsonIgnore]
    public string RenderOutputDir => !string.IsNullOrWhiteSpace(WorkOutputDir)
        ? Path.Combine(WorkOutputDir, "몬스터_렌더_sv")
        : string.Empty;

    [JsonIgnore]
    public string SpineViewerExe => !string.IsNullOrWhiteSpace(SpineHwaPath)
        ? Path.Combine(SpineHwaPath, @"bin\SpineViewer.exe")
        : string.Empty;

    [JsonIgnore]
    public string SvCliExe => !string.IsNullOrWhiteSpace(SpineHwaPath)
        ? Path.Combine(SpineHwaPath, @"bin\sv-cli.exe")
        : string.Empty;
}
