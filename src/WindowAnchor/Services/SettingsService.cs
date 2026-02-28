using System;
using System.IO;
using System.Text.Json;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Loads and persists <see cref="AppSettings"/> to
/// <c>%AppData%\WindowAnchor\settings.json</c>.
/// </summary>
public class SettingsService
{
    private readonly string _settingsPath;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowAnchor", "settings.json");
        Load();
    }

    // ── Load ──────────────────────────────────────────────────────────────

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
                AppLogger.Info("SettingsService: loaded settings");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"SettingsService: failed to load settings — {ex.Message}");
            Settings = new AppSettings();
        }
    }

    // ── Save ──────────────────────────────────────────────────────────────

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            string json = JsonSerializer.Serialize(Settings, JsonOpts);
            File.WriteAllText(_settingsPath, json);
            AppLogger.Info("SettingsService: saved settings");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"SettingsService: failed to save settings — {ex.Message}");
        }
    }
}
