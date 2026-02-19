namespace WindowAnchor.Models;

/// <summary>
/// Immutable snapshot of a single top-level window captured by <see cref="WindowAnchor.Services.WindowService"/>.
/// Stored as the <see cref="WorkspaceEntry.Position"/> field inside a <see cref="WorkspaceSnapshot"/>.
/// </summary>
public class WindowRecord
{
    // ── App identity ──────────────────────────────────────────────────────────────
    /// <summary>Full path to the process executable, e.g. <c>C:\Program Files\...\app.exe</c>.</summary>
    public string ExecutablePath { get; set; } = "";
    /// <summary>Process name without extension, e.g. <c>"notepad"</c>.</summary>
    public string ProcessName    { get; set; } = "";
    /// <summary>Win32 window class name returned by <c>GetClassName</c>.</summary>
    public string ClassName      { get; set; } = "";
    /// <summary>First 120 characters of the window title, used for matching and display.</summary>
    public string TitleSnippet   { get; set; } = "";

    // ── Window state ──────────────────────────────────────────────────────────────
    /// <summary>
    /// <c>WINDOWPLACEMENT.showCmd</c> value (e.g. <c>SW_NORMAL = 1</c>, <c>SW_MAXIMIZE = 3</c>,
    /// <c>SW_MINIMIZE = 6</c>). Used to restore maximised/minimised state after repositioning.
    /// </summary>
    public int  ShowCmd     { get; set; } = 1;
    /// <summary>Left edge of the window's restored (non-maximised) rectangle, in virtual-screen coordinates.</summary>
    public int  NormalLeft   { get; set; }
    /// <summary>Top edge of the window's restored rectangle, in virtual-screen coordinates.</summary>
    public int  NormalTop    { get; set; }
    /// <summary>Right edge of the window's restored rectangle, in virtual-screen coordinates.</summary>
    public int  NormalRight  { get; set; }
    /// <summary>Bottom edge of the window's restored rectangle, in virtual-screen coordinates.</summary>
    public int  NormalBottom { get; set; }
    /// <summary>DPI of the monitor the window was on when captured. Used for DPI-aware coordinate scaling on restore.</summary>
    public uint SavedDpi     { get; set; } = 96;

    /// <summary>
    /// For File Explorer windows: the folder open at snapshot time.
    /// Populated via Shell.Application COM. Empty for non-Explorer windows.
    /// </summary>
    public string FolderPath { get; set; } = "";

    // ── Monitor assignment (populated during snapshot, empty in old saves) ───
    /// <summary>Stable EDID-based monitor ID, e.g. "A1B2:C3D4:0".</summary>
    public string MonitorId   { get; set; } = "";

    /// <summary>0-based monitor index matching <see cref="MonitorInfo.Index"/>.</summary>
    public int    MonitorIndex { get; set; }

    /// <summary>Human-readable monitor name for display purposes.</summary>
    public string MonitorName  { get; set; } = "";
}
