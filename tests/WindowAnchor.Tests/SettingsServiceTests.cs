using System.Text.Json;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void V1_settings_migrate_name_references_to_ids_and_are_idempotent()
    {
        using var directory = new TestDirectory();
        directory.CopyFixture("current.workspace.json", @"workspaces\legacy-current.workspace.json");
        directory.CopyFixture("current-v3.workspace.json", @"workspaces\Stable Workspace.workspace.json");
        string settingsPath = directory.CopyFixture("legacy-v1.settings.json", "settings.json");
        var storage = new StorageService(directory.Path);
        var workspaces = storage.LoadAllWorkspaces();
        var legacy = workspaces.Single(workspace => workspace.Name == "Characterization Fixture");
        var current = workspaces.Single(workspace => workspace.Name == "Stable Workspace");

        var service = new SettingsService(settingsPath, storage);

        Assert.False(service.IsSaveBlocked);
        Assert.Null(service.LastLoadError);
        Assert.Equal(AppSettings.CurrentSchemaVersion, service.Settings.SchemaVersion);
        Assert.Equal(legacy.WorkspaceId, service.Settings.DefaultWorkspaceId);
        Assert.Equal([current.WorkspaceId, legacy.WorkspaceId], service.Settings.WorkspaceOrderIds);
        Assert.Null(service.Settings.DefaultWorkspaceName);
        Assert.Null(service.Settings.WorkspaceOrder);
        Assert.Equal("Main Desk", service.Settings.MonitorAliases!["1234:5678:0"]);

        string onceMigrated = File.ReadAllText(settingsPath);
        service.Load();
        Assert.Equal(onceMigrated, File.ReadAllText(settingsPath));
        Assert.DoesNotContain("defaultWorkspaceName", onceMigrated);
        Assert.DoesNotContain("\"workspaceOrder\"", onceMigrated);
    }

    [Fact]
    public void Rename_does_not_break_default_or_order_references()
    {
        using var directory = new TestDirectory();
        directory.CopyFixture("current-v3.workspace.json", @"workspaces\Stable Workspace.workspace.json");
        string settingsPath = directory.CopyFixture("current-v3.settings.json", "settings.json");
        var storage = new StorageService(directory.Path);
        var workspace = Assert.Single(storage.LoadAllWorkspaces());
        var settings = new SettingsService(settingsPath, storage);
        string workspaceId = workspace.WorkspaceId;

        storage.RenameWorkspace(workspace, "Renamed Workspace");
        settings.Load();

        Assert.Equal(workspaceId, Assert.Single(storage.LoadAllWorkspaces()).WorkspaceId);
        Assert.Equal(workspaceId, settings.Settings.DefaultWorkspaceId);
        Assert.Equal(workspaceId, Assert.Single(settings.Settings.WorkspaceOrderIds!));
    }

    [Fact]
    public void New_settings_save_carries_current_schema_and_only_id_based_references()
    {
        using var directory = new TestDirectory();
        var storage = new StorageService(directory.Path);
        string settingsPath = System.IO.Path.Combine(directory.Path, "settings.json");
        var service = new SettingsService(settingsPath, storage);
        string workspaceId = Guid.NewGuid().ToString("D");
        service.Settings.DefaultWorkspaceId = workspaceId;
        service.Settings.WorkspaceOrderIds = [workspaceId];
        service.Settings.DiagnosticLogLevel = DiagnosticLogLevel.Warning;

        service.Save();

        using var json = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.Equal(3, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(workspaceId, json.RootElement.GetProperty("defaultWorkspaceId").GetString());
        Assert.Equal(
            workspaceId,
            json.RootElement.GetProperty("workspaceOrderIds")[0].GetString());
        Assert.Equal(
            (int)DiagnosticLogLevel.Warning,
            json.RootElement.GetProperty("diagnosticLogLevel").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("defaultWorkspaceName", out _));
        Assert.False(json.RootElement.TryGetProperty("workspaceOrder", out _));

        var reloaded = new SettingsService(settingsPath, storage);
        Assert.Equal(DiagnosticLogLevel.Warning, reloaded.Settings.DiagnosticLogLevel);
    }

    [Theory]
    [InlineData("unsupported-future.settings.json", "Unsupported future settings")]
    [InlineData("corrupt.settings.json", null)]
    public void Unreadable_settings_are_visible_and_block_saves_without_overwriting(
        string fixture,
        string? expectedMessage)
    {
        using var directory = new TestDirectory();
        string settingsPath = directory.CopyFixture(fixture, "settings.json");
        string original = File.ReadAllText(settingsPath);
        var service = new SettingsService(settingsPath, new StorageService(directory.Path));

        Assert.True(service.IsSaveBlocked);
        Assert.NotNull(service.LastLoadError);
        if (expectedMessage != null)
            Assert.Contains(expectedMessage, service.LastLoadError);

        service.Settings.NotificationsEnabled = false;
        service.Save();
        Assert.Equal(original, File.ReadAllText(settingsPath));
    }

    [Fact]
    public void Current_settings_load_without_rewrite()
    {
        using var directory = new TestDirectory();
        string settingsPath = directory.CopyFixture("current-v3.settings.json", "settings.json");
        string original = File.ReadAllText(settingsPath);

        var service = new SettingsService(settingsPath, new StorageService(directory.Path));

        Assert.False(service.IsSaveBlocked);
        Assert.Equal("11111111-1111-4111-8111-111111111111", service.Settings.DefaultWorkspaceId);
        Assert.Equal(original, File.ReadAllText(settingsPath));
    }

    [Fact]
    public void V2_settings_migrate_to_current_schema_without_losing_preferences()
    {
        using var directory = new TestDirectory();
        string settingsPath = directory.CopyFixture("current-v2.settings.json", "settings.json");

        var service = new SettingsService(settingsPath, new StorageService(directory.Path));

        Assert.Equal(AppSettings.CurrentSchemaVersion, service.Settings.SchemaVersion);
        Assert.Equal("11111111-1111-4111-8111-111111111111", service.Settings.DefaultWorkspaceId);
        Assert.Null(service.Settings.WindowMatchHints);
        Assert.Contains("\"schemaVersion\": 3", File.ReadAllText(settingsPath));
    }

    [Fact]
    public void Learned_matches_use_stable_ids_never_runtime_handles_and_can_be_cleared()
    {
        using var directory = new TestDirectory();
        string settingsPath = Path.Combine(directory.Path, "settings.json");
        var service = new SettingsService(settingsPath, new StorageService(directory.Path));
        const string workspaceId = "11111111-1111-4111-8111-111111111111";
        const string entryId = "22222222-2222-4222-8222-222222222222";
        var identity = new WindowIdentityHint
        {
            ExecutablePath = @"c:\apps\editor.exe",
            ProcessName = "editor",
            WindowClassName = "EditorWindow",
            TitleTokens = ["alpha", "north", "report"]
        };

        service.RememberWindowMatch(workspaceId, entryId, identity);

        WindowMatchHint hint = Assert.Single(service.GetWindowMatchHints(workspaceId));
        Assert.Equal(entryId, hint.EntryId);
        string json = File.ReadAllText(settingsPath);
        Assert.DoesNotContain("windowHandle", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("processId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hwnd", json, StringComparison.OrdinalIgnoreCase);

        var reloaded = new SettingsService(settingsPath, new StorageService(directory.Path));
        Assert.Single(reloaded.Settings.WindowMatchHints!);
        Assert.Equal(1, reloaded.ClearAllWindowMatches());
        Assert.Null(reloaded.Settings.WindowMatchHints);
        Assert.Empty(new SettingsService(settingsPath, new StorageService(directory.Path))
            .Settings.WindowMatchHints ?? []);
    }

    [Fact]
    public void Interrupted_settings_commit_preserves_the_previous_valid_document()
    {
        using var directory = new TestDirectory();
        _ = new StorageService(directory.Path); // Creates the legacy-import marker before injection.
        string settingsPath = directory.CopyFixture("current-v3.settings.json", "settings.json");
        string original = File.ReadAllText(settingsPath);
        var failingStorage = new StorageService(
            directory.Path,
            new AtomicFileWriter((stage, destination) =>
            {
                if (stage == AtomicWriteStage.TemporaryFileFlushed &&
                    destination.Equals(settingsPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Injected settings interruption");
                }
            }));
        var service = new SettingsService(settingsPath, failingStorage);
        service.Settings.NotificationsEnabled = !service.Settings.NotificationsEnabled;

        service.Save();

        Assert.Equal(original, File.ReadAllText(settingsPath));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }
}
