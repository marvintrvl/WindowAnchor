using System;
using System.Collections.Generic;

namespace WindowAnchor.Models;

public class MonitorProfile
{
    public string Id          { get; set; } = Guid.NewGuid().ToString("N");
    public string Fingerprint { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime LastSaved { get; set; }
    public List<WindowRecord> Windows { get; set; } = new();
}
