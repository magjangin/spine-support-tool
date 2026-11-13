using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using SpineSupportTool.Models;

namespace SpineSupportTool.Services;

public class ConfigService
{
    // 한글 경로가 유니코드 이스케이프(\uXXXX)로 저장되지 않게 해 사용자가 설정 파일을 직접 읽고 고치기 쉽게 한다.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _configPath;

    public AppConfig Config { get; private set; }

    /// <summary>마지막 로드/저장 중 발생한 문제(없으면 null). UI 로그에 표시한다.</summary>
    public string? LastError { get; private set; }

    public ConfigService(string? configPath = null)
    {
        _configPath = configPath ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Config = Load();
    }

    public AppConfig Load()
    {
        LastError = null;
        if (File.Exists(_configPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configPath), JsonOptions);
                if (loaded != null)
                {
                    Config = loaded;
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                // 손상된 설정을 조용히 덮어쓰지 않도록 백업해 둔다.
                var backup = _configPath + ".bak";
                try { File.Copy(_configPath, backup, overwrite: true); } catch { /* 백업 실패는 무시 */ }
                LastError = $"설정 파일을 읽지 못해 기본값으로 시작합니다 (백업: {backup}): {ex.Message}";
            }
        }

        Config = new AppConfig();
        Save();
        return Config;
    }

    public bool Save()
    {
        try
        {
            // 임시 파일에 쓴 뒤 교체해 저장 도중 종료돼도 설정 파일이 잘리지 않게 한다.
            var tmp = _configPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Config, JsonOptions));
            File.Move(tmp, _configPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"설정 저장 실패 ({_configPath}): {ex.Message}";
            return false;
        }
    }
}
