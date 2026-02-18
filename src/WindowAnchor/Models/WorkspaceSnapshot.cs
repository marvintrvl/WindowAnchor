using System;
using System.Collections.Generic;

namespace WindowAnchor.Models;

public class WorkspaceSnapshot
{
    public string Name { get; set; } = "";
    public string MonitorFingerprint { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public List<WorkspaceEntry> Entries { get; set; } = new();
}
