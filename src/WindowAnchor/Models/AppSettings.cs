using System.Collections.Generic;

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
    // ── Startup ───────────────────────────────────────────────────────────
    public StartupBehavior StartupBehavior   { get; set; } = StartupBehavior.None;
    public string?         DefaultWorkspaceName { get; set; }

    // ── Keyboard shortcuts ────────────────────────────────────────────────
    public bool HotkeysEnabled { get; set; } = true;

    /// <summary>
    /// Custom hotkey overrides.  When <c>null</c> or empty the built-in defaults apply.
    /// Only entries that differ from the defaults need to be stored.
    /// </summary>
    public List<HotkeyBinding>? CustomHotkeys { get; set; }

    // ── Workspace display order ───────────────────────────────────────────
    /// <summary>
    /// Workspace names in the user's preferred display order.
    /// The first three map to Ctrl+Alt+1/2/3 hotkeys.
    /// Workspaces not in this list are appended at the end (sorted by save date).
    /// </summary>
    public List<string>? WorkspaceOrder { get; set; }
}
