using System.IO;
using System.Text.Json;

namespace DeskTwin.Core;

public sealed class DesktopSettings
{
    public int DesktopWidth { get; set; } = 1920;
    public int DesktopHeight { get; set; } = 1080;
    /// <summary>true：自适应模式（分辨率随窗口变化）；false：缩放模式（固定分辨率、等比缩放）。</summary>
    public bool FitToWindow { get; set; } = true;
    /// <summary>缩放模式下会话缩放（100/140/180）；0 表示跟随系统 DPI。</summary>
    public int SessionScaleFactor { get; set; }

    /// <summary>启动时不再弹出环境风险提示。</summary>
    public bool SkipStartupWarning { get; set; }
    public bool Topmost { get; set; }
    public bool AudioMuted { get; set; }
    public bool SendSystemShortcuts { get; set; } = true;
    public bool WatchMode { get; set; }
}

public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskTwin",
        "settings.json");

    public static DesktopSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new DesktopSettings();
            }

            var settings = JsonSerializer.Deserialize<DesktopSettings>(
                File.ReadAllText(SettingsPath), JsonOptions) ?? new DesktopSettings();
            if (settings.DesktopWidth is < 200 or > 8192)
            {
                settings.DesktopWidth = 1920;
            }

            if (settings.DesktopHeight is < 200 or > 8192)
            {
                settings.DesktopHeight = 1080;
            }

            return settings;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DesktopSettings();
        }
    }

    public static void Save(DesktopSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("无法确定配置目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, true);
    }
}
