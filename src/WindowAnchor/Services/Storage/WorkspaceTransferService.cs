using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

public enum WorkspaceExportMode { ExactBackup, PortableRedacted }
public enum WorkspaceImportCollisionPolicy { Clone, Reject }
public enum WorkspaceImportConflictKind { None, Name, WorkspaceId, NameAndWorkspaceId }

/// <summary>
/// Explicit metadata selection for a single local workspace export. No option ever includes
/// settings, credentials, cookies, document contents, or native-host registration data.
/// </summary>
public sealed record WorkspaceExportOptions
{
    public WorkspaceExportMode Mode { get; init; } = WorkspaceExportMode.ExactBackup;
    public bool IncludeLayout { get; init; } = true;
    public bool IncludeApplicationIdentities { get; init; } = true;
    public bool IncludeFilesAndFolders { get; init; } = true;
    public bool IncludeBrowserUrls { get; init; } = true;
    public bool IncludeMachineIdentifiers { get; init; } = true;
    public bool IncludeLogicalPathAliases { get; init; } = true;

    public static WorkspaceExportOptions ExactBackup() => new();

    public static WorkspaceExportOptions PortableRedacted() => new()
    {
        Mode = WorkspaceExportMode.PortableRedacted,
        IncludeLayout = false,
        IncludeApplicationIdentities = true,
        IncludeFilesAndFolders = false,
        IncludeBrowserUrls = false,
        IncludeMachineIdentifiers = false,
        IncludeLogicalPathAliases = true
    };
}

/// <summary>Read-only result shown before an import can write local workspace storage.</summary>
public sealed record WorkspaceImportPreview(
    WorkspaceSnapshot Workspace,
    WorkspaceExportMode Mode,
    WorkspaceExportOptions Options,
    string ContentHash,
    WorkspaceImportConflictKind ConflictKind,
    bool WasMigrated)
{
    public bool HasConflict => ConflictKind != WorkspaceImportConflictKind.None;
}

public sealed record WorkspaceTransferDocument
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public WorkspaceExportMode Mode { get; init; }
    public WorkspaceExportOptions Options { get; init; } = WorkspaceExportOptions.ExactBackup();
    public WorkspaceSnapshot Workspace { get; init; } = new();
}

/// <summary>Validates, stages, and transfers one named workspace without exporting application settings.</summary>
public sealed class WorkspaceTransferService
{
    private const int MaximumBytes = 5 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly StorageService _storage;

    public WorkspaceTransferService(StorageService storage) => _storage = storage ?? throw new ArgumentNullException(nameof(storage));

    public void Export(WorkspaceSnapshot workspace, WorkspaceExportMode mode, string destinationPath)
        => Export(workspace, mode == WorkspaceExportMode.ExactBackup
            ? WorkspaceExportOptions.ExactBackup()
            : WorkspaceExportOptions.PortableRedacted(), destinationPath);

    public void Export(WorkspaceSnapshot workspace, WorkspaceExportOptions options, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ValidateOptions(options);
        WorkspaceSnapshot staged = ApplyExportOptions(workspace, options);
        string json = JsonSerializer.Serialize(new WorkspaceTransferDocument
        {
            Mode = options.Mode,
            Options = options,
            Workspace = staged
        }, JsonOptions);
        _storage.AtomicWriter.WriteAllText(destinationPath, json);
    }

    /// <summary>Stages, migrates, validates, and compares a transfer without mutating storage.</summary>
    public WorkspaceImportPreview PreviewImport(string sourcePath)
    {
        StagedWorkspaceTransfer staged = ReadAndValidate(sourcePath);
        WorkspaceSnapshot[] existingWorkspaces = _storage.LoadAllWorkspaces().ToArray();
        bool identityCollision = existingWorkspaces.Any(existing => existing.WorkspaceId.Equals(
            staged.Document.Workspace.WorkspaceId,
            StringComparison.OrdinalIgnoreCase));
        bool nameCollision = existingWorkspaces.Any(existing => existing.Name.Equals(
            staged.Document.Workspace.Name,
            StringComparison.OrdinalIgnoreCase));
        WorkspaceImportConflictKind conflict = identityCollision && nameCollision
            ? WorkspaceImportConflictKind.NameAndWorkspaceId
            : identityCollision ? WorkspaceImportConflictKind.WorkspaceId
            : nameCollision ? WorkspaceImportConflictKind.Name
            : WorkspaceImportConflictKind.None;
        return new WorkspaceImportPreview(
            staged.Document.Workspace,
            staged.Document.Mode,
            staged.Document.Options,
            staged.ContentHash,
            conflict,
            staged.WasMigrated);
    }

    public WorkspaceSnapshot Import(
        string sourcePath,
        WorkspaceImportCollisionPolicy collisionPolicy = WorkspaceImportCollisionPolicy.Clone,
        string? expectedContentHash = null)
    {
        StagedWorkspaceTransfer staged = ReadAndValidate(sourcePath);
        if (!string.IsNullOrWhiteSpace(expectedContentHash) &&
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedContentHash),
                Convert.FromHexString(staged.ContentHash)))
        {
            throw new InvalidDataException("The import file changed after preview. Review it again before importing.");
        }

        WorkspaceTransferDocument document = staged.Document;
        WorkspaceSnapshot[] existingWorkspaces = _storage.LoadAllWorkspaces().ToArray();
        bool identityCollision = existingWorkspaces.Any(existing => existing.WorkspaceId.Equals(
            document.Workspace.WorkspaceId,
            StringComparison.OrdinalIgnoreCase));
        if (identityCollision && collisionPolicy == WorkspaceImportCollisionPolicy.Reject)
            throw new InvalidDataException("The imported workspace ID already exists locally.");
        if (identityCollision)
            document.Workspace.WorkspaceId = Guid.NewGuid().ToString("D");

        document.Workspace.Name = UniqueImportedName(document.Workspace.Name, existingWorkspaces);
        _storage.NamedWorkspaces.Save(document.Workspace);
        return document.Workspace;
    }

    private static StagedWorkspaceTransfer ReadAndValidate(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var info = new FileInfo(sourcePath);
        if (!info.Exists || info.Length > MaximumBytes)
            throw new InvalidDataException("Workspace import is missing or exceeds the size limit.");

        string json = File.ReadAllText(sourcePath);
        string contentHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("Workspace transfer document root must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new JsonException("Workspace transfer document is not valid JSON.", exception);
        }
        int version = JsonMigrationPipeline.ReadVersion(root, assumedVersion: 1);
        if (version > WorkspaceTransferDocument.CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported future workspace transfer schema version.");
        if (version <= 0)
            throw new InvalidDataException("Invalid workspace transfer schema version.");

        bool wasMigrated = false;
        while (version < WorkspaceTransferDocument.CurrentSchemaVersion)
        {
            if (version != 1)
                throw new InvalidDataException($"No workspace transfer migration is registered from schema version {version}.");
            MigrateV1ToV2(root);
            version++;
            root["schemaVersion"] = version;
            wasMigrated = true;
        }

        WorkspaceTransferDocument document = root.Deserialize<WorkspaceTransferDocument>(JsonOptions)
            ?? throw new InvalidDataException("Workspace import deserialized to null.");
        if (document.Workspace is null)
            throw new InvalidDataException("Workspace import has no workspace payload.");
        if (!Enum.IsDefined(document.Mode))
            throw new InvalidDataException("Workspace import has an invalid export mode.");
        ValidateOptions(document.Options);
        if (document.Mode != document.Options.Mode)
            throw new InvalidDataException("Workspace import mode does not match its metadata options.");

        JsonNode? workspaceNode = root["workspace"];
        if (workspaceNode is null)
            throw new InvalidDataException("Workspace import has no workspace payload.");
        MigratedDocument<WorkspaceSnapshot> workspace = WorkspaceSchemaMigrator.Migrate(
            workspaceNode.ToJsonString(JsonOptions),
            Path.GetFileName(sourcePath),
            JsonOptions);
        wasMigrated |= workspace.WasMigrated;
        document = document with
        {
            SchemaVersion = WorkspaceTransferDocument.CurrentSchemaVersion,
            Workspace = workspace.Value
        };
        return new StagedWorkspaceTransfer(document, contentHash, wasMigrated);
    }

    private static void MigrateV1ToV2(JsonObject root)
    {
        WorkspaceExportMode mode = WorkspaceExportMode.ExactBackup;
        if (root["mode"] is JsonValue value && value.TryGetValue<int>(out int numericMode) &&
            Enum.IsDefined((WorkspaceExportMode)numericMode))
        {
            mode = (WorkspaceExportMode)numericMode;
        }
        root["options"] = JsonSerializer.SerializeToNode(
            mode == WorkspaceExportMode.ExactBackup
                ? WorkspaceExportOptions.ExactBackup()
                : WorkspaceExportOptions.PortableRedacted(),
            JsonOptions);
    }

    private static WorkspaceSnapshot ApplyExportOptions(WorkspaceSnapshot workspace, WorkspaceExportOptions options)
    {
        string json = JsonSerializer.Serialize(workspace, JsonOptions);
        WorkspaceSnapshot copy = JsonSerializer.Deserialize<WorkspaceSnapshot>(json, JsonOptions)!;

        if (options.Mode == WorkspaceExportMode.PortableRedacted)
            RedactPortableAbsolutePaths(copy);
        if (!options.IncludeMachineIdentifiers)
            RedactMachineIdentifiers(copy);
        if (!options.IncludeLayout)
            RedactLayout(copy);

        foreach (WorkspaceEntry entry in copy.Entries)
        {
            if (!options.IncludeApplicationIdentities) RedactApplicationIdentity(entry);
            if (!options.IncludeFilesAndFolders) RedactFilesAndFolders(entry);
            if (!options.IncludeBrowserUrls) RedactBrowserUrls(entry);
            if (!options.IncludeLogicalPathAliases)
            {
                RedactLogicalAliases(entry);
                if (options.Mode == WorkspaceExportMode.PortableRedacted)
                    RedactPortableAliasReferences(entry);
            }
        }
        if (!options.IncludeBrowserUrls)
            copy.BrowserSessions.Clear();
        copy.Checkpoint = null;
        return copy;
    }

    private static void ValidateOptions(WorkspaceExportOptions? options)
    {
        if (options is null || !Enum.IsDefined(options.Mode))
            throw new InvalidDataException("Workspace transfer options are invalid.");
    }

    private static void RedactMachineIdentifiers(WorkspaceSnapshot workspace)
    {
        workspace.MonitorFingerprint = "";
        workspace.VirtualDesktops.Clear();
        foreach (MonitorInfo monitor in workspace.Monitors) RedactMonitor(monitor);
        foreach (WorkspaceEntry entry in workspace.Entries)
        {
            entry.MonitorId = "";
            entry.MonitorName = "";
            entry.Position.MonitorId = "";
            entry.Position.MonitorName = "";
        }
        foreach (LayoutVariant variant in workspace.LayoutVariants)
        {
            variant.MonitorFingerprint = "";
            variant.DeskProfileId = null;
            foreach (MonitorInfo monitor in variant.Monitors) RedactMonitor(monitor);
            foreach (LayoutVariantPlacement placement in variant.Placements)
            {
                placement.MonitorId = "";
                placement.MonitorName = "";
                placement.Position.MonitorId = "";
                placement.Position.MonitorName = "";
            }
        }
        foreach (BrowserSession session in workspace.BrowserSessions)
        {
            session.ProfileKey = "";
            session.ProfileLabel = "";
            session.BrowserWindowId = "";
            session.LinkedEntryId = "";
            session.MonitorId = "";
        }
    }

    private static void RedactLayout(WorkspaceSnapshot workspace)
    {
        workspace.Monitors.Clear();
        workspace.MonitorFingerprint = "";
        foreach (WorkspaceEntry entry in workspace.Entries)
        {
            entry.MonitorId = "";
            entry.MonitorName = "";
            entry.MonitorIndex = 0;
            entry.Position.ShowCmd = 1;
            entry.Position.NormalLeft = 0;
            entry.Position.NormalTop = 0;
            entry.Position.NormalRight = 0;
            entry.Position.NormalBottom = 0;
            entry.Position.SavedDpi = 96;
            entry.Position.NormalizedLayout = null;
            entry.Position.TitleSnippet = "";
            entry.Position.MonitorId = "";
            entry.Position.MonitorName = "";
            entry.Position.MonitorIndex = 0;
            entry.Position.VirtualDesktopId = "";
        }
        workspace.LayoutVariants.Clear();
        workspace.EnsureLayoutVariants();
    }

    /// <summary>Portable exports may retain logical aliases, never their local absolute fallback paths.</summary>
    private static void RedactPortableAbsolutePaths(WorkspaceSnapshot workspace)
    {
        foreach (WorkspaceEntry entry in workspace.Entries)
        {
            entry.ExecutablePath = entry.LogicalExecutablePath ?? "";
            entry.FilePath = entry.LogicalFilePath;
            entry.LaunchArg = entry.LogicalLaunchArg;
            entry.Position.ExecutablePath = entry.LogicalExecutablePath ?? "";
            entry.Position.FolderPath = "";
            entry.Position.TitleSnippet = "";
            entry.ExplorerTabPaths.Clear();
            entry.ExplorerActiveTabIndex = 0;
            entry.TerminalTabs.Clear();
            entry.TerminalActiveTabIndex = 0;
            entry.WebAppShortcutPath = null;
            entry.WebAppLaunchTarget = null;
            entry.WebAppLaunchArguments = null;
        }
    }

    private static void RedactApplicationIdentity(WorkspaceEntry entry)
    {
        entry.ExecutablePath = "";
        entry.ProcessName = "";
        entry.WindowClassName = "";
        entry.AppUserModelId = "";
        entry.IsWebApp = false;
        entry.WebAppName = "";
        entry.WebAppShortcutPath = null;
        entry.WebAppLaunchTarget = null;
        entry.WebAppLaunchArguments = null;
        entry.Position.ExecutablePath = "";
        entry.Position.ProcessName = "";
        entry.Position.ClassName = "";
        entry.Position.AppUserModelId = "";
    }

    private static void RedactFilesAndFolders(WorkspaceEntry entry)
    {
        entry.FilePath = null;
        entry.LaunchArg = null;
        entry.EditorWorkspaceKind = EditorWorkspaceKind.None;
        entry.ExplorerTabPaths.Clear();
        entry.ExplorerActiveTabIndex = 0;
        entry.TerminalTabs.Clear();
        entry.TerminalActiveTabIndex = 0;
        entry.Position.FolderPath = "";
    }

    private static void RedactBrowserUrls(WorkspaceEntry entry)
    {
        entry.BrowserUrl = "";
        entry.Position.BrowserUrl = "";
    }

    private static void RedactLogicalAliases(WorkspaceEntry entry)
    {
        entry.LogicalExecutablePath = null;
        entry.LogicalFilePath = null;
        entry.LogicalLaunchArg = null;
    }

    private static void RedactPortableAliasReferences(WorkspaceEntry entry)
    {
        entry.ExecutablePath = "";
        entry.FilePath = null;
        entry.LaunchArg = null;
        entry.Position.ExecutablePath = "";
    }

    private static string UniqueImportedName(
        string requestedName,
        IReadOnlyCollection<WorkspaceSnapshot> existingWorkspaces)
    {
        string baseName = string.IsNullOrWhiteSpace(requestedName) ? "Imported workspace" : requestedName;
        var names = existingWorkspaces
            .Select(workspace => workspace.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;

        string candidate = $"{baseName} (Imported)";
        for (int suffix = 2; names.Contains(candidate); suffix++)
            candidate = $"{baseName} (Imported {suffix})";
        return candidate;
    }

    private static void RedactMonitor(MonitorInfo monitor)
    {
        monitor.MonitorId = "";
        monitor.FriendlyName = "";
        monitor.DeviceName = "";
    }

    private sealed record StagedWorkspaceTransfer(
        WorkspaceTransferDocument Document,
        string ContentHash,
        bool WasMigrated);
}
