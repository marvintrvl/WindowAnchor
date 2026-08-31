using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WindowAnchor.Models;

/// <summary>
/// Defines how the app behaves on startup regarding workspace restoration.
/// </summary>
public enum StartupBehavior
{
    /// <summary>Don't restore any workspace on startup.</summary>
    None = 0,

    /// <summary>Restore a specific default workspace.</summary>
    RestoreDefault = 1,

    /// <summary>Restore the most recently saved workspace.</summary>
    RestoreLastUsed = 2,

    /// <summary>Show a dialog asking the user which workspace to restore.</summary>
    AskUser = 3,
}

/// <summary>Minimum severity written to the local diagnostic log.</summary>
public enum DiagnosticLogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    Off = 4
}

/// <summary>
/// Persisted binding for a single keyboard shortcut.
/// Stored as human-readable strings for clean JSON output.
/// </summary>
public class HotkeyBinding
{
    /// <summary>Matches <see cref="Services.HotkeyService.HotkeyInfo.ActionId"/>.</summary>
    public string ActionId  { get; set; } = "";

    /// <summary>Modifier keys, e.g. "Ctrl+Alt" or "Ctrl+Shift".</summary>
    public string Modifiers { get; set; } = "";

    /// <summary>Key name matching <see cref="System.Windows.Input.Key"/> enum, e.g. "S", "D1".</summary>
    public string KeyName   { get; set; } = "";
}

/// <summary>
/// Persisted application settings stored in %AppData%\WindowAnchor\settings.json.
/// </summary>
public class AppSettings
{
    /// <summary>Current persisted settings schema version.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Schema version used to serialize this settings document.</summary>
    [JsonInclude]
    public int SchemaVersion { get; internal set; } = CurrentSchemaVersion;

    // ── Startup ───────────────────────────────────────────────────────────
    public StartupBehavior StartupBehavior { get; set; } = StartupBehavior.None;

    /// <summary>Stable ID of the workspace restored by <see cref="StartupBehavior.RestoreDefault"/>.</summary>
    public string? DefaultWorkspaceId { get; set; }

    /// <summary>Legacy name-based default reference, accepted only during v1 migration.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultWorkspaceName { get; set; }

    // ── Notifications ────────────────────────────────────────────────────
    /// <summary>Whether WindowAnchor may show system-tray balloon notifications.</summary>
    public bool NotificationsEnabled { get; set; } = true;

    // ── Diagnostics ──────────────────────────────────────────────────────
    /// <summary>
    /// Minimum local log severity. Exported diagnostics are independently redacted by default.
    /// </summary>
    public DiagnosticLogLevel DiagnosticLogLevel { get; set; } = DiagnosticLogLevel.Debug;

    // ── Keyboard shortcuts ────────────────────────────────────────────────
    public bool HotkeysEnabled { get; set; } = true;

    /// <summary>
    /// Custom hotkey overrides.  When <c>null</c> or empty the built-in defaults apply.
    /// Only entries that differ from the defaults need to be stored.
    /// </summary>
    public List<HotkeyBinding>? CustomHotkeys { get; set; }

    // ── Workspace display order ───────────────────────────────────────────
    /// <summary>
    /// Stable workspace IDs in the user's preferred display order.
    /// The first three map to Ctrl+Alt+1/2/3 hotkeys.
    /// Workspaces not in this list are appended at the end (sorted by save date).
    /// </summary>
    public List<string>? WorkspaceOrderIds { get; set; }

    /// <summary>Legacy name-based display order, accepted only during v1 migration.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? WorkspaceOrder { get; set; }
    // ── Monitor aliases ───────────────────────────────────────────────────
    /// <summary>
    /// User-defined friendly names for monitors, keyed by stable EDID-based MonitorId.
    /// When set, the alias replaces the hardware FriendlyName throughout the UI.
    /// </summary>
    public Dictionary<string, string>? MonitorAliases { get; set; }

    // ── Dedicated browser windows ─────────────────────────────────────────
    /// <summary>
    /// URL fragments identifying browser windows that should be restored as their own window
    /// rather than through the browser's session restore.
    /// <para>
    /// Some sites cannot be installed as a web app (no PWA manifest) but are still kept in a
    /// dedicated window next to a normal multi-tab window. Such a window is indistinguishable
    /// from any other browser window, so <c>--restore-last-session</c> cannot bring it back
    /// reliably. When a window's address bar matches one of these fragments, WindowAnchor stores
    /// the URL and reopens the window with <c>--new-window &lt;url&gt;</c>.
    /// </para>
    /// <para>
    /// A bare domain such as <c>vari.love</c> matches every page on that site. When the list is
    /// empty no URLs are read at all, so snapshots are unaffected for users not using this.
    /// </para>
    /// </summary>
    public List<string>? DedicatedBrowserUrlPatterns { get; set; }
}
