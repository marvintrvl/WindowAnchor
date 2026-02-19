using System.Text.Json.Serialization;

namespace WindowAnchor.Models;

/// <summary>
/// Represents one saved application window within a <see cref="WorkspaceSnapshot"/>.
/// Combines app identity, optional open-file tracking, DPI-aware position, and
/// the monitor the window was on when the snapshot was taken.
/// </summary>
public class WorkspaceEntry
{
    // ── App identity ─────────────────────────────────────────────────────────
    public string ExecutablePath  { get; set; } = "";
    public string ProcessName     { get; set; } = "";
    public string WindowClassName { get; set; } = "";

    // ── File tracking (null when SavedWithFiles = false) ─────────────────────
    public string? FilePath       { get; set; }
    public int     FileConfidence { get; set; }
    public string  FileSource     { get; set; } = "NONE";
    public string? LaunchArg      { get; set; }

    // ── Window position ──────────────────────────────────────────────────────
    public WindowRecord Position  { get; set; } = new();

    // ── Monitor assignment ───────────────────────────────────────────────────
    /// <summary>Stable EDID-based monitor ID (matches <see cref="MonitorInfo.MonitorId"/>).</summary>
    public string MonitorId       { get; set; } = "";

    /// <summary>0-based monitor index (matches <see cref="MonitorInfo.Index"/>).</summary>
    public int    MonitorIndex    { get; set; }

    /// <summary>Friendly name of the monitor, e.g. "DELL U2723QE". For UI display only.</summary>
    public string MonitorName     { get; set; } = "";

    // ── Runtime-only ─────────────────────────────────────────────────────────
    [JsonIgnore]
    public bool WasRestored { get; set; } = false;
}
