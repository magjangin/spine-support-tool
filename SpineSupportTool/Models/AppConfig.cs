using System;
using System.IO;
using System.Text.Json;

namespace SpineSupportTool.Models;

public class AppConfig
{
    public string MuseDashPath { get; set; } = @"H:\muse dash hwa";
    public string AssetStudioCliPath { get; set; } = @"H:\AssetStudioMod.v0.19.0\cli\AssetStudioModCLI_net9_win64\AssetStudioModCLI.exe";
    public string SpineHwaPath { get; set; } = @"H:\source\repos\spine hwa";
    public string WorkOutputDir { get; set; } = @"H:\유튜브 곡 모음\음악원본 저작권 O\뮤즈대쉬";
    public string BmsNoteSkinDir { get; set; } = @"H:\유튜브 곡 모음\음악원본 저작권 O\뮤즈대쉬\bms 에디터 노트 이미지";
    public string GithubRepoUrl { get; set; } = "https://github.com/magjangin/spine-hwa";

    public string StreamingAssetsPath => Path.Combine(MuseDashPath, @"MuseDash_Data\StreamingAssets\aa\StandaloneWindows64");
    public string ExtractedOutputDir => Path.Combine(WorkOutputDir, "gamescene_추출");
    public string RenderOutputDir => Path.Combine(WorkOutputDir, "몬스터_렌더_sv");

    public string SpineViewerExe => Path.Combine(SpineHwaPath, @"bin\SpineViewer.exe");
    public string SvCliExe => Path.Combine(SpineHwaPath, @"bin\sv-cli.exe");
}
