namespace WindowAnchor.Models;

public class WindowRecord
{
    public string ExecutablePath { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string TitleSnippet { get; set; } = "";
    public int ShowCmd { get; set; } = 1;
    public int NormalLeft { get; set; }
    public int NormalTop { get; set; }
    public int NormalRight { get; set; }
    public int NormalBottom { get; set; }
    public uint SavedDpi { get; set; } = 96;
}
