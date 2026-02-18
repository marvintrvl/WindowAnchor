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
    private readonly string _profilesDir;
    private readonly string _lastFingerprintFile;

    // Spec: WriteIndented = true, CamelCase naming policy
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public StorageService()
    {
        _baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowAnchor");
        _workspacesDir = Path.Combine(_baseDir, "workspaces");
        _profilesDir = Path.Combine(_baseDir, "profiles");
        _lastFingerprintFile = Path.Combine(_baseDir, "last_fingerprint.txt");

        EnsureDirectories();
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_baseDir);
        Directory.CreateDirectory(_workspacesDir);
        Directory.CreateDirectory(_profilesDir);
    }

    // ── Monitor Profile ───────────────────────────────────────────────────────

    public void SaveProfile(MonitorProfile profile)
    {
        // Each profile gets its own file keyed by its GUID Id
        string path = Path.Combine(_profilesDir, $"{profile.Id}.profile.json");
        string json = JsonSerializer.Serialize(profile, JsonOpts);
        File.WriteAllText(path, json);
    }

    /// <summary>Returns the most recently saved profile for <paramref name="fingerprint"/>, or null.</summary>
    public MonitorProfile? LoadProfile(string fingerprint)
    {
        return LoadAllProfiles()
            .Where(p => p.Fingerprint == fingerprint)
            .OrderByDescending(p => p.LastSaved)
            .FirstOrDefault();
    }

    public bool HasProfile(string fingerprint)
        => LoadAllProfiles().Any(p => p.Fingerprint == fingerprint);

    public List<MonitorProfile> LoadAllProfiles()
    {
        var list = new List<MonitorProfile>();
        if (!Directory.Exists(_profilesDir)) return list;

        foreach (var file in Directory.GetFiles(_profilesDir, "*.profile.json"))
        {
            try
            {
                string json = File.ReadAllText(file);
                var profile = JsonSerializer.Deserialize<MonitorProfile>(json, JsonOpts);
                if (profile != null)
                {
                    // Back-fill Id from filename if missing (migration)
                    if (string.IsNullOrEmpty(profile.Id))
                        profile.Id = Path.GetFileNameWithoutExtension(
                            Path.GetFileNameWithoutExtension(file));
                    list.Add(profile);
                }
            }
            catch { }
        }
        return list;
    }

    public void DeleteProfile(MonitorProfile profile)
    {
        string path = Path.Combine(_profilesDir, $"{profile.Id}.profile.json");
        if (File.Exists(path)) File.Delete(path);
    }

    public void RenameProfile(MonitorProfile profile, string newDisplayName)
    {
        profile.DisplayName = newDisplayName;
        SaveProfile(profile);
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
}
