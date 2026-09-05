namespace WindowAnchor.Models;

/// <summary>Semantic layout detected from a window's restored rectangle.</summary>
public enum WindowLayoutKind
{
    Custom,
    Full,
    LeftHalf,
    RightHalf,
    TopHalf,
    BottomHalf,
    LeftThird,
    CenterThird,
    RightThird,
    Centered
}

/// <summary>Horizontal relationship between a window and its source monitor work area.</summary>
public enum HorizontalWindowAnchor
{
    Custom,
    Left,
    Center,
    Right,
    Stretch
}

/// <summary>Vertical relationship between a window and its source monitor work area.</summary>
public enum VerticalWindowAnchor
{
    Custom,
    Top,
    Center,
    Bottom,
    Stretch
}

/// <summary>
/// Monitor-relative restored geometry stored alongside the legacy absolute pixel rectangle.
/// Values are ratios of the source monitor work area and may fall just outside 0..1 when the
/// captured window intentionally overlaps an edge.
/// </summary>
public sealed class NormalizedWindowLayout
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public HorizontalWindowAnchor HorizontalAnchor { get; set; } = HorizontalWindowAnchor.Custom;
    public VerticalWindowAnchor VerticalAnchor { get; set; } = VerticalWindowAnchor.Custom;
    public WindowLayoutKind Kind { get; set; } = WindowLayoutKind.Custom;
}
