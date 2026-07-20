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

    // ── Browser web app (PWA) identity ───────────────────────────────────────
    /// <summary>
    /// Per-window <c>AppUserModelID</c> captured at snapshot time. For Chromium browsers this
    /// distinguishes an installed web app window from an ordinary browser window.
    /// Empty for non-browser apps and for snapshots taken before web-app support was added.
    /// </summary>
    public string  AppUserModelId        { get; set; } = "";

    /// <summary>True when this entry is an installed browser web app (PWA), not a plain browser window.</summary>
    public bool    IsWebApp              { get; set; }

    /// <summary>Display name of the web app, taken from its shortcut (e.g. "Insilico Terminal").</summary>
    public string  WebAppName            { get; set; } = "";

    /// <summary>Path of the <c>.lnk</c> that launches the web app. Preferred launch method on restore.</summary>
    public string? WebAppShortcutPath    { get; set; }

    /// <summary>Shortcut target used when the <c>.lnk</c> no longer exists (usually <c>chrome_proxy.exe</c>).</summary>
    public string? WebAppLaunchTarget    { get; set; }

    /// <summary>Command line for <see cref="WebAppLaunchTarget"/>, e.g. <c>--profile-directory=Default --app-id=…</c>.</summary>
    public string? WebAppLaunchArguments { get; set; }

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
