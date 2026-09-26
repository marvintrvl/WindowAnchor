using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WindowAnchor.Models;

/// <summary>
/// Represents one saved application window within a <see cref="WorkspaceSnapshot"/>.
/// Combines app identity, optional open-file tracking, DPI-aware position, and
/// the monitor the window was on when the snapshot was taken.
/// </summary>
public class WorkspaceEntry
{
    /// <summary>
    /// Stable identity for this saved entry. It is assigned once when the entry is captured or
    /// migrated and remains unchanged while the workspace is renamed or rewritten.
    /// </summary>
    [JsonInclude]
    public string EntryId { get; internal set; } = Guid.NewGuid().ToString("D");

    // ── App identity ─────────────────────────────────────────────────────────
    public string ExecutablePath  { get; set; } = "";
    /// <summary>Optional portable alias form of <see cref="ExecutablePath"/>; the absolute path remains local fallback data.</summary>
    public string? LogicalExecutablePath { get; set; }
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

    // ── Dedicated browser window (site kept in its own window) ───────────────
    /// <summary>
    /// True when this entry is a browser window that must be reopened as its own window at a
    /// specific URL, instead of relying on the browser's session restore.
    /// </summary>
    public bool    IsDedicatedBrowserWindow { get; set; }

    /// <summary>URL to reopen for a dedicated browser window, e.g. <c>https://vari.love/</c>.</summary>
    public string  BrowserUrl               { get; set; } = "";

    // ── File tracking (null when SavedWithFiles = false) ─────────────────────
    public string? FilePath       { get; set; }
    /// <summary>Optional portable alias form of <see cref="FilePath"/>.</summary>
    public string? LogicalFilePath { get; set; }
    public int     FileConfidence { get; set; }
    public string  FileSource     { get; set; } = "NONE";
    public string? LaunchArg      { get; set; }
    /// <summary>Optional portable alias form of <see cref="LaunchArg"/>.</summary>
    public string? LogicalLaunchArg { get; set; }

    /// <summary>
    /// Identifies whether <see cref="LaunchArg"/> is a VS Code folder workspace or a
    /// <c>.code-workspace</c> file. The path itself remains in LaunchArg so existing resource,
    /// alias, and privacy handling continues to apply.
    /// </summary>
    public EditorWorkspaceKind EditorWorkspaceKind { get; set; } = EditorWorkspaceKind.None;

    /// <summary>
    /// Ordered File Explorer folder tabs captured for this top-level Explorer window. Empty for
    /// non-Explorer entries and when file/folder capture was disabled.
    /// </summary>
    public List<string> ExplorerTabPaths { get; set; } = new();

    /// <summary>Index of the tab that was active when the Explorer window was captured.</summary>
    public int ExplorerActiveTabIndex { get; set; }

    /// <summary>Ordered Windows Terminal tabs with explicitly confirmed profiles and directories.</summary>
    public List<TerminalTab> TerminalTabs { get; set; } = new();

    /// <summary>Index of the focused Terminal tab at capture time.</summary>
    public int TerminalActiveTabIndex { get; set; }

    // ── Window position ──────────────────────────────────────────────────────
    public WindowRecord Position  { get; set; } = new();

    // ── Monitor assignment ───────────────────────────────────────────────────
    /// <summary>Stable EDID-based monitor ID (matches <see cref="MonitorInfo.MonitorId"/>).</summary>
    public string MonitorId       { get; set; } = "";

    /// <summary>0-based monitor index (matches <see cref="MonitorInfo.Index"/>).</summary>
    public int    MonitorIndex    { get; set; }

    /// <summary>Friendly name of the monitor, e.g. "DELL U2723QE". For UI display only.</summary>
    public string MonitorName     { get; set; } = "";

    /// <summary>Optional behavior override applied when this entry is planned.</summary>
    public EntryRestorePolicy RestorePolicy { get; set; } = EntryRestorePolicy.WorkspaceDefault;

    // ── Runtime-only ─────────────────────────────────────────────────────────
    [JsonIgnore]
    public bool WasRestored { get; set; } = false;
}

/// <summary>Persisted shape of a VS Code workspace target when one was captured safely.</summary>
public enum EditorWorkspaceKind
{
    None,
    Folder,
    WorkspaceFile
}
