using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private readonly StorageService _storageService;
    private bool _saveBlocked;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WindowAnchor", "settings.json"),
            new StorageService())
    {
    }

    /// <summary>Creates a settings service that resolves workspace references through the supplied store.</summary>
    public SettingsService(StorageService storageService)
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WindowAnchor", "settings.json"),
            storageService)
    {
    }

    internal SettingsService(string settingsPath, StorageService storageService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = settingsPath;
        _storageService = storageService;
        Load();
    }

    /// <summary>
    /// Explains the most recent settings load failure, or <c>null</c> when loading succeeded.
    /// </summary>
    public string? LastLoadError { get; private set; }

    /// <summary>
    /// Indicates that saves are disabled to protect an unreadable or future-version document.
    /// </summary>
    public bool IsSaveBlocked => _saveBlocked;

    // ── Load ──────────────────────────────────────────────────────────────

    public void Load()
    {
        LastLoadError = null;
        _saveBlocked = false;

        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                int version = SettingsSchemaMigrator.ReadVersion(json);
                var workspaces = version < AppSettings.CurrentSchemaVersion
                    ? _storageService.LoadAllWorkspaces()
                    : new List<WorkspaceSnapshot>();
                var result = SettingsSchemaMigrator.Migrate(json, workspaces, JsonOpts);
                Settings = result.Value;
                if (result.WasMigrated)
                    _storageService.AtomicWriter.WriteAllText(_settingsPath, result.Json);
                AppLogger.Info(
                    "settings.loaded",
                    "Loaded application settings",
                    LogField.Public("schemaVersion", Settings.SchemaVersion));
            }
            else
            {
                Settings = new AppSettings();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "settings.load_failed",
                "Could not load application settings",
                ex,
                LogField.Path("settingsPath", _settingsPath),
                LogField.Public("errorCategory", "settings_load"));
            Settings = new AppSettings();
            LastLoadError = ex.Message;
            _saveBlocked = true;
        }
    }

    // ── Save ──────────────────────────────────────────────────────────────

    public void Save()
    {
        if (_saveBlocked)
        {
            AppLogger.Warn(
                "settings.save_blocked",
                "Settings save was blocked to protect an unreadable settings document",
                LogField.Path("settingsPath", _settingsPath),
                LogField.Public("errorCategory", "settings_unreadable"));
            return;
        }

        try
        {
            Settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            Settings.DefaultWorkspaceName = null;
            Settings.WorkspaceOrder = null;
            SettingsSchemaMigrator.Validate(Settings);
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            string json = JsonSerializer.Serialize(Settings, JsonOpts);
            _storageService.AtomicWriter.WriteAllText(_settingsPath, json);
            AppLogger.Info(
                "settings.saved",
                "Saved application settings",
                LogField.Public("schemaVersion", Settings.SchemaVersion));
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "settings.save_failed",
                "Could not save application settings",
                ex,
                LogField.Path("settingsPath", _settingsPath),
                LogField.Public("errorCategory", "settings_save"));
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

    // ── Learned window-match helpers ─────────────────────────────────────

    /// <summary>Returns learned hints for one stable workspace ID.</summary>
    public IReadOnlyList<WindowMatchHint> GetWindowMatchHints(string workspaceId) =>
        (Settings.WindowMatchHints ?? [])
            .Where(hint => hint.WorkspaceId.Equals(
                workspaceId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>Creates or replaces the learned composite identity for one workspace entry.</summary>
    public void RememberWindowMatch(
        string workspaceId,
        string entryId,
        WindowIdentityHint identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        ArgumentNullException.ThrowIfNull(identity);
        if (!Guid.TryParse(workspaceId, out _) || !Guid.TryParse(entryId, out _))
            throw new ArgumentException("Learned matches require stable workspace and entry GUIDs.");
        if (!WindowMatcher.MatchesHint(identity, identity))
            throw new ArgumentException(
                "A learned match requires an executable/class or stronger application identity anchor.",
                nameof(identity));

        Settings.WindowMatchHints ??= [];
        Settings.WindowMatchHints.RemoveAll(hint =>
            hint.WorkspaceId.Equals(workspaceId, StringComparison.OrdinalIgnoreCase) &&
            hint.EntryId.Equals(entryId, StringComparison.OrdinalIgnoreCase));
        Settings.WindowMatchHints.Add(new WindowMatchHint
        {
            WorkspaceId = workspaceId,
            EntryId = entryId,
            Identity = identity
        });
        Save();
    }

    /// <summary>Clears one learned workspace-entry match, returning whether it existed.</summary>
    public bool ClearWindowMatch(string workspaceId, string entryId)
    {
        int removed = Settings.WindowMatchHints?.RemoveAll(hint =>
            hint.WorkspaceId.Equals(workspaceId, StringComparison.OrdinalIgnoreCase) &&
            hint.EntryId.Equals(entryId, StringComparison.OrdinalIgnoreCase)) ?? 0;
        if (Settings.WindowMatchHints?.Count == 0)
            Settings.WindowMatchHints = null;
        if (removed > 0) Save();
        return removed > 0;
    }

    /// <summary>Clears every learned window-match hint and returns the number removed.</summary>
    public int ClearAllWindowMatches()
    {
        int count = Settings.WindowMatchHints?.Count ?? 0;
        if (count == 0) return 0;
        Settings.WindowMatchHints = null;
        Save();
        return count;
    }
}
