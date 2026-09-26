using System.Text.Json.Serialization;

namespace WindowAnchor.Models;

/// <summary>A Windows Terminal tab that can be recreated with the wt command line.</summary>
public sealed class TerminalTab
{
    /// <summary>Installed Terminal profile name. Empty selects the current default profile.</summary>
    public string Profile { get; set; } = "";

    /// <summary>Directory to open in this tab, supplied by the user at capture time.</summary>
    public string StartingDirectory { get; set; } = "";

    /// <summary>Accessibility label shown only while configuring a new snapshot.</summary>
    [JsonIgnore]
    public string TitleHint { get; set; } = "";
}
