using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowAnchor.Models;

public class WorkspaceSnapshot
{
    public string   Name               { get; set; } = "";
    public string   MonitorFingerprint { get; set; } = "";
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
