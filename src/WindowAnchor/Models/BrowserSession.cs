using System.Collections.Generic;

namespace WindowAnchor.Models;

/// <summary>Browser tab state captured by the WindowAnchor browser extension.</summary>
public class BrowserTab
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int Index { get; set; }
    public bool Active { get; set; }
    public bool Pinned { get; set; }
    public int GroupIndex { get; set; } = -1;
}

/// <summary>Browser tab-group metadata captured by the WindowAnchor browser extension.</summary>
public class BrowserTabGroup
{
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public string Color { get; set; } = "grey";
    public bool Collapsed { get; set; }
}

/// <summary>One browser window and its restorable tab state.</summary>
public class BrowserSession
{
    public string Browser { get; set; } = "";
    public string ActiveTitle { get; set; } = "";
    public int WindowIndex { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string State { get; set; } = "normal";
    public List<BrowserTab> Tabs { get; set; } = new();
    public List<BrowserTabGroup> Groups { get; set; } = new();
}
