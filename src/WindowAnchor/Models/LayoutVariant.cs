using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowAnchor.Models;

/// <summary>Monitor-specific placements for one logical workspace without duplicating its app and browser context.</summary>
public sealed class LayoutVariant
{
    public string VariantId { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = "Default layout";
    public string MonitorFingerprint { get; set; } = "";
    public List<MonitorInfo> Monitors { get; set; } = new();
    public List<LayoutVariantPlacement> Placements { get; set; } = new();
    public string? DeskProfileId { get; set; }
    public DateTime SavedAt { get; set; }
    public DateTime LastUsedAt { get; set; }
}

/// <summary>Placement-only data owned by a <see cref="LayoutVariant"/>.</summary>
public sealed class LayoutVariantPlacement
{
    public string EntryId { get; set; } = "";
    public WindowRecord Position { get; set; } = new();
    public string MonitorId { get; set; } = "";
    public int MonitorIndex { get; set; }
    public string MonitorName { get; set; } = "";
}

/// <summary>Deterministically selects the layout that should supply restore geometry.</summary>
public static class LayoutVariantSelector
{
    public static LayoutVariant? Select(
        WorkspaceSnapshot workspace,
        string currentFingerprint,
        IEnumerable<string>? currentMonitorIds = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        LayoutVariant[] variants = workspace.LayoutVariants.ToArray();
        LayoutVariant? exact = variants
            .Where(variant => string.Equals(
                variant.MonitorFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(variant => variant.LastUsedAt)
            .ThenByDescending(variant => variant.SavedAt)
            .ThenBy(variant => variant.VariantId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (exact is not null) return exact;

        HashSet<string> currentIds = (currentMonitorIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return variants
            .Select(variant => new
            {
                Variant = variant,
                Overlap = variant.Monitors.Count(monitor => currentIds.Contains(monitor.MonitorId))
            })
            .OrderByDescending(item => item.Overlap)
            .ThenByDescending(item => item.Variant.LastUsedAt)
            .ThenByDescending(item => item.Variant.SavedAt)
            .ThenBy(item => item.Variant.VariantId, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Variant)
            .FirstOrDefault();
    }
}
