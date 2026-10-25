using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SpineSupportTool.Models;

public class BundleItem : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string SceneTag { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public string FileSizeFormatted => $"{FileSizeBytes / (1024.0 * 1024.0):F2} MB";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public static BundleItem FromFile(string filePath)
    {
        var fi = new FileInfo(filePath);
        var fileName = fi.Name;
        var sceneTag = ExtractSceneTag(fileName);
        var displayName = $"{sceneTag} ({fileName})";

        return new BundleItem
        {
            FilePath = filePath,
            FileName = fileName,
            FileSizeBytes = fi.Length,
            SceneTag = sceneTag,
            DisplayName = displayName,
            IsSelected = true
        };
    }

    private static string ExtractSceneTag(string fileName)
    {
        // gamescene_01_assets_all_82f3... -> 장면01
        // gamescene_01_cy_... -> 장면01_cy
        // gamescene_dave_... -> dave
        var match = Regex.Match(fileName, @"gamescene_([0-9]{2})(_cy|_fool)?", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var num = match.Groups[1].Value;
            var sub = match.Groups[2].Success ? match.Groups[2].Value : "";
            return $"장면{num}{sub}";
        }

        var generalMatch = Regex.Match(fileName, @"gamescene_([a-zA-Z0-9_]+?)_assets", RegexOptions.IgnoreCase);
        if (generalMatch.Success)
        {
            return generalMatch.Groups[1].Value;
        }

        return "gamescene";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
