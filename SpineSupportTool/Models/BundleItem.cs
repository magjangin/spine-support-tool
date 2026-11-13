using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SpineSupportTool.Models;

public sealed partial class BundleItem : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public long FileSizeBytes { get; init; }
    public required string SceneTag { get; init; }

    /// <summary>바인딩마다 재계산하지 않도록 생성 시 한 번만 포맷한다.</summary>
    public string FileSizeFormatted { get; private init; } = string.Empty;

    /// <summary>"장면NN" 형식의 본편 장면(cy/fool 변형 포함) 여부.</summary>
    public bool IsMainScene => SceneTag.StartsWith("장면");

    /// <summary>만우절(fool) 변형 장면 여부.</summary>
    public bool IsFoolVariant => SceneTag.EndsWith("_fool");

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static BundleItem FromFile(string filePath)
    {
        var fi = new FileInfo(filePath);
        return new BundleItem
        {
            FilePath = filePath,
            FileName = fi.Name,
            FileSizeBytes = fi.Length,
            FileSizeFormatted = $"{fi.Length / (1024.0 * 1024.0):F2} MB",
            SceneTag = ExtractSceneTag(fi.Name),
        };
    }

    /// <summary>
    /// gamescene_01_assets_all_82f3... -> 장면01
    /// gamescene_01_cy_...            -> 장면01_cy
    /// gamescene_dave_assets_...      -> dave
    /// </summary>
    internal static string ExtractSceneTag(string fileName)
    {
        var match = SceneNumRegex().Match(fileName);
        if (match.Success)
        {
            return $"장면{match.Groups[1].Value}{match.Groups[2].Value.ToLowerInvariant()}";
        }

        var generalMatch = GeneralSceneRegex().Match(fileName);
        return generalMatch.Success ? generalMatch.Groups[1].Value : "gamescene";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    // 소스 생성기 정규식: 런타임 컴파일/JIT 비용 없이 시작 시점부터 최적화된 매처 사용
    [GeneratedRegex(@"gamescene_([0-9]{2})(_cy|_fool)?", RegexOptions.IgnoreCase)]
    private static partial Regex SceneNumRegex();

    [GeneratedRegex(@"gamescene_([a-zA-Z0-9_]+?)_assets", RegexOptions.IgnoreCase)]
    private static partial Regex GeneralSceneRegex();
}
