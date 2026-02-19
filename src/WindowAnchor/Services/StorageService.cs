using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

public class StorageService
{
    private readonly string _baseDir;
    private readonly string _workspacesDir;
    private readonly string _legacyProfilesDir;   // kept solely for v2 migration
    private readonly string _lastFingerprintFile;
    // Spec: WriteIndented = true, CamelCase naming policy
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public StorageService()
    {
        _baseDir             = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowAnchor");
        _workspacesDir       = Path.Combine(_baseDir, "workspaces");
        _legacyProfilesDir   = Path.Combine(_baseDir, "profiles");
        _lastFingerprintFile = Path.Combine(_baseDir, "last_fingerprint.txt");

        EnsureDirectories();
        MigrateToV2();
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_baseDir);
        Directory.CreateDirectory(_workspacesDir);
    }

    // ── v2 Migration ─────────────────────────────────────────────────────────

    /// <summary>
    /// One-time migration: converts legacy "Monitor Profile" JSON files (*.profile.json)
    /// into Workspace snapshots and writes a sentinel file so this only runs once.
    /// </summary>
    private void MigrateToV2()
    {
        string sentinel = Path.Combine(_baseDir, ".migrated_v2");
        if (File.Exists(sentinel)) return;

        AppLogger.Info("StorageService: running v2 migration");

        if (Directory.Exists(_legacyProfilesDir))
        {
            foreach (var file in Directory.GetFiles(_legacyProfilesDir, "*.profile.json"))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var profile = JsonSerializer.Deserialize<LegacyMonitorProfile>(json, JsonOpts);
                    if (profile == null) continue;

                    string name = string.IsNullOrWhiteSpace(profile.DisplayName)
                        ? $"Monitor Config {profile.Fingerprint[..Math.Min(6, profile.Fingerprint.Length)]}"
                        : profile.DisplayName;

                    var entries = (profile.Windows ?? new List<WindowRecord>())
                        .Select(w => new WorkspaceEntry
                        {
                            ExecutablePath  = w.ExecutablePath,
                            ProcessName     = w.ProcessName,
                            WindowClassName = w.ClassName,
                            FilePath        = null,
                            FileConfidence  = 0,
                            FileSource      = "NONE",
                            LaunchArg       = null,
                            Position        = w,
                            MonitorId       = "",
                            MonitorIndex    = 0,
                            MonitorName     = "",
                        })
                        .ToList();

                    var snapshot = new WorkspaceSnapshot
                    {
                        Name               = name,
                        MonitorFingerprint = profile.Fingerprint,
                        SavedAt            = profile.LastSaved,
                        SavedWithFiles     = false,
                        Monitors           = new List<MonitorInfo>(),
                        Entries            = entries,
                    };

                    SaveWorkspace(snapshot);
                    AppLogger.Info($"Migrated profile \u2018{profile.DisplayName}\u2019 \u2192 workspace \u2018{name}\u2019 ({entries.Count} windows)");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Migration: skipping \u2018{file}\u2019: {ex.Message}");
                }
            }
        }

        File.WriteAllText(sentinel, "");
        AppLogger.Info("StorageService: v2 migration complete");
    }

    // ── Last-Known Fingerprint ────────────────────────────────────────────────

    /// <summary>Returns the fingerprint from the last successful save/restore, or "".</summary>
    public string GetLastKnownFingerprint()
    {
        try
        {
            return File.Exists(_lastFingerprintFile)
                ? File.ReadAllText(_lastFingerprintFile).Trim()
                : "";
        }
        catch { return ""; }
    }

    /// <summary>Persists the current fingerprint so it survives app restarts.</summary>
    public void SetLastKnownFingerprint(string fingerprint)
    {
        try { File.WriteAllText(_lastFingerprintFile, fingerprint); }
        catch { }
    }

    // ── Workspace Snapshots ───────────────────────────────────────────────────

    public void SaveWorkspace(WorkspaceSnapshot snapshot)
    {
        string sanitized = string.Concat(snapshot.Name.Split(Path.GetInvalidFileNameChars()));
        string path = Path.Combine(_workspacesDir, $"{sanitized}.workspace.json");
        string json = JsonSerializer.Serialize(snapshot, JsonOpts);
        File.WriteAllText(path, json);
    }

    public List<WorkspaceSnapshot> LoadAllWorkspaces()
    {
        var list = new List<WorkspaceSnapshot>();
        if (!Directory.Exists(_workspacesDir)) return list;

        foreach (var file in Directory.GetFiles(_workspacesDir, "*.workspace.json"))
        {
            try
            {
                string json = File.ReadAllText(file);
                var snapshot = JsonSerializer.Deserialize<WorkspaceSnapshot>(json, JsonOpts);
                if (snapshot != null) list.Add(snapshot);
            }
            catch { }
        }
        return list;
    }

    public void RenameWorkspace(WorkspaceSnapshot snapshot, string newName)
    {
        // Delete old file
        string oldSanitized = string.Concat(snapshot.Name.Split(Path.GetInvalidFileNameChars()));
        string oldPath = Path.Combine(_workspacesDir, $"{oldSanitized}.workspace.json");
        if (File.Exists(oldPath)) File.Delete(oldPath);

        // Save with new name
        snapshot.Name = newName;
        SaveWorkspace(snapshot);
    }

    public void DeleteWorkspace(WorkspaceSnapshot snapshot)
    {
        string sanitized = string.Concat(snapshot.Name.Split(Path.GetInvalidFileNameChars()));
        string path = Path.Combine(_workspacesDir, $"{sanitized}.workspace.json");
        if (File.Exists(path)) File.Delete(path);
    }

    public void DeleteWorkspace(string name)
    {
        string sanitized = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        string path = Path.Combine(_workspacesDir, $"{sanitized}.workspace.json");
        if (File.Exists(path)) File.Delete(path);
    }

    // ── Private: legacy deserialization only used during migration ────────────

    private sealed class LegacyMonitorProfile
    {
        public string             Fingerprint { get; set; } = "";
        public string             DisplayName { get; set; } = "";
        public DateTime           LastSaved   { get; set; }
        public List<WindowRecord> Windows     { get; set; } = new();
    }
}