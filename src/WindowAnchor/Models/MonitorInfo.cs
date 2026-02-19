namespace WindowAnchor.Models;

/// <summary>
/// Describes a single physical monitor as seen at workspace-save time.
/// Used to tag <see cref="WorkspaceEntry"/> records by monitor and to
/// display per-monitor breakdowns in the UI.
/// </summary>
public class MonitorInfo
{
    /// <summary>Stable EDID-based ID, e.g. "A1B2:C3D4:0". Empty for monitors without EDID.</summary>
    public string MonitorId      { get; set; } = "";

    /// <summary>Human-readable name, e.g. "DELL U2723QE" or "Generic PnP Monitor".</summary>
    public string FriendlyName   { get; set; } = "";

    /// <summary>GDI device name used to correlate with MonitorFromWindow, e.g. "\\\\.\\DISPLAY1".</summary>
    public string DeviceName     { get; set; } = "";

    /// <summary>0-based index. Displayed as "Monitor (Index+1)" in the UI.</summary>
    public int    Index          { get; set; }

    public int    WidthPixels    { get; set; }
    public int    HeightPixels   { get; set; }
    public bool   IsPrimary      { get; set; }
}
