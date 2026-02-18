using System.Text.Json.Serialization;

namespace WindowAnchor.Models;

public class WorkspaceEntry
{
    public string ExecutablePath { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string WindowClassName { get; set; } = "";
    public string? FilePath { get; set; }
    public int FileConfidence { get; set; }
    public string FileSource { get; set; } = "NONE";
    public string? LaunchArg { get; set; }
    public WindowRecord Position { get; set; } = new();

    [JsonIgnore]
    public bool WasRestored { get; set; } = false;
}
