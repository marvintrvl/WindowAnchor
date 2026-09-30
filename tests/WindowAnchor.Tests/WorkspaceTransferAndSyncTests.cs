using System.Text.Json;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class WorkspaceTransferAndSyncTests
{
    [Fact]
    public void Portable_export_redacts_machine_metadata_from_entries_and_layout_variants()
    {
        using var directory = new TestDirectory();
        var service = new WorkspaceTransferService(new StorageService(directory.Path));
        string destination = Path.Combine(directory.Path, "portable.windowanchor.json");
        WorkspaceSnapshot workspace = Workspace("Portable");
        WorkspaceEntry entry = Assert.Single(workspace.Entries);
        entry.ExecutablePath = @"C:\Users\Alice\Tools\editor.exe";
        entry.LogicalExecutablePath = "${TOOLS}\\editor.exe";
        entry.BrowserUrl = "https://private.example/work";
        entry.AppUserModelId = "Private.Package!App";
        entry.Position.TitleSnippet = "Secret document";
        entry.Position.FolderPath = @"C:\Users\Alice\Secret";
        entry.ExplorerTabPaths =
        [
            @"C:\Users\Alice\Secret",
            @"C:\Users\Alice\Other secret"
        ];
        entry.ExplorerActiveTabIndex = 1;
        entry.TerminalTabs =
        [
            new TerminalTab { Profile = "PowerShell", StartingDirectory = @"C:\Users\Alice\TerminalOnly" },
            new TerminalTab { Profile = "Ubuntu", StartingDirectory = @"C:\Users\Alice\SecondTerminalOnly" }
        ];
        entry.TerminalActiveTabIndex = 1;
        workspace.MonitorFingerprint = "private-fingerprint";
        workspace.Monitors = [Monitor("private-monitor")];
        workspace.EnsureLayoutVariants();
        workspace.LayoutVariants[0].DeskProfileId = "private-desk";

        service.Export(workspace, WorkspaceExportMode.PortableRedacted, destination);

        string json = File.ReadAllText(destination);
        Assert.DoesNotContain(@"C:\Users\Alice", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-monitor", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-fingerprint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-desk", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private.example", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret document", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TerminalOnly", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("${TOOLS}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_with_a_duplicate_name_creates_a_distinct_workspace_without_overwriting()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        WorkspaceSnapshot existing = Workspace("Shared name");
        storage.NamedWorkspaces.Save(existing);
        WorkspaceSnapshot imported = Workspace("Shared name");
        string source = WriteTransfer(directory, imported);

        WorkspaceSnapshot result = new WorkspaceTransferService(storage).Import(source);

        WorkspaceSnapshot[] saved = storage.LoadAllWorkspaces().ToArray();
        Assert.Equal(2, saved.Length);
        Assert.Contains(saved, workspace => workspace.WorkspaceId == existing.WorkspaceId && workspace.Name == "Shared name");
        Assert.Contains(saved, workspace => workspace.WorkspaceId == imported.WorkspaceId && workspace.Name == "Shared name (Imported)");
        Assert.Equal("Shared name (Imported)", result.Name);
    }

    [Fact]
    public void Import_rejects_an_identity_collision_without_mutating_storage()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        WorkspaceSnapshot existing = Workspace("Original");
        storage.NamedWorkspaces.Save(existing);
        string source = WriteTransfer(directory, existing);

        Assert.Throws<InvalidDataException>(() => new WorkspaceTransferService(storage).Import(
            source,
            WorkspaceImportCollisionPolicy.Reject));

        Assert.Equal("Original", Assert.Single(storage.LoadAllWorkspaces()).Name);
    }

    [Fact]
    public void Import_preview_reports_name_and_stable_id_conflicts_before_any_write()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        WorkspaceSnapshot existing = Workspace("Existing");
        storage.NamedWorkspaces.Save(existing);
        string source = WriteTransfer(directory, existing);

        WorkspaceImportPreview preview = new WorkspaceTransferService(storage).PreviewImport(source);

        Assert.Equal(WorkspaceImportConflictKind.NameAndWorkspaceId, preview.ConflictKind);
        Assert.True(preview.HasConflict);
        Assert.Equal("Existing", Assert.Single(storage.LoadAllWorkspaces()).Name);
    }

    [Fact]
    public void Import_migrates_a_v1_transfer_envelope_before_saving()
    {
        using var directory = new TestDirectory();
        WorkspaceSnapshot workspace = Workspace("Old transfer");
        workspace.EnsureLayoutVariants();
        string source = Path.Combine(directory.Path, "old.windowanchor.json");
        File.WriteAllText(source, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            mode = (int)WorkspaceExportMode.PortableRedacted,
            workspace
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var service = new WorkspaceTransferService(new StorageService(directory.Path));
        WorkspaceImportPreview preview = service.PreviewImport(source);
        WorkspaceSnapshot imported = service.Import(source, expectedContentHash: preview.ContentHash);

        Assert.True(preview.WasMigrated);
        Assert.Equal(WorkspaceExportMode.PortableRedacted, preview.Mode);
        Assert.False(preview.Options.IncludeFilesAndFolders);
        Assert.Equal("Old transfer", imported.Name);
    }

    [Fact]
    public void Import_rejects_a_file_changed_after_the_preview_without_mutating_storage()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        var service = new WorkspaceTransferService(storage);
        string source = WriteTransfer(directory, Workspace("Reviewed"));
        WorkspaceImportPreview preview = service.PreviewImport(source);
        WorkspaceSnapshot tampered = Workspace("Tampered");
        tampered.EnsureLayoutVariants();
        File.WriteAllText(source, JsonSerializer.Serialize(
            new WorkspaceTransferDocument { Mode = WorkspaceExportMode.ExactBackup, Workspace = tampered },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => service.Import(
            source,
            WorkspaceImportCollisionPolicy.Clone,
            preview.ContentHash));

        Assert.Contains("changed after preview", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(storage.LoadAllWorkspaces());
    }

    [Fact]
    public void Import_rejects_a_future_transfer_schema_without_mutating_storage()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        string source = Path.Combine(directory.Path, "future.windowanchor.json");
        File.WriteAllText(source, "{\"schemaVersion\":999,\"workspace\":{}}");

        Assert.Throws<InvalidDataException>(() => new WorkspaceTransferService(storage).PreviewImport(source));
        Assert.Empty(storage.LoadAllWorkspaces());
    }

    [Fact]
    public void Import_atomic_commit_failure_preserves_existing_workspace_storage()
    {
        using var directory = new TestDirectory();
        string source = WriteTransfer(directory, Workspace("Interrupted"));
        string destinationDirectory = Path.Combine(directory.Path, "destination");
        Directory.CreateDirectory(destinationDirectory);
        File.WriteAllText(Path.Combine(destinationDirectory, ".migrated_v2"), "");
        var failingStorage = new StorageService(
            destinationDirectory,
            new ThrowingAtomicFileWriter());

        Assert.Throws<IOException>(() => new WorkspaceTransferService(failingStorage).Import(source));
        Assert.Empty(failingStorage.LoadAllWorkspaces());
    }

    [Fact]
    public void Explicit_export_options_remove_unselected_metadata_categories()
    {
        using var directory = new TestDirectory();
        var service = new WorkspaceTransferService(new StorageService(directory.Path));
        string destination = Path.Combine(directory.Path, "minimal.windowanchor.json");
        WorkspaceSnapshot workspace = Workspace("Minimal");
        WorkspaceEntry entry = Assert.Single(workspace.Entries);
        entry.ExecutablePath = @"C:\Users\Alice\private.exe";
        entry.FilePath = @"C:\Users\Alice\private.txt";
        entry.LaunchArg = @"C:\Users\Alice\project";
        entry.BrowserUrl = "https://private.example/secret";
        entry.Position = new WindowRecord
        {
            ExecutablePath = entry.ExecutablePath,
            ProcessName = entry.ProcessName,
            ClassName = entry.WindowClassName,
            BrowserUrl = entry.BrowserUrl,
            TitleSnippet = "Private title",
            FolderPath = @"C:\Users\Alice\folder",
            NormalRight = 800,
            NormalBottom = 600
        };
        workspace.Monitors = [Monitor("private-monitor")];
        workspace.MonitorFingerprint = "private-fingerprint";
        workspace.EnsureLayoutVariants();

        service.Export(workspace, new WorkspaceExportOptions
        {
            IncludeLayout = false,
            IncludeApplicationIdentities = false,
            IncludeFilesAndFolders = false,
            IncludeBrowserUrls = false,
            IncludeMachineIdentifiers = false,
            IncludeLogicalPathAliases = false
        }, destination);

        string json = File.ReadAllText(destination);
        Assert.DoesNotContain("C:\\Users\\Alice", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private.example", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-monitor", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-fingerprint", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private title", json, StringComparison.OrdinalIgnoreCase);
        WorkspaceTransferDocument document = JsonSerializer.Deserialize<WorkspaceTransferDocument>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Assert.Equal(WorkspaceTransferDocument.CurrentSchemaVersion, document.SchemaVersion);
        Assert.False(document.Options.IncludeFilesAndFolders);
    }

    [Fact]
    public void Portable_export_without_aliases_removes_alias_references_and_absolute_fallbacks()
    {
        using var directory = new TestDirectory();
        var service = new WorkspaceTransferService(new StorageService(directory.Path));
        string destination = Path.Combine(directory.Path, "no-alias.windowanchor.json");
        WorkspaceSnapshot workspace = Workspace("No aliases");
        WorkspaceEntry entry = Assert.Single(workspace.Entries);
        entry.ExecutablePath = @"C:\Users\Alice\editor.exe";
        entry.LogicalExecutablePath = "${TOOLS}\\editor.exe";
        workspace.EnsureLayoutVariants();

        service.Export(workspace, WorkspaceExportOptions.PortableRedacted() with
        {
            IncludeLogicalPathAliases = false
        }, destination);

        string json = File.ReadAllText(destination);
        Assert.DoesNotContain("C:\\Users\\Alice", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("${TOOLS}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_import_does_not_mutate_storage()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        storage.NamedWorkspaces.Save(Workspace("Healthy"));
        string source = Path.Combine(directory.Path, "malformed.json");
        File.WriteAllText(source, "{ this is not json }");

        Assert.Throws<JsonException>(() => new WorkspaceTransferService(storage).Import(source));
        Assert.Equal("Healthy", Assert.Single(storage.LoadAllWorkspaces()).Name);
    }

    [Fact]
    public async Task Folder_provider_round_trips_content_and_computes_a_stable_revision()
    {
        using var directory = new TestDirectory();
        var provider = new FolderSyncProvider(Path.Combine(directory.Path, "sync"));
        string workspaceId = Guid.NewGuid().ToString("D");

        await provider.PutAsync(new SyncRevision(workspaceId, "ignored-at-transport", "{\"schemaVersion\":1}"));
        SyncRevision loaded = Assert.Single(await provider.ListAsync());

        Assert.Equal(workspaceId, loaded.WorkspaceId);
        Assert.Equal("{\"schemaVersion\":1}", loaded.Content);
        Assert.Equal(64, loaded.Revision.Length);
    }

    [Fact]
    public async Task Folder_provider_atomic_failure_preserves_the_previous_revision()
    {
        using var directory = new TestDirectory();
        string syncDirectory = Path.Combine(directory.Path, "sync");
        string workspaceId = Guid.NewGuid().ToString("D");
        var healthy = new FolderSyncProvider(syncDirectory);
        await healthy.PutAsync(new SyncRevision(workspaceId, "first", "original"));
        var failing = new FolderSyncProvider(syncDirectory, new ThrowingAtomicFileWriter());

        await Assert.ThrowsAsync<IOException>(() => failing.PutAsync(
            new SyncRevision(workspaceId, "second", "replacement")));

        Assert.Equal("original", Assert.Single(await healthy.ListAsync()).Content);
    }

    private static WorkspaceSnapshot Workspace(string name) => new()
    {
        Name = name,
        SavedAt = new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc),
        Entries =
        [
            new WorkspaceEntry
            {
                ProcessName = "editor",
                ExecutablePath = @"C:\Tools\editor.exe",
                Position = new WindowRecord { ProcessName = "editor", ClassName = "EditorWindow" }
            }
        ]
    };

    private static MonitorInfo Monitor(string id) => new()
    {
        MonitorId = id,
        FriendlyName = "Private display",
        DeviceName = @"\\.\DISPLAY9",
        WidthPixels = 1920,
        HeightPixels = 1080,
        BoundsRight = 1920,
        BoundsBottom = 1080,
        WorkAreaRight = 1920,
        WorkAreaBottom = 1040
    };

    private static string WriteTransfer(TestDirectory directory, WorkspaceSnapshot workspace)
    {
        workspace.EnsureLayoutVariants();
        string source = Path.Combine(directory.Path, $"{Guid.NewGuid():N}.windowanchor.json");
        File.WriteAllText(source, JsonSerializer.Serialize(
            new WorkspaceTransferDocument { Mode = WorkspaceExportMode.ExactBackup, Workspace = workspace },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return source;
    }
}
