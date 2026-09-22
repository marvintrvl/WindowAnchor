using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Owns topology-specific layout variants without changing workspace identity.</summary>
internal sealed class WorkspaceLayoutVariantService
{
    private readonly StorageService _storage;
    private readonly IMonitorInventory _monitors;

    internal WorkspaceLayoutVariantService(StorageService storage, IMonitorInventory monitors)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
    }

    internal WorkspaceSnapshot? FindByFingerprint(string fingerprint) =>
        _storage.LoadAllWorkspaces()
            .Where(workspace => workspace.LayoutVariants.Any(variant =>
                string.Equals(variant.MonitorFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(workspace => workspace.SavedAt)
            .FirstOrDefault();

    internal LayoutVariant Add(WorkspaceSnapshot workspace, string name, string? deskProfileId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        workspace.EnsureLayoutVariants();
        var variant = new LayoutVariant
        {
            Name = name.Trim(),
            MonitorFingerprint = _monitors.GetCurrentMonitorFingerprint(),
            Monitors = _monitors.GetCurrentMonitors(),
            Placements = workspace.Entries.Select(entry => new LayoutVariantPlacement
            {
                EntryId = entry.EntryId,
                Position = entry.Position,
                MonitorId = entry.MonitorId,
                MonitorIndex = entry.MonitorIndex,
                MonitorName = entry.MonitorName
            }).ToList(),
            DeskProfileId = deskProfileId,
            SavedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow
        };
        workspace.LayoutVariants.Add(variant);
        _storage.SaveWorkspace(workspace);
        return variant;
    }

    internal void Rename(WorkspaceSnapshot workspace, string variantId, string name)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Find(workspace, variantId).Name = name.Trim();
        _storage.SaveWorkspace(workspace);
    }

    internal void Delete(WorkspaceSnapshot workspace, string variantId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.LayoutVariants.Count <= 1)
            throw new InvalidOperationException("A workspace must retain at least one layout variant.");
        workspace.LayoutVariants.Remove(Find(workspace, variantId));
        _storage.SaveWorkspace(workspace);
    }

    internal WorkspaceLayoutVariantSelection SelectForRestore(WorkspaceSnapshot workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.EnsureLayoutVariants();
        List<MonitorInfo> currentMonitors = _monitors.GetCurrentMonitors();
        LayoutVariant? selected = LayoutVariantSelector.Select(
            workspace,
            _monitors.GetCurrentMonitorFingerprint(),
            currentMonitors.Select(monitor => monitor.MonitorId));
        if (selected is null)
            return new WorkspaceLayoutVariantSelection(workspace, null);

        WorkspaceSnapshot projected = JsonSerializer.Deserialize<WorkspaceSnapshot>(
            JsonSerializer.Serialize(workspace))
            ?? throw new InvalidOperationException("Could not project the selected layout variant.");
        Dictionary<string, LayoutVariantPlacement> placements = selected.Placements
            .ToDictionary(placement => placement.EntryId, StringComparer.OrdinalIgnoreCase);
        projected.MonitorFingerprint = selected.MonitorFingerprint;
        projected.Monitors = selected.Monitors;
        foreach (WorkspaceEntry entry in projected.Entries)
        {
            if (!placements.TryGetValue(entry.EntryId, out LayoutVariantPlacement? placement)) continue;
            entry.Position = placement.Position;
            entry.MonitorId = placement.MonitorId;
            entry.MonitorIndex = placement.MonitorIndex;
            entry.MonitorName = placement.MonitorName;
        }
        return new WorkspaceLayoutVariantSelection(projected, selected);
    }

    internal void MarkUsed(WorkspaceSnapshot snapshot, RestorePlan plan, RestoreExecutionResult result)
    {
        if (result.Status != RestoreExecutionStatus.Completed ||
            string.IsNullOrWhiteSpace(plan.LayoutVariantId) || snapshot.Checkpoint is not null)
        {
            return;
        }

        LayoutVariant? variant = snapshot.LayoutVariants.FirstOrDefault(item =>
            item.VariantId.Equals(plan.LayoutVariantId, StringComparison.OrdinalIgnoreCase));
        if (variant is null) return;

        bool isNamedWorkspace = _storage.LoadAllWorkspaces().Any(workspace =>
            workspace.WorkspaceId.Equals(snapshot.WorkspaceId, StringComparison.OrdinalIgnoreCase));
        if (!isNamedWorkspace) return;

        variant.LastUsedAt = DateTime.UtcNow;
        _storage.SaveWorkspace(snapshot);
    }

    private static LayoutVariant Find(WorkspaceSnapshot workspace, string variantId) =>
        workspace.LayoutVariants.Single(variant =>
            variant.VariantId.Equals(variantId, StringComparison.OrdinalIgnoreCase));
}

internal sealed record WorkspaceLayoutVariantSelection(
    WorkspaceSnapshot Snapshot,
    LayoutVariant? Variant);
