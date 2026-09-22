using System;
using System.IO;
using System.Text.Json;

namespace VUMixer;

/// <summary>
/// Persistent settings, stored as config.json next to the exe (portable app:
/// the app never writes outside its own folder).
/// </summary>
public sealed class AppConfig
{
    public const string BuildCode = "0.1.0-win-0001";

    /// <summary>Device ids (WASAPI endpoint ids) — empty = auto/first available.</summary>
    public string MicDevice { get; set; } = "";
    public string DeviceA { get; set; } = "";
    public string DeviceB { get; set; } = "";

    public double MicGain { get; set; } = 1.0;
    public bool MicMute { get; set; }
    public double SysGain { get; set; } = 1.0;
    public bool SysMute { get; set; }
    public double GainA { get; set; } = 1.0;
    public bool MuteA { get; set; }
    public double GainB { get; set; } = 1.0;
    public bool MuteB { get; set; }

    /// <summary>Mic sends: desktop always flows to both buses, the mic only to enabled ones.</summary>
    public bool MicToA { get; set; } = true;
    public bool MicToB { get; set; } = true;

    /// <summary>Noise reduction (v1 = noise gate).</summary>
    public bool NsEnabled { get; set; } = true;
    public double NsThresholdDb { get; set; } = -50.0;

    private static string Path_ => Path.Combine(AppContext.BaseDirectory, FileName);

    public const string FileName = "config.json";

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path_));
                if (cfg != null) return cfg;
            }
        }
        catch (Exception ex)
        {
            UiLog.Write("config: could not read config.json (" + ex.Message + ") — using defaults");
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            var tmp = Path_ + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Path_, true);
        }
        catch (Exception ex)
        {
            UiLog.Write("config: could not save config.json (" + ex.Message + ")");
        }
    }
}