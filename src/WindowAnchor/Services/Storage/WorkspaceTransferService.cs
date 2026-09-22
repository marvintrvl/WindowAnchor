using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

public enum WorkspaceExportMode { ExactBackup, PortableRedacted }
public enum WorkspaceImportCollisionPolicy { Clone, Reject }
public sealed record WorkspaceTransferDocument
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public WorkspaceExportMode Mode { get; init; }
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
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        WorkspaceSnapshot staged = mode == WorkspaceExportMode.ExactBackup ? workspace : Portable(workspace);
        string json = JsonSerializer.Serialize(new WorkspaceTransferDocument { Mode = mode, Workspace = staged }, JsonOptions);
        _storage.AtomicWriter.WriteAllText(destinationPath, json);
    }

    public WorkspaceSnapshot Import(string sourcePath, WorkspaceImportCollisionPolicy collisionPolicy = WorkspaceImportCollisionPolicy.Clone)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists || info.Length > MaximumBytes) throw new InvalidDataException("Workspace import is missing or exceeds the size limit.");
        WorkspaceTransferDocument document = JsonSerializer.Deserialize<WorkspaceTransferDocument>(File.ReadAllText(sourcePath), JsonOptions)
            ?? throw new InvalidDataException("Workspace import deserialized to null.");
        if (document.SchemaVersion != WorkspaceTransferDocument.CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported workspace transfer schema version.");
        WorkspaceSchemaMigrator.Validate(document.Workspace);
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

    private static WorkspaceSnapshot Portable(WorkspaceSnapshot workspace)
    {
        string json = JsonSerializer.Serialize(workspace, JsonOptions);
        WorkspaceSnapshot copy = JsonSerializer.Deserialize<WorkspaceSnapshot>(json, JsonOptions)!;
        copy.MonitorFingerprint = "";
        foreach (MonitorInfo monitor in copy.Monitors) RedactMonitor(monitor);
        foreach (WorkspaceEntry entry in copy.Entries)
        {
            entry.ExecutablePath = entry.LogicalExecutablePath ?? "";
            entry.FilePath = entry.LogicalFilePath;
            entry.LaunchArg = entry.LogicalLaunchArg;
            entry.AppUserModelId = "";
            entry.BrowserUrl = "";
            entry.WebAppShortcutPath = null;
            entry.WebAppLaunchTarget = null;
            entry.WebAppLaunchArguments = null;
            entry.MonitorId = "";
            entry.MonitorName = "";
            RedactPosition(entry.Position);
        }
        foreach (LayoutVariant variant in copy.LayoutVariants)
        {
            variant.MonitorFingerprint = "";
            variant.DeskProfileId = null;
            foreach (MonitorInfo monitor in variant.Monitors) RedactMonitor(monitor);
            foreach (LayoutVariantPlacement placement in variant.Placements)
            {
                placement.MonitorId = "";
                placement.MonitorName = "";
                RedactPosition(placement.Position);
            }
        }
        copy.BrowserSessions.Clear();
        copy.Checkpoint = null;
        return copy;
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

    private static void RedactPosition(WindowRecord position)
    {
        position.ExecutablePath = "";
        position.FolderPath = "";
        position.TitleSnippet = "";
        position.AppUserModelId = "";
        position.BrowserUrl = "";
        position.MonitorId = "";
        position.MonitorName = "";
    }
}
