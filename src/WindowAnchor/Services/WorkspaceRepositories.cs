using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Physical repository containing a persisted workspace-shaped artifact.</summary>
public enum WorkspaceArtifactKind
{
    NamedWorkspace,
    Checkpoint,
    TemporaryCapture,
}

/// <summary>Machine-readable category for a document that could not be loaded.</summary>
public enum StorageLoadFailureKind
{
    CorruptJson,
    UnsupportedSchemaVersion,
    Validation,
    Io,
    Unknown,
}

/// <summary>Describes one isolated persistence failure.</summary>
/// <param name="ArtifactKind">Repository in which the document was found.</param>
/// <param name="FailureKind">Machine-readable failure classification.</param>
/// <param name="FilePath">Path of the affected document.</param>
/// <param name="Message">Diagnostic explaining why the document was skipped.</param>
public sealed record StorageLoadIssue(
    WorkspaceArtifactKind ArtifactKind,
    StorageLoadFailureKind FailureKind,
    string FilePath,
    string Message);

/// <summary>Healthy documents and isolated failures produced by one repository scan.</summary>
public sealed record WorkspaceLoadResult(
    IReadOnlyList<WorkspaceSnapshot> Workspaces,
    IReadOnlyList<StorageLoadIssue> Issues);

/// <summary>
/// Base for directory-scoped workspace artifact repositories. Each concrete repository scans
/// only its own directory and extension, which keeps future retention operations type-safe.
/// </summary>
public abstract class WorkspaceRepository
{
    private readonly string _directory;
    private readonly string _extension;
    private readonly IAtomicFileWriter _writer;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private protected WorkspaceRepository(
        string directory,
        string extension,
        WorkspaceArtifactKind artifactKind,
        IAtomicFileWriter writer)
    {
        _directory = directory;
        _extension = extension;
        ArtifactKind = artifactKind;
        _writer = writer;
        Directory.CreateDirectory(_directory);
    }

    public WorkspaceArtifactKind ArtifactKind { get; }

    /// <summary>Loads healthy artifacts and reports every isolated failure.</summary>
    public WorkspaceLoadResult Load()
    {
        var workspaces = new List<WorkspaceSnapshot>();
        var issues = new List<StorageLoadIssue>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string sourcePath in EnumerateDocumentPaths())
        {
            // A previous legacy file may have been removed after its canonical copy was committed.
            if (!File.Exists(sourcePath))
                continue;

            try
            {
                var result = Read(sourcePath);
                string canonicalPath = GetCanonicalPath(result.Value.WorkspaceId);
                WorkspaceSnapshot loaded = result.Value;

                if (!PathsEqual(sourcePath, canonicalPath))
                {
                    if (File.Exists(canonicalPath))
                    {
                        var canonical = Read(canonicalPath);
                        if (!canonical.Value.WorkspaceId.Equals(
                                result.Value.WorkspaceId,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                $"Storage identity conflict for workspace '{result.Value.WorkspaceId}'.");
                        }

                        if (canonical.WasMigrated)
                            _writer.WriteAllText(canonicalPath, canonical.Json);
                        loaded = canonical.Value;
                    }
                    else
                    {
                        // Commit the ID-addressed copy before removing the legacy name-addressed copy.
                        _writer.WriteAllText(canonicalPath, result.Json);
                    }

                    TryDeleteLegacySource(sourcePath, issues);
                }
                else if (result.WasMigrated)
                {
                    _writer.WriteAllText(canonicalPath, result.Json);
                }

                if (seenIds.Add(loaded.WorkspaceId))
                    workspaces.Add(loaded);
            }
            catch (Exception ex)
            {
                issues.Add(CreateIssue(sourcePath, ex));
            }
        }

        return new WorkspaceLoadResult(workspaces, issues);
    }

    /// <summary>Saves an artifact under its stable workspace ID.</summary>
    public virtual void Save(WorkspaceSnapshot snapshot) => SaveCanonical(snapshot);

    /// <summary>Deletes the artifact with the supplied stable ID from this repository only.</summary>
    public virtual void Delete(string workspaceId)
    {
        string path = GetCanonicalPath(workspaceId);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Deletes the supplied artifact from this repository only.</summary>
    public void Delete(WorkspaceSnapshot snapshot) => Delete(snapshot.WorkspaceId);

    private protected void SaveCanonical(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.SchemaVersion = WorkspaceSnapshot.CurrentSchemaVersion;
        WorkspaceSchemaMigrator.Validate(snapshot);
        string json = JsonSerializer.Serialize(snapshot, JsonOptions);
        _writer.WriteAllText(GetCanonicalPath(snapshot.WorkspaceId), json);
    }

    private protected MigratedDocument<WorkspaceSnapshot> Read(string path)
    {
        string json = File.ReadAllText(path);
        return WorkspaceSchemaMigrator.Migrate(json, Path.GetFileName(path), JsonOptions);
    }

    private protected IEnumerable<string> EnumerateDocumentPaths() =>
        Directory.GetFiles(_directory, $"*{_extension}")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

    private protected string GetCanonicalPath(string workspaceId)
    {
        if (!Guid.TryParse(workspaceId, out Guid parsed))
            throw new InvalidDataException("WorkspaceId must be a GUID.");
        return Path.Combine(_directory, $"{parsed:D}{_extension}");
    }

    private protected static bool TryReadName(string path, out string name)
    {
        name = "";
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root?["name"] is JsonValue value &&
                value.TryGetValue<string>(out string? persistedName))
            {
                name = persistedName ?? "";
                return true;
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private void TryDeleteLegacySource(string sourcePath, List<StorageLoadIssue> issues)
    {
        try
        {
            File.Delete(sourcePath);
        }
        catch (Exception ex)
        {
            issues.Add(CreateIssue(sourcePath, ex));
        }
    }

    private StorageLoadIssue CreateIssue(string path, Exception exception) =>
        new(
            ArtifactKind,
            exception switch
            {
                JsonException => StorageLoadFailureKind.CorruptJson,
                UnsupportedSchemaVersionException => StorageLoadFailureKind.UnsupportedSchemaVersion,
                InvalidDataException => StorageLoadFailureKind.Validation,
                IOException => StorageLoadFailureKind.Io,
                UnauthorizedAccessException => StorageLoadFailureKind.Io,
                _ => StorageLoadFailureKind.Unknown,
            },
            path,
            exception.Message);

    private static bool PathsEqual(string first, string second) =>
        Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Permanent user-named workspaces stored under <c>workspaces/</c>.</summary>
public sealed class NamedWorkspaceRepository : WorkspaceRepository
{
    internal NamedWorkspaceRepository(string directory, IAtomicFileWriter writer)
        : base(directory, ".workspace.json", WorkspaceArtifactKind.NamedWorkspace, writer)
    {
    }

    /// <summary>
    /// Saves by stable ID. A newly captured object with an existing display name adopts the
    /// existing document's ID so ordinary recapture remains an update rather than a duplicate.
    /// </summary>
    public override void Save(WorkspaceSnapshot snapshot)
    {
        string canonicalPath = GetCanonicalPath(snapshot.WorkspaceId);
        string? legacyOrExistingPath = null;

        if (!File.Exists(canonicalPath))
        {
            foreach (string path in EnumerateDocumentPaths())
            {
                if (!TryReadName(path, out string persistedName) ||
                    !persistedName.Equals(snapshot.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Full migration/validation is deliberate: a matching future or invalid document
                // must not be shadowed by a newly saved current-version document.
                var existing = Read(path);
                snapshot.WorkspaceId = existing.Value.WorkspaceId;
                legacyOrExistingPath = path;
                canonicalPath = GetCanonicalPath(snapshot.WorkspaceId);
                break;
            }
        }

        SaveCanonical(snapshot);

        // A durable ID-addressed replacement now exists, so the legacy filename is no longer the
        // only good copy. Canonical files are never deleted here.
        if (legacyOrExistingPath != null &&
            !Path.GetFullPath(legacyOrExistingPath).Equals(
                Path.GetFullPath(canonicalPath),
                StringComparison.OrdinalIgnoreCase) &&
            File.Exists(legacyOrExistingPath))
        {
            File.Delete(legacyOrExistingPath);
        }
    }

    /// <summary>Atomically changes display-name metadata without changing storage identity.</summary>
    public void Rename(WorkspaceSnapshot snapshot, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        string previousName = snapshot.Name;
        snapshot.Name = newName;
        try
        {
            SaveCanonical(snapshot);
        }
        catch
        {
            snapshot.Name = previousName;
            throw;
        }
    }
}

/// <summary>Recovery checkpoints stored exclusively under <c>checkpoints/</c>.</summary>
public sealed class CheckpointRepository : WorkspaceRepository
{
    private readonly string _directory;
    private readonly IAtomicFileWriter _writer;
    private readonly CheckpointRetentionPolicy _retention;
    private readonly ICheckpointClock _clock;
    private readonly object _sync = new();
    private static readonly JsonSerializerOptions IndexJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    static CheckpointRepository() =>
        IndexJsonOptions.Converters.Add(new JsonStringEnumConverter());

    internal CheckpointRepository(
        string directory,
        IAtomicFileWriter writer,
        CheckpointRetentionPolicy? retention = null,
        ICheckpointClock? clock = null)
        : base(directory, ".checkpoint.json", WorkspaceArtifactKind.Checkpoint, writer)
    {
        _directory = directory;
        _writer = writer;
        _retention = retention ?? new CheckpointRetentionPolicy();
        _clock = clock ?? new SystemCheckpointClock();
        if (_retention.MaximumCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(retention), "Checkpoint count must be positive.");
        if (_retention.MaximumAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention), "Checkpoint age must be positive.");
    }

    public override void Save(WorkspaceSnapshot snapshot) =>
        Save(snapshot, WorkspaceCheckpointTrigger.ManualCapture, "");

    /// <summary>
    /// Atomically commits a versioned checkpoint, then applies bounded retention and refreshes the
    /// reconstructable metadata index. A committed payload remains usable if index maintenance
    /// subsequently fails.
    /// </summary>
    public CheckpointSaveReceipt Save(
        WorkspaceSnapshot snapshot,
        WorkspaceCheckpointTrigger trigger,
        string targetWorkspaceId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.IsNullOrWhiteSpace(targetWorkspaceId) && !Guid.TryParse(targetWorkspaceId, out _))
            throw new ArgumentException("Target workspace ID must be a GUID when supplied.", nameof(targetWorkspaceId));

        lock (_sync)
        {
            DateTime createdAt = _clock.UtcNow;
            snapshot.SavedAt = createdAt;
            snapshot.Checkpoint = new WorkspaceCheckpointMetadata
            {
                SchemaVersion = WorkspaceCheckpointMetadata.CurrentSchemaVersion,
                CheckpointId = snapshot.WorkspaceId,
                Trigger = trigger,
                CreatedAtUtc = createdAt,
                ExpiresAtUtc = createdAt.Add(_retention.MaximumAge),
                TargetWorkspaceId = targetWorkspaceId,
                SourceMonitorFingerprint = snapshot.MonitorFingerprint
            };

            // This is the transaction's durability boundary. If it fails, the caller must not
            // begin any restore mutation.
            SaveCanonical(snapshot);
            TryMaintainHistoryAndIndex(writeIndexWhenUnchanged: true);

            return new CheckpointSaveReceipt(
                snapshot.Checkpoint.CheckpointId,
                snapshot.Checkpoint.CreatedAtUtc,
                snapshot.Checkpoint.ExpiresAtUtc,
                snapshot.Checkpoint.Trigger,
                snapshot.Checkpoint.TargetWorkspaceId);
        }
    }

    /// <summary>Returns the newest healthy, non-expired checkpoint; corrupt peers are isolated.</summary>
    public WorkspaceSnapshot? GetLatest()
    {
        lock (_sync)
        {
            TryMaintainHistoryAndIndex(writeIndexWhenUnchanged: false);
            DateTime now = _clock.UtcNow;
            return Load().Workspaces
                .Where(checkpoint => GetExpiry(checkpoint) > now)
                .OrderByDescending(GetCreatedAt)
                .ThenByDescending(checkpoint => checkpoint.WorkspaceId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
    }

    public override void Delete(string workspaceId)
    {
        lock (_sync)
        {
            base.Delete(workspaceId);
            TryMaintainHistoryAndIndex(writeIndexWhenUnchanged: true);
        }
    }

    private void TryMaintainHistoryAndIndex(bool writeIndexWhenUnchanged)
    {
        try
        {
            DateTime now = _clock.UtcNow;
            WorkspaceLoadResult loaded = Load();
            WorkspaceSnapshot[] newest = loaded.Workspaces
                .Where(checkpoint => GetExpiry(checkpoint) > now)
                .OrderByDescending(GetCreatedAt)
                .ThenByDescending(checkpoint => checkpoint.WorkspaceId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var deleteIds = loaded.Workspaces
                .Where(checkpoint => GetExpiry(checkpoint) <= now)
                .Select(checkpoint => checkpoint.WorkspaceId)
                .Concat(newest.Skip(_retention.MaximumCount).Select(checkpoint => checkpoint.WorkspaceId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (string checkpointId in deleteIds)
                base.Delete(checkpointId);

            if (deleteIds.Length == 0 && !writeIndexWhenUnchanged)
                return;

            WorkspaceLoadResult retained = Load();
            var index = new CheckpointIndexDocument
            {
                UpdatedAtUtc = now,
                IsolatedFailureCount = retained.Issues.Count,
                Checkpoints = retained.Workspaces
                    .Where(checkpoint => GetExpiry(checkpoint) > now)
                    .OrderByDescending(GetCreatedAt)
                    .Select(checkpoint => new CheckpointIndexEntry(
                        checkpoint.WorkspaceId,
                        GetCreatedAt(checkpoint),
                        GetExpiry(checkpoint),
                        checkpoint.Checkpoint?.Trigger ?? WorkspaceCheckpointTrigger.ManualCapture,
                        checkpoint.Checkpoint?.TargetWorkspaceId ?? "",
                        checkpoint.Checkpoint?.SourceMonitorFingerprint ?? checkpoint.MonitorFingerprint,
                        checkpoint.Entries.Count))
                    .ToArray()
            };
            _writer.WriteAllText(
                Path.Combine(_directory, "checkpoint-index.json"),
                JsonSerializer.Serialize(index, IndexJsonOptions));
        }
        catch (Exception ex)
        {
            // The payload was already committed atomically. The index is a cache and is rebuilt
            // on the next successful save/delete, so maintenance failure must not invalidate an
            // otherwise safe pre-restore checkpoint.
            AppLogger.Warn(
                "checkpoint.maintenance_failed",
                "Checkpoint retention or metadata-index maintenance failed",
                ex,
                LogField.Public("errorCategory", "checkpoint_maintenance"));
        }
    }

    private DateTime GetCreatedAt(WorkspaceSnapshot checkpoint) =>
        checkpoint.Checkpoint?.CreatedAtUtc.ToUniversalTime() ?? checkpoint.SavedAt.ToUniversalTime();

    private DateTime GetExpiry(WorkspaceSnapshot checkpoint) =>
        checkpoint.Checkpoint?.ExpiresAtUtc.ToUniversalTime() ??
        checkpoint.SavedAt.ToUniversalTime().Add(_retention.MaximumAge);
}

/// <summary>Short-lived captures stored exclusively under <c>temporary-captures/</c>.</summary>
public sealed class TemporaryCaptureRepository : WorkspaceRepository
{
    internal TemporaryCaptureRepository(string directory, IAtomicFileWriter writer)
        : base(directory, ".temporary.json", WorkspaceArtifactKind.TemporaryCapture, writer)
    {
    }
}
