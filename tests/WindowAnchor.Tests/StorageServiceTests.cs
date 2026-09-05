using System.Text.Json;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class StorageServiceTests
{
    [Fact]
    public void Load_all_workspaces_migrates_v2_loads_v3_and_reports_corrupt_and_future_documents()
    {
        using var directory = new TestDirectory();
        string legacyPath = directory.CopyFixture(
            "current.workspace.json",
            @"workspaces\legacy-current.workspace.json");
        string currentPath = directory.CopyFixture(
            "current-v3.workspace.json",
            @"workspaces\current-v3.workspace.json");
        string futurePath = directory.CopyFixture(
            "unsupported-future.workspace.json",
            @"workspaces\future.workspace.json");
        directory.CopyFixture("corrupt.workspace.json", @"workspaces\corrupt.workspace.json");
        string futureBefore = File.ReadAllText(futurePath);
        var storage = new StorageService(directory.Path);

        var load = storage.LoadNamedWorkspaces();
        var workspaces = load.Workspaces;

        Assert.Equal(2, workspaces.Count);
        var migrated = Assert.Single(
            workspaces,
            workspace => workspace.Name == "Characterization Fixture");
        Assert.Equal(WorkspaceSnapshot.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.True(Guid.TryParse(migrated.WorkspaceId, out _));
        Assert.Equal(7, migrated.Entries.Count);
        Assert.All(migrated.Entries, entry => Assert.True(Guid.TryParse(entry.EntryId, out _)));
        Assert.Equal(migrated.Entries.Count, migrated.Entries.Select(entry => entry.EntryId).Distinct().Count());
        Assert.Contains(migrated.Entries, entry => entry.IsWebApp);
        Assert.Contains(migrated.Entries, entry => entry.IsDedicatedBrowserWindow);
        Assert.Equal(2, migrated.Entries.Count(entry => entry.ProcessName == "TradingView"));
        Assert.Contains(migrated.Entries, entry => entry.LaunchArg?.EndsWith("Thesis.docx") == true);
        Assert.Contains(migrated.Entries, entry => entry.AppUserModelId.Contains('!'));
        Assert.Contains(migrated.Entries, entry => entry.LaunchArg == @"Z:\missing\gone.txt");
        Assert.Contains(migrated.Entries, entry => entry.Position.SavedDpi == 96);
        Assert.Contains(migrated.Entries, entry => entry.Position.SavedDpi == 144);

        var current = Assert.Single(workspaces, workspace => workspace.Name == "Stable Workspace");
        Assert.Equal("11111111-1111-4111-8111-111111111111", current.WorkspaceId);
        Assert.Equal("22222222-2222-4222-8222-222222222222", Assert.Single(current.Entries).EntryId);
        Assert.False(File.Exists(currentPath));
        Assert.True(File.Exists(WorkspacePath(directory, current.WorkspaceId)));

        Assert.Equal(2, load.Issues.Count);
        Assert.Contains(load.Issues, issue =>
            issue.FailureKind == StorageLoadFailureKind.UnsupportedSchemaVersion &&
            issue.Message.Contains("Unsupported future workspace"));
        Assert.Contains(load.Issues, issue =>
            issue.FailureKind == StorageLoadFailureKind.CorruptJson &&
            issue.FilePath.EndsWith("corrupt.workspace.json"));
        Assert.All(load.Issues, issue =>
            Assert.Equal(WorkspaceArtifactKind.NamedWorkspace, issue.ArtifactKind));
        Assert.Equal(load.Issues, storage.LastLoadIssues);
        Assert.Equal(futureBefore, File.ReadAllText(futurePath));

        Assert.False(File.Exists(legacyPath));
        using var migratedJson = JsonDocument.Parse(
            File.ReadAllText(WorkspacePath(directory, migrated.WorkspaceId)));
        Assert.Equal(
            WorkspaceSnapshot.CurrentSchemaVersion,
            migratedJson.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(migrated.WorkspaceId, migratedJson.RootElement.GetProperty("workspaceId").GetString());
    }

    [Fact]
    public void V1_profile_import_is_deterministic_retries_failures_and_is_idempotent()
    {
        using var firstDirectory = new TestDirectory();
        firstDirectory.CopyFixture("legacy-v1.profile.json", @"profiles\desk.profile.json");
        firstDirectory.CopyFixture("legacy-corrupt.profile.json", @"profiles\corrupt.profile.json");

        var firstStorage = new StorageService(firstDirectory.Path);
        var first = Assert.Single(firstStorage.LoadAllWorkspaces());
        Assert.Equal("Legacy Desk", first.Name);
        Assert.Equal("legacy42", first.MonitorFingerprint);
        Assert.Equal(WorkspaceSnapshot.CurrentSchemaVersion, first.SchemaVersion);
        Assert.False(first.SavedWithFiles);
        Assert.Empty(first.Monitors);
        Assert.Equal(2, first.Entries.Count);
        Assert.All(first.Entries, entry =>
        {
            Assert.True(Guid.TryParse(entry.EntryId, out _));
            Assert.Null(entry.FilePath);
            Assert.Null(entry.LaunchArg);
            Assert.Equal("NONE", entry.FileSource);
            Assert.Empty(entry.MonitorId);
        });

        Assert.False(File.Exists(System.IO.Path.Combine(firstDirectory.Path, ".migrated_v2")));
        string workspacePath = WorkspacePath(firstDirectory, first.WorkspaceId);
        string afterFirstImport = File.ReadAllText(workspacePath);

        var afterRetry = Assert.Single(new StorageService(firstDirectory.Path).LoadAllWorkspaces());
        Assert.Equal(first.WorkspaceId, afterRetry.WorkspaceId);
        Assert.Equal(
            first.Entries.Select(entry => entry.EntryId),
            afterRetry.Entries.Select(entry => entry.EntryId));
        Assert.Equal(afterFirstImport, File.ReadAllText(workspacePath));

        using var secondDirectory = new TestDirectory();
        secondDirectory.CopyFixture("legacy-v1.profile.json", @"profiles\desk.profile.json");
        var second = Assert.Single(new StorageService(secondDirectory.Path).LoadAllWorkspaces());
        Assert.Equal(first.WorkspaceId, second.WorkspaceId);
        Assert.Equal(
            first.Entries.Select(entry => entry.EntryId),
            second.Entries.Select(entry => entry.EntryId));
        Assert.True(File.Exists(System.IO.Path.Combine(secondDirectory.Path, ".migrated_v2")));
    }

    [Fact]
    public void V2_workspace_migration_is_deterministic_and_idempotent()
    {
        using var firstDirectory = new TestDirectory();
        string legacyPath = firstDirectory.CopyFixture(
            "legacy-v2.workspace.json",
            @"workspaces\legacy-v2.workspace.json");
        var firstStorage = new StorageService(firstDirectory.Path);
        var first = Assert.Single(firstStorage.LoadAllWorkspaces());
        string firstPath = WorkspacePath(firstDirectory, first.WorkspaceId);
        Assert.False(File.Exists(legacyPath));
        string onceMigrated = File.ReadAllText(firstPath);

        var secondLoad = Assert.Single(firstStorage.LoadAllWorkspaces());

        Assert.Equal(first.WorkspaceId, secondLoad.WorkspaceId);
        Assert.Equal(Assert.Single(first.Entries).EntryId, Assert.Single(secondLoad.Entries).EntryId);
        Assert.Equal(onceMigrated, File.ReadAllText(firstPath));

        using var secondDirectory = new TestDirectory();
        secondDirectory.CopyFixture("legacy-v2.workspace.json", @"workspaces\legacy-v2.workspace.json");
        var independent = Assert.Single(new StorageService(secondDirectory.Path).LoadAllWorkspaces());
        Assert.Equal(first.WorkspaceId, independent.WorkspaceId);
        Assert.Equal(Assert.Single(first.Entries).EntryId, Assert.Single(independent.Entries).EntryId);
    }

    [Fact]
    public void New_save_and_resave_emit_current_schema_and_preserve_workspace_identity()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        var original = new WorkspaceSnapshot
        {
            Name = "Desk Focus",
            MonitorFingerprint = "local123",
            SavedAt = DateTime.UtcNow,
            Entries = [new WorkspaceEntry { ProcessName = "editor" }]
        };
        string originalId = original.WorkspaceId;

        storage.SaveWorkspace(original);
        var saved = Assert.Single(storage.LoadAllWorkspaces());
        Assert.Equal(originalId, saved.WorkspaceId);
        Assert.Equal(WorkspaceSnapshot.CurrentSchemaVersion, saved.SchemaVersion);
        Assert.True(Guid.TryParse(Assert.Single(saved.Entries).EntryId, out _));

        var replacement = new WorkspaceSnapshot
        {
            Name = "Desk Focus",
            MonitorFingerprint = "changed",
            SavedAt = DateTime.UtcNow,
            Entries = [new WorkspaceEntry { ProcessName = "terminal" }]
        };
        storage.SaveWorkspace(replacement);

        Assert.Equal(originalId, replacement.WorkspaceId);
        Assert.Equal(originalId, Assert.Single(storage.LoadAllWorkspaces()).WorkspaceId);
    }

    [Fact]
    public void Current_workspace_round_trips_monitor_work_area_and_normalized_layout()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        var snapshot = new WorkspaceSnapshot
        {
            Name = "Semantic layout",
            SavedAt = DateTime.UtcNow,
            Monitors =
            [
                new MonitorInfo
                {
                    MonitorId = "display",
                    Index = 0,
                    WidthPixels = 1920,
                    HeightPixels = 1080,
                    BoundsRight = 1920,
                    BoundsBottom = 1080,
                    WorkAreaRight = 1920,
                    WorkAreaBottom = 1040,
                    Dpi = 144,
                    IsPrimary = true
                }
            ],
            Entries =
            [
                new WorkspaceEntry
                {
                    MonitorId = "display",
                    Position = new WindowRecord
                    {
                        NormalRight = 960,
                        NormalBottom = 1040,
                        SavedDpi = 144,
                        NormalizedLayout = new NormalizedWindowLayout
                        {
                            X = 0,
                            Y = 0,
                            Width = .5,
                            Height = 1,
                            Kind = WindowLayoutKind.LeftHalf,
                            HorizontalAnchor = HorizontalWindowAnchor.Left,
                            VerticalAnchor = VerticalWindowAnchor.Stretch
                        }
                    }
                }
            ]
        };

        storage.SaveWorkspace(snapshot);
        WorkspaceSnapshot restored = Assert.Single(storage.LoadAllWorkspaces());

        Assert.Equal(WorkspaceSnapshot.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.Equal((0, 0, 1920, 1040, (uint)144),
            (restored.Monitors[0].WorkAreaLeft,
             restored.Monitors[0].WorkAreaTop,
             restored.Monitors[0].WorkAreaRight,
             restored.Monitors[0].WorkAreaBottom,
             restored.Monitors[0].Dpi));
        NormalizedWindowLayout layout = Assert.IsType<NormalizedWindowLayout>(
            restored.Entries[0].Position.NormalizedLayout);
        Assert.Equal(WindowLayoutKind.LeftHalf, layout.Kind);
        Assert.Equal(.5, layout.Width);
        Assert.Equal(VerticalWindowAnchor.Stretch, layout.VerticalAnchor);
    }

    [Fact]
    public void Rename_preserves_workspace_identity_and_named_workspace_behavior()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        var snapshot = new WorkspaceSnapshot
        {
            Name = "Desk: Focus",
            MonitorFingerprint = "local123",
            SavedAt = DateTime.UtcNow
        };
        storage.SaveWorkspace(snapshot);
        string workspaceId = snapshot.WorkspaceId;

        storage.RenameWorkspace(snapshot, "Desk Review");

        var renamed = Assert.Single(storage.LoadAllWorkspaces());
        Assert.Equal("Desk Review", renamed.Name);
        Assert.Equal(workspaceId, renamed.WorkspaceId);

        storage.DeleteWorkspace(snapshot);
        Assert.Empty(storage.LoadAllWorkspaces());
    }

    [Fact]
    public void Unsupported_future_workspace_is_not_overwritten_by_load_or_same_name_save()
    {
        using var directory = new TestDirectory();
        string path = directory.CopyFixture(
            "unsupported-future.workspace.json",
            @"workspaces\Future Workspace.workspace.json");
        string original = File.ReadAllText(path);
        var storage = new StorageService(directory.Path);

        Assert.Empty(storage.LoadAllWorkspaces());
        Assert.Single(storage.LastLoadIssues);
        Assert.Throws<UnsupportedSchemaVersionException>(() => storage.SaveWorkspace(new WorkspaceSnapshot
        {
            Name = "Future Workspace"
        }));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Invalid_current_workspace_ids_fail_validation_without_being_replaced_by_defaults()
    {
        using var directory = new TestDirectory();
        string path = directory.CopyFixture(
            "invalid-v3.workspace.json",
            @"workspaces\invalid-v3.workspace.json");
        string original = File.ReadAllText(path);
        var storage = new StorageService(directory.Path);

        Assert.Empty(storage.LoadAllWorkspaces());

        var issue = Assert.Single(storage.LastLoadIssues);
        Assert.Contains("persist a GUID EntryId", issue.Message);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Last_known_fingerprint_round_trips_in_the_injected_root()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);

        Assert.Empty(storage.GetLastKnownFingerprint());
        storage.SetLastKnownFingerprint(" a1b2c3d4 ");

        Assert.Equal("a1b2c3d4", storage.GetLastKnownFingerprint());
    }

    [Fact]
    public void Interrupted_atomic_update_preserves_the_previous_valid_document()
    {
        using var directory = new TestDirectory();
        var baselineStorage = new StorageService(directory.Path);
        var snapshot = new WorkspaceSnapshot
        {
            Name = "Atomic Desk",
            MonitorFingerprint = "before",
            SavedAt = DateTime.UtcNow,
        };
        baselineStorage.SaveWorkspace(snapshot);
        string path = WorkspacePath(directory, snapshot.WorkspaceId);
        string before = File.ReadAllText(path);

        var failingWriter = new AtomicFileWriter((stage, destination) =>
        {
            if (stage == AtomicWriteStage.TemporaryFileFlushed &&
                destination.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Injected interruption before atomic commit");
            }
        });
        var failingStorage = new StorageService(directory.Path, failingWriter);
        snapshot.MonitorFingerprint = "after";

        Assert.Throws<IOException>(() => failingStorage.SaveWorkspace(snapshot));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(
            System.IO.Path.GetDirectoryName(path)!,
            "*.tmp"));
        var reloaded = Assert.Single(new StorageService(directory.Path).LoadAllWorkspaces());
        Assert.Equal("before", reloaded.MonitorFingerprint);
    }

    [Fact]
    public void Interrupted_rename_keeps_the_only_durable_copy_and_restores_runtime_name()
    {
        using var directory = new TestDirectory();
        var baselineStorage = new StorageService(directory.Path);
        var snapshot = new WorkspaceSnapshot
        {
            Name = "Before Rename",
            MonitorFingerprint = "atomic",
            SavedAt = DateTime.UtcNow,
        };
        baselineStorage.SaveWorkspace(snapshot);
        string path = WorkspacePath(directory, snapshot.WorkspaceId);
        string before = File.ReadAllText(path);

        var failingStorage = new StorageService(
            directory.Path,
            new AtomicFileWriter((_, destination) =>
            {
                if (destination.Equals(path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Injected rename interruption");
            }));

        Assert.Throws<IOException>(() => failingStorage.RenameWorkspace(snapshot, "After Rename"));

        Assert.Equal("Before Rename", snapshot.Name);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(
            "Before Rename",
            Assert.Single(new StorageService(directory.Path).LoadAllWorkspaces()).Name);
    }

    [Fact]
    public void Names_that_collide_when_sanitized_coexist_in_id_addressed_files()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        var punctuated = new WorkspaceSnapshot { Name = "A:B", SavedAt = DateTime.UtcNow };
        var plain = new WorkspaceSnapshot { Name = "AB", SavedAt = DateTime.UtcNow };

        storage.SaveWorkspace(punctuated);
        storage.SaveWorkspace(plain);

        var loaded = storage.LoadAllWorkspaces();
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, workspace => workspace.Name == "A:B");
        Assert.Contains(loaded, workspace => workspace.Name == "AB");
        Assert.NotEqual(punctuated.WorkspaceId, plain.WorkspaceId);
        Assert.True(File.Exists(WorkspacePath(directory, punctuated.WorkspaceId)));
        Assert.True(File.Exists(WorkspacePath(directory, plain.WorkspaceId)));
    }

    [Fact]
    public void Typed_repositories_never_mix_or_delete_unrelated_artifacts()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        var named = new WorkspaceSnapshot { Name = "Named", SavedAt = DateTime.UtcNow };
        var checkpoint = new WorkspaceSnapshot { Name = "Checkpoint", SavedAt = DateTime.UtcNow };
        var temporary = new WorkspaceSnapshot { Name = "Temporary", SavedAt = DateTime.UtcNow };

        storage.NamedWorkspaces.Save(named);
        storage.Checkpoints.Save(checkpoint);
        storage.TemporaryCaptures.Save(temporary);

        Assert.Equal("Named", Assert.Single(storage.LoadAllWorkspaces()).Name);
        Assert.Equal("Checkpoint", Assert.Single(storage.Checkpoints.Load().Workspaces).Name);
        Assert.Equal("Temporary", Assert.Single(storage.TemporaryCaptures.Load().Workspaces).Name);

        storage.TemporaryCaptures.Delete(temporary);

        Assert.Empty(storage.TemporaryCaptures.Load().Workspaces);
        Assert.Single(storage.NamedWorkspaces.Load().Workspaces);
        Assert.Single(storage.Checkpoints.Load().Workspaces);
    }

    [Fact]
    public void Checkpoint_retention_prunes_oldest_and_expired_without_touching_named_workspaces()
    {
        using var directory = new TestDirectory();
        var clock = new FakeCheckpointClock();
        var storage = new StorageService(
            directory.Path,
            checkpointRetention: new CheckpointRetentionPolicy
            {
                MaximumCount = 2,
                MaximumAge = TimeSpan.FromDays(2)
            },
            checkpointClock: clock);
        var named = new WorkspaceSnapshot { Name = "Permanent", SavedAt = clock.UtcNow };
        storage.NamedWorkspaces.Save(named);

        var first = new WorkspaceSnapshot { Name = "First" };
        storage.Checkpoints.Save(first, WorkspaceCheckpointTrigger.Restore, named.WorkspaceId);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        var second = new WorkspaceSnapshot { Name = "Second" };
        storage.Checkpoints.Save(second, WorkspaceCheckpointTrigger.WorkspaceSwitch, named.WorkspaceId);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        var third = new WorkspaceSnapshot { Name = "Third" };
        storage.Checkpoints.Save(third, WorkspaceCheckpointTrigger.Undo, first.WorkspaceId);

        WorkspaceSnapshot[] retained = storage.Checkpoints.Load().Workspaces
            .OrderBy(checkpoint => checkpoint.SavedAt)
            .ToArray();
        Assert.Equal([second.WorkspaceId, third.WorkspaceId], retained.Select(item => item.WorkspaceId));
        Assert.Equal("Permanent", Assert.Single(storage.NamedWorkspaces.Load().Workspaces).Name);

        clock.UtcNow = clock.UtcNow.AddDays(3);
        var newest = new WorkspaceSnapshot { Name = "Newest" };
        storage.Checkpoints.Save(newest, WorkspaceCheckpointTrigger.Restore, named.WorkspaceId);

        Assert.Equal(newest.WorkspaceId, Assert.Single(storage.Checkpoints.Load().Workspaces).WorkspaceId);
        Assert.Equal("Permanent", Assert.Single(storage.NamedWorkspaces.Load().Workspaces).Name);

        clock.UtcNow = clock.UtcNow.AddDays(3);
        Assert.Null(storage.Checkpoints.GetLatest());
        Assert.Empty(storage.Checkpoints.Load().Workspaces);
        Assert.Equal("Permanent", Assert.Single(storage.NamedWorkspaces.Load().Workspaces).Name);
    }

    [Fact]
    public void Checkpoint_index_is_versioned_and_corrupt_checkpoint_is_isolated()
    {
        using var directory = new TestDirectory();
        var clock = new FakeCheckpointClock();
        var storage = new StorageService(directory.Path, checkpointClock: clock);
        var healthy = new WorkspaceSnapshot
        {
            Name = "Healthy",
            MonitorFingerprint = "topology-safe"
        };
        storage.Checkpoints.Save(
            healthy,
            WorkspaceCheckpointTrigger.AdaptiveRestore,
            Guid.NewGuid().ToString("D"));
        string corruptPath = Path.Combine(
            directory.Path,
            "checkpoints",
            $"{Guid.NewGuid():D}.checkpoint.json");
        File.WriteAllText(corruptPath, "{ definitely not json");

        WorkspaceLoadResult loaded = storage.Checkpoints.Load();
        WorkspaceSnapshot latest = Assert.IsType<WorkspaceSnapshot>(storage.Checkpoints.GetLatest());

        Assert.Equal(healthy.WorkspaceId, latest.WorkspaceId);
        Assert.Single(loaded.Workspaces);
        Assert.Contains(loaded.Issues, issue => issue.FailureKind == StorageLoadFailureKind.CorruptJson);
        using JsonDocument index = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            directory.Path,
            "checkpoints",
            "checkpoint-index.json")));
        Assert.Equal(1, index.RootElement.GetProperty("schemaVersion").GetInt32());
        JsonElement indexed = Assert.Single(index.RootElement.GetProperty("checkpoints").EnumerateArray());
        Assert.Equal(healthy.WorkspaceId, indexed.GetProperty("checkpointId").GetString());
        Assert.Equal("AdaptiveRestore", indexed.GetProperty("trigger").GetString());
    }

    private static string WorkspacePath(TestDirectory directory, string workspaceId) =>
        System.IO.Path.Combine(
            directory.Path,
            "workspaces",
            $"{Guid.Parse(workspaceId):D}.workspace.json");
}
