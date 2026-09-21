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
        catch (Exception e)
        {
            // 配置损坏时静默回退会让用户设置"莫名丢失"，必须留痕
            Logger.Log($"配置读取失败，已使用默认值: {e.Message}");
        }
        return new AppConfig();
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // 原子替换：先写临时文件再 Move 覆盖。直接覆盖写一旦写到一半被杀/断电，
            // config.json 会变成截断的 JSON，下次 Load 走 catch 静默回默认值（表现为"设置莫名丢失"）
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, ConfigJsonContext.Default.AppConfig));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e)
        {
            Logger.Log($"配置保存失败: {e.Message}");
        }
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
        catch (Exception e)
        {
            Logger.Log($"写开机自启注册表失败: {e.Message}");
            return false;
        }
    }

    public static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch (Exception e)
        {
            Logger.Log($"读开机自启注册表失败: {e.Message}");
            return false;
        }
    }
}
