using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowAnchor.Models;

/// <summary>
/// The top-level save artifact produced by <see cref="WindowAnchor.Services.WorkspaceService.TakeSnapshot"/>.
/// Contains the monitor configuration at save time, the set of captured windows grouped
/// by monitor, and metadata controlling restore behaviour.
/// </summary>
public class WorkspaceSnapshot
{
    /// <summary>Current persisted workspace schema version.</summary>
    public const int CurrentSchemaVersion = 3;

    /// <summary>Schema version used to serialize this workspace document.</summary>
    [System.Text.Json.Serialization.JsonInclude]
    public int SchemaVersion { get; internal set; } = CurrentSchemaVersion;

    /// <summary>
    /// Stable workspace identity. It is assigned once when a workspace is created or migrated
    /// and is unaffected by display-name changes.
    /// </summary>
    [System.Text.Json.Serialization.JsonInclude]
    public string WorkspaceId { get; internal set; } = Guid.NewGuid().ToString("D");

    /// <summary>User-visible name of this workspace.</summary>
    public string   Name               { get; set; } = "";
    /// <summary>8-character hex fingerprint of the monitor set, used for auto-restore matching.</summary>
    public string   MonitorFingerprint { get; set; } = "";
    /// <summary>UTC timestamp of when the snapshot was taken.</summary>
    public DateTime SavedAt            { get; set; }

    /// <summary>
    /// Details of each physical monitor present when the snapshot was taken.
    /// Used for per-monitor display in the UI and selective restore.
    /// </summary>
    public List<MonitorInfo>    Monitors       { get; set; } = new();

    /// <summary>
    /// True when the user asked to include open-file detection (Tier 1 + Tier 2).
    /// False means FilePath/LaunchArg on all entries will be null.
    /// </summary>
    public bool                 SavedWithFiles { get; set; } = true;

    public List<WorkspaceEntry> Entries        { get; set; } = new();

    /// <summary>Browser tab sessions captured by the optional WindowAnchor extension.</summary>
    public List<BrowserSession> BrowserSessions { get; set; } = new();

    // ── Convenience helpers ──────────────────────────────────────────────────

    /// <summary>Returns entries belonging to a specific monitor.</summary>
    public IEnumerable<WorkspaceEntry> EntriesForMonitor(string monitorId)
        => Entries.Where(e => e.MonitorId == monitorId);

    /// <summary>
    /// Returns entries grouped by monitor in the order monitors were saved
    /// (left-to-right, primary first).
    /// </summary>
    public IEnumerable<(MonitorInfo Monitor, IEnumerable<WorkspaceEntry> Entries)> EntriesByMonitor()
        => Monitors.Select(m => (m, EntriesForMonitor(m.MonitorId)));
}
