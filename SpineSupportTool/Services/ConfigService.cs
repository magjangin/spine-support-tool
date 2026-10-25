using System;
using System.IO;
using System.Text.Json;
using SpineSupportTool.Models;

namespace SpineSupportTool.Services;

public class ConfigService
{
    private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppConfig Config { get; private set; }

    public ConfigService()
    {
        Config = Load();
    }

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded != null)
                {
                    Config = loaded;
                    return loaded;
                }
            }
        }
        catch
        {
            // fallback to default
        }

        Config = new AppConfig();
        Save();
        return Config;
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // ignore save failure
        }
    }
}
