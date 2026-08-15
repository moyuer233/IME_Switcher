using System.Text.Json;
using System.Text.Json.Serialization;

namespace IMESwitcher;

/// <summary>应用配置</summary>
public sealed class AppConfig
{
    public string Hotkey { get; set; } = "caps lock";
    public string ToggleHotkey { get; set; } = "";
    public bool Autostart { get; set; }
    public int Method { get; set; } = 1; // 1=API, 2=模拟
    public bool StartToTray { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext { }

public static class Config
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IMESwitcher");
    private static readonly string FilePath = Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize(File.ReadAllText(FilePath), ConfigJsonContext.Default.AppConfig);
                if (cfg != null) return cfg;
            }
        }
        catch { }
        return new AppConfig();
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, ConfigJsonContext.Default.AppConfig));
        }
        catch { }
    }

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "IMESwitcher";

    public static bool SetAutostart(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return false;
            if (enabled)
                key.SetValue(ValueName, "\"" + Environment.ProcessPath + "\"");
            else
                key.DeleteValue(ValueName, false);
            return true;
        }
        catch { return false; }
    }

    public static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }
}
