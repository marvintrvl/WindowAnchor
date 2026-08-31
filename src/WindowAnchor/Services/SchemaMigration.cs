using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

internal sealed record MigratedDocument<T>(T Value, string Json, bool WasMigrated);

internal sealed class UnsupportedSchemaVersionException : Exception
{
    internal UnsupportedSchemaVersionException(string documentType, int found, int supported)
        : base($"Unsupported future {documentType} schema version {found}; this build supports up to {supported}.")
    {
    }
}

internal static class StableDocumentId
{
    internal static string Create(string scope, params string?[] parts)
    {
        string input = string.Join('\u001f', new[] { scope }.Concat(parts.Select(part => part ?? "")));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }
}

internal static class JsonMigrationPipeline
{
    internal static bool Apply(
        JsonObject root,
        string documentType,
        int assumedVersion,
        int currentVersion,
        IReadOnlyDictionary<int, Action<JsonObject>> migrations)
    {
        int version = ReadVersion(root, assumedVersion);
        if (version > currentVersion)
            throw new UnsupportedSchemaVersionException(documentType, version, currentVersion);
        if (version <= 0)
            throw new InvalidDataException($"Invalid {documentType} schema version {version}.");

        bool migrated = false;
        while (version < currentVersion)
        {
            if (!migrations.TryGetValue(version, out var migrate))
                throw new InvalidDataException(
                    $"No {documentType} migration is registered from schema version {version}.");

            migrate(root);
            version++;
            root["schemaVersion"] = version;
            migrated = true;
        }

        return migrated;
    }

    internal static int ReadVersion(JsonObject root, int assumedVersion)
    {
        if (!root.TryGetPropertyValue("schemaVersion", out var node) || node == null)
            return assumedVersion;

        if (node is JsonValue value && value.TryGetValue<int>(out int version))
            return version;

        throw new InvalidDataException("The schemaVersion property must be an integer.");
    }
}

internal static class WorkspaceSchemaMigrator
{
    private const int LegacyWorkspaceVersion = 2;

    internal static MigratedDocument<WorkspaceSnapshot> Migrate(
        string json,
        string sourceIdentity,
        JsonSerializerOptions options)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("Workspace document root must be a JSON object.");

        var migrations = new Dictionary<int, Action<JsonObject>>
        {
            [LegacyWorkspaceVersion] = node => MigrateV2ToV3(node, sourceIdentity)
        };
        bool migrated = JsonMigrationPipeline.Apply(
            root,
            "workspace",
            LegacyWorkspaceVersion,
            WorkspaceSnapshot.CurrentSchemaVersion,
            migrations);

        ValidatePersistedIdentities(root);
        var snapshot = root.Deserialize<WorkspaceSnapshot>(options)
            ?? throw new InvalidDataException("Workspace document deserialized to null.");
        Validate(snapshot);

        return new MigratedDocument<WorkspaceSnapshot>(
            snapshot,
            root.ToJsonString(options),
            migrated);
    }

    internal static void Validate(WorkspaceSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != WorkspaceSnapshot.CurrentSchemaVersion)
            throw new InvalidDataException($"Workspace schema version must be {WorkspaceSnapshot.CurrentSchemaVersion}.");
        if (!Guid.TryParse(snapshot.WorkspaceId, out _))
            throw new InvalidDataException("WorkspaceId must be a GUID.");
        if (snapshot.Entries == null)
            throw new InvalidDataException("Workspace entries cannot be null.");

        var entryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshot.Entries)
        {
            if (!Guid.TryParse(entry.EntryId, out _))
                throw new InvalidDataException("Every EntryId must be a GUID.");
            if (!entryIds.Add(entry.EntryId))
                throw new InvalidDataException($"Duplicate EntryId '{entry.EntryId}'.");
        }
    }

    internal static WorkspaceSnapshot CreateFromLegacyProfile(
        string sourceIdentity,
        string name,
        string fingerprint,
        DateTime savedAt,
        IReadOnlyList<WindowRecord> windows)
    {
        string workspaceId = StableDocumentId.Create(
            "legacy-profile-workspace",
            Path.GetFileName(sourceIdentity).ToLowerInvariant(),
            name,
            fingerprint,
            savedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        var entries = windows.Select((window, index) => new WorkspaceEntry
        {
            EntryId         = CreateEntryId(workspaceId, index, window),
            ExecutablePath  = window.ExecutablePath,
            ProcessName     = window.ProcessName,
            WindowClassName = window.ClassName,
            FilePath        = null,
            FileConfidence  = 0,
            FileSource      = "NONE",
            LaunchArg       = null,
            Position        = window,
            MonitorId       = "",
            MonitorIndex    = 0,
            MonitorName     = "",
        }).ToList();

        return new WorkspaceSnapshot
        {
            SchemaVersion      = WorkspaceSnapshot.CurrentSchemaVersion,
            WorkspaceId       = workspaceId,
            Name              = name,
            MonitorFingerprint = fingerprint,
            SavedAt           = savedAt,
            SavedWithFiles    = false,
            Monitors          = new List<MonitorInfo>(),
            Entries           = entries,
        };
    }

    private static void MigrateV2ToV3(JsonObject root, string sourceIdentity)
    {
        string name = GetString(root, "name");
        string fingerprint = GetString(root, "monitorFingerprint");
        string savedAt = GetString(root, "savedAt");
        string workspaceId = GetValidGuid(root, "workspaceId")
            ?? StableDocumentId.Create(
                "workspace-v2",
                Path.GetFileName(sourceIdentity).ToLowerInvariant(),
                name,
                fingerprint,
                savedAt);
        root["workspaceId"] = workspaceId;

        if (!root.TryGetPropertyValue("entries", out var entriesNode) || entriesNode == null)
        {
            root["entries"] = new JsonArray();
            return;
        }
        if (entriesNode is not JsonArray entries) return;
        for (int index = 0; index < entries.Count; index++)
        {
            if (entries[index] is not JsonObject entry) continue;
            entry["entryId"] = GetValidGuid(entry, "entryId")
                ?? StableDocumentId.Create(
                    "workspace-entry-v2",
                    workspaceId,
                    index.ToString(CultureInfo.InvariantCulture),
                    GetString(entry, "executablePath"),
                    GetString(entry, "launchArg"),
                    GetString(entry["position"] as JsonObject, "titleSnippet"));
        }
    }

    private static void ValidatePersistedIdentities(JsonObject root)
    {
        if (!Guid.TryParse(GetString(root, "workspaceId"), out _))
            throw new InvalidDataException("WorkspaceId must be present and contain a GUID.");

        if (root["entries"] is not JsonArray entries)
            throw new InvalidDataException("Workspace entries must be a JSON array.");

        foreach (var node in entries)
        {
            if (node is not JsonObject entry || !Guid.TryParse(GetString(entry, "entryId"), out _))
                throw new InvalidDataException("Every workspace entry must persist a GUID EntryId.");
        }
    }

    private static string CreateEntryId(string workspaceId, int index, WindowRecord window) =>
        StableDocumentId.Create(
            "legacy-profile-entry",
            workspaceId,
            index.ToString(CultureInfo.InvariantCulture),
            window.ExecutablePath,
            window.TitleSnippet);

    private static string? GetValidGuid(JsonObject? node, string propertyName)
    {
        string value = GetString(node, propertyName);
        return Guid.TryParse(value, out _) ? value : null;
    }

    private static string GetString(JsonObject? node, string propertyName)
    {
        if (node?[propertyName] is JsonValue value && value.TryGetValue<string>(out string? result))
            return result ?? "";
        return "";
    }
}

internal static class SettingsSchemaMigrator
{
    private const int LegacySettingsVersion = 1;

    internal static int ReadVersion(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("Settings document root must be a JSON object.");
        int version = JsonMigrationPipeline.ReadVersion(root, LegacySettingsVersion);
        if (version > AppSettings.CurrentSchemaVersion)
            throw new UnsupportedSchemaVersionException(
                "settings",
                version,
                AppSettings.CurrentSchemaVersion);
        if (version <= 0)
            throw new InvalidDataException($"Invalid settings schema version {version}.");
        return version;
    }

    internal static MigratedDocument<AppSettings> Migrate(
        string json,
        IReadOnlyList<WorkspaceSnapshot> workspaces,
        JsonSerializerOptions options)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("Settings document root must be a JSON object.");

        var migrations = new Dictionary<int, Action<JsonObject>>
        {
            [LegacySettingsVersion] = node => MigrateV1ToV2(node, workspaces)
        };
        bool migrated = JsonMigrationPipeline.Apply(
            root,
            "settings",
            LegacySettingsVersion,
            AppSettings.CurrentSchemaVersion,
            migrations);

        var settings = root.Deserialize<AppSettings>(options)
            ?? throw new InvalidDataException("Settings document deserialized to null.");
        Validate(settings);

        return new MigratedDocument<AppSettings>(settings, root.ToJsonString(options), migrated);
    }

    internal static void Validate(AppSettings settings)
    {
        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
            throw new InvalidDataException($"Settings schema version must be {AppSettings.CurrentSchemaVersion}.");
        if (settings.DefaultWorkspaceId != null && !Guid.TryParse(settings.DefaultWorkspaceId, out _))
            throw new InvalidDataException("DefaultWorkspaceId must be a GUID when set.");
        if (!Enum.IsDefined(settings.DiagnosticLogLevel))
            throw new InvalidDataException("DiagnosticLogLevel is invalid.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in settings.WorkspaceOrderIds ?? [])
        {
            if (!Guid.TryParse(id, out _))
                throw new InvalidDataException("Every WorkspaceOrderIds value must be a GUID.");
            if (!seen.Add(id))
                throw new InvalidDataException($"Duplicate workspace order ID '{id}'.");
        }
    }

    private static void MigrateV1ToV2(JsonObject root, IReadOnlyList<WorkspaceSnapshot> workspaces)
    {
        string? defaultName = GetString(root, "defaultWorkspaceName");
        var defaultWorkspace = ResolveByName(defaultName, workspaces);
        if (defaultWorkspace != null)
            root["defaultWorkspaceId"] = defaultWorkspace.WorkspaceId;
        else
            root.Remove("defaultWorkspaceId");

        var orderIds = new List<string>();
        if (root["workspaceOrder"] is JsonArray legacyOrder)
        {
            foreach (var value in legacyOrder)
            {
                string? name = value?.GetValue<string>();
                var workspace = ResolveByName(name, workspaces);
                if (workspace != null && !orderIds.Contains(workspace.WorkspaceId, StringComparer.OrdinalIgnoreCase))
                    orderIds.Add(workspace.WorkspaceId);
            }
        }

        if (orderIds.Count > 0)
            root["workspaceOrderIds"] = new JsonArray(
                orderIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        else
            root.Remove("workspaceOrderIds");

        root.Remove("defaultWorkspaceName");
        root.Remove("workspaceOrder");
    }

    private static WorkspaceSnapshot? ResolveByName(
        string? name,
        IReadOnlyList<WorkspaceSnapshot> workspaces) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : workspaces
                .Where(workspace => workspace.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(workspace => workspace.SavedAt)
                .ThenBy(workspace => workspace.WorkspaceId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

    private static string? GetString(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonValue value && value.TryGetValue<string>(out string? result))
            return result;
        return null;
    }
}
