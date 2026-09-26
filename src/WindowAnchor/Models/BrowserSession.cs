using System.Collections.Generic;

namespace WindowAnchor.Models;

/// <summary>Controls how a matching tab in the captured browser profile is handled during restore.</summary>
public enum BrowserTabRestorePolicy
{
    ReuseMatchingTab,
    OpenDuplicate,
    Ask
}

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
    /// <summary>Stable ID for this saved browser-window session.</summary>
    public string BrowserSessionId { get; set; } = "";
    /// <summary>Opaque extension-local identifier for the browser profile that captured this session.</summary>
    public string ProfileKey { get; set; } = "";
    /// <summary>Optional non-sensitive profile label. Empty when the browser does not expose one.</summary>
    public string ProfileLabel { get; set; } = "";
    /// <summary>Browser-provided runtime window ID observed during capture.</summary>
    public string BrowserWindowId { get; set; } = "";
    /// <summary>Stable desktop-entry ID when a single captured browser window could be linked safely.</summary>
    public string LinkedEntryId { get; set; } = "";
    /// <summary>Stable monitor ID inherited from a safely linked desktop entry.</summary>
    public string MonitorId { get; set; } = "";
    /// <summary>Restore conflict policy resolved into the approved plan.</summary>
    public BrowserTabRestorePolicy RestorePolicy { get; set; } =
        BrowserTabRestorePolicy.ReuseMatchingTab;
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
