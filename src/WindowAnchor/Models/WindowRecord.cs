namespace WindowAnchor.Models;

public class WindowRecord
{
    // ── App identity ─────────────────────────────────────────────────────────
    public string ExecutablePath { get; set; } = "";
    public string ProcessName    { get; set; } = "";
    public string ClassName      { get; set; } = "";
    public string TitleSnippet   { get; set; } = "";

    // ── Window state ─────────────────────────────────────────────────────────
    public int  ShowCmd     { get; set; } = 1;
    public int  NormalLeft   { get; set; }
    public int  NormalTop    { get; set; }
    public int  NormalRight  { get; set; }
    public int  NormalBottom { get; set; }
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
