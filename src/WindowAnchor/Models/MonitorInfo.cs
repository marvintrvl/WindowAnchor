namespace WindowAnchor.Models;

public class MonitorInfo
{
    public ushort EdidManufactureId { get; set; }
    public ushort EdidProductCodeId { get; set; }
    public uint ConnectorInstance { get; set; }
    public string MonitorFriendlyDeviceName { get; set; } = "";
    public string MonitorDevicePath { get; set; } = "";
}
