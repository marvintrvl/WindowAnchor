using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Compatibility façade for machine-local persistence. Permanent workspaces, recovery
/// checkpoints, and temporary captures are exposed as separate typed repositories.
/// </summary>
public class StorageService
{
    private readonly string _baseDir;
    private readonly string _legacyProfilesDir;
    private readonly string _lastFingerprintFile;
    private readonly List<StorageLoadIssue> _lastLoadIssues = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public StorageService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowAnchor"))
    {
    }

    internal StorageService(
        string baseDirectory,
        IAtomicFileWriter? atomicWriter = null,
        CheckpointRetentionPolicy? checkpointRetention = null,
        ICheckpointClock? checkpointClock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        _baseDir = baseDirectory;
        _legacyProfilesDir = Path.Combine(_baseDir, "profiles");
        _lastFingerprintFile = Path.Combine(_baseDir, "last_fingerprint.txt");
        AtomicWriter = atomicWriter ?? new AtomicFileWriter();

        Directory.CreateDirectory(_baseDir);
        NamedWorkspaces = new NamedWorkspaceRepository(
            Path.Combine(_baseDir, "workspaces"),
            AtomicWriter);
        Checkpoints = new CheckpointRepository(
            Path.Combine(_baseDir, "checkpoints"),
            AtomicWriter,
            checkpointRetention,
            checkpointClock);
        TemporaryCaptures = new TemporaryCaptureRepository(
            Path.Combine(_baseDir, "temporary-captures"),
            AtomicWriter);

        ImportLegacyProfiles();
    }

    /// <summary>Permanent named workspace storage.</summary>
    public NamedWorkspaceRepository NamedWorkspaces { get; }

    /// <summary>Recovery checkpoint storage, isolated from named workspaces.</summary>
    public CheckpointRepository Checkpoints { get; }

    /// <summary>Short-lived capture storage, isolated from named workspaces and checkpoints.</summary>
    public TemporaryCaptureRepository TemporaryCaptures { get; }

    /// <summary>Issues reported by the most recent named-workspace load.</summary>
    public IReadOnlyList<StorageLoadIssue> LastLoadIssues => _lastLoadIssues;

    internal IAtomicFileWriter AtomicWriter { get; }

    // ── Legacy profile import ─────────────────────────────────────────────

    /// <summary>
    /// One-time import of legacy Monitor Profile documents. The completion marker is committed
    /// only after every source has produced a durable named-workspace document.
    /// </summary>
    private void ImportLegacyProfiles()
    {
        string sentinel = Path.Combine(_baseDir, ".migrated_v2");
        if (File.Exists(sentinel))
            return;

        AppLogger.Info(
            "storage.legacy_import_started",
            "Importing legacy monitor profiles");
        bool allSucceeded = true;

        if (Directory.Exists(_legacyProfilesDir))
        {
            foreach (string file in Directory.GetFiles(_legacyProfilesDir, "*.profile.json")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var profile = JsonSerializer.Deserialize<LegacyMonitorProfile>(json, JsonOptions);
                    if (profile == null)
                        throw new InvalidDataException("Legacy profile deserialized to null.");

                    string name = string.IsNullOrWhiteSpace(profile.DisplayName)
                        ? $"Monitor Config {profile.Fingerprint[..Math.Min(6, profile.Fingerprint.Length)]}"
                        : profile.DisplayName;

                    var snapshot = WorkspaceSchemaMigrator.CreateFromLegacyProfile(
                        file,
                        name,
                        profile.Fingerprint,
                        profile.LastSaved,
                        profile.Windows ?? new List<WindowRecord>());

                    NamedWorkspaces.Save(snapshot);
                    AppLogger.Info(
                        "storage.legacy_profile_imported",
                        "Migrated a legacy monitor profile",
                        LogField.Workspace("legacyProfileName", profile.DisplayName),
                        LogField.Workspace("workspaceName", name),
                        LogField.Public("windowCount", snapshot.Entries.Count));
                }
                catch (Exception ex)
                {
                    allSucceeded = false;
                    AppLogger.Warn(
                        "storage.legacy_profile_import_failed",
                        "Skipped a legacy monitor profile during import",
                        ex,
                        LogField.Path("profilePath", file),
                        LogField.Public("errorCategory", "legacy_profile_import"));
                }
            }
        }

        if (allSucceeded)
        {
            AtomicWriter.WriteAllText(sentinel, "");
            AppLogger.Info(
                "storage.legacy_import_completed",
                "Legacy monitor profile import completed");
        }
        else
        {
            AppLogger.Warn(
                "storage.legacy_import_incomplete",
                "Legacy monitor profile import is incomplete; failed profiles will be retried",
                LogField.Public("errorCategory", "legacy_profile_import"));
        }
    }

    // ── Last-known monitor fingerprint ───────────────────────────────────

    public string GetLastKnownFingerprint()
    {
        try
        {
            return File.Exists(_lastFingerprintFile)
                ? File.ReadAllText(_lastFingerprintFile).Trim()
                : "";
        }
        catch
        {
            return "";
        }
    }

    public void SetLastKnownFingerprint(string fingerprint)
    {
        try
        {
            AtomicWriter.WriteAllText(_lastFingerprintFile, fingerprint);
        }
        catch
        {
        }
    }

    // ── Named-workspace compatibility API ────────────────────────────────

    public void SaveWorkspace(WorkspaceSnapshot snapshot) => NamedWorkspaces.Save(snapshot);

    /// <summary>Returns healthy named workspaces together with structured load failures.</summary>
    public WorkspaceLoadResult LoadNamedWorkspaces()
    {
        WorkspaceLoadResult result = NamedWorkspaces.Load();
        _lastLoadIssues.Clear();
        _lastLoadIssues.AddRange(result.Issues);

        foreach (StorageLoadIssue issue in result.Issues)
        {
            AppLogger.Warn(
                "storage.document_load_failed",
                "Skipped an unreadable storage document",
                LogField.Public("artifactKind", issue.ArtifactKind),
                LogField.Path("documentPath", issue.FilePath),
                LogField.Public("errorCategory", issue.FailureKind),
                LogField.Exception("detail", issue.Message));
        }

        return result;
    }

    /// <summary>
    /// Compatibility helper returning only healthy permanent workspaces. Call
    /// <see cref="LoadNamedWorkspaces"/> when structured failures are required.
    /// </summary>
    public List<WorkspaceSnapshot> LoadAllWorkspaces() =>
        LoadNamedWorkspaces().Workspaces.ToList();

    public void RenameWorkspace(WorkspaceSnapshot snapshot, string newName) =>
        NamedWorkspaces.Rename(snapshot, newName);

    public void DeleteWorkspace(WorkspaceSnapshot snapshot) =>
        NamedWorkspaces.Delete(snapshot);

    private sealed class LegacyMonitorProfile
    {
        public string Fingerprint { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public DateTime LastSaved { get; set; }
        public List<WindowRecord> Windows { get; set; } = new();
    }
}
