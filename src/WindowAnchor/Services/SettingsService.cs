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

    // ── Monitor alias helpers ─────────────────────────────────────────────

    /// <summary>
    /// Returns the user-defined alias for <paramref name="monitorId"/>,
    /// or <paramref name="fallbackName"/> when no alias is set.
    /// </summary>
    public string ResolveMonitorName(string monitorId, string fallbackName)
    {
        if (Settings.MonitorAliases != null &&
            Settings.MonitorAliases.TryGetValue(monitorId, out var alias) &&
            !string.IsNullOrWhiteSpace(alias))
            return alias;
        return fallbackName;
    }

    /// <summary>Sets or removes a monitor alias and persists immediately.</summary>
    public void SetMonitorAlias(string monitorId, string? alias)
    {
        Settings.MonitorAliases ??= new();
        if (string.IsNullOrWhiteSpace(alias))
            Settings.MonitorAliases.Remove(monitorId);
        else
            Settings.MonitorAliases[monitorId] = alias.Trim();

        // Clean up empty dictionary
        if (Settings.MonitorAliases.Count == 0)
            Settings.MonitorAliases = null;

        Save();
    }
}
