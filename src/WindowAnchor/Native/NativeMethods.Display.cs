using System.Runtime.InteropServices;

namespace WindowAnchor.Native;

/// <summary>
/// P/Invoke declarations for QueryDisplayConfig-based monitor fingerprinting.
/// NEVER use EnumDisplayMonitors / Screen.DeviceName — those change on reconnect.
/// </summary>
public static class NativeMethodsDisplay
{
    // ── Enums ────────────────────────────────────────────────────────────────

    public enum QueryDeviceConfigFlags : uint
    {
        QdcOnlyActivePaths = 0x00000002
    }

    public enum DisplayConfigDeviceInfoType : int
    {
        DisplayConfigDeviceInfoGetSourceName         = 1,
        DisplayConfigDeviceInfoGetTargetName         = 2,
        DisplayConfigDeviceInfoGetTargetPreferredMode = 3,
    }

    // ── Core structs ─────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int  HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigPathTargetInfo
    {
        public Luid                 AdapterId;
        public uint                 Id;
        public uint                 ModeInfoIdx;
        public int                  OutputTechnology;
        public int                  Rotation;
        public int                  Scaling;
        public DisplayConfigRational RefreshRate;
        public int                  ScanLineOrdering;
        public bool                 TargetAvailable;
        public uint                 StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    /// <summary>
    /// Opaque 64-byte buffer — the spec mandates this exact approach to
    /// guarantee sizeof == 64 and avoid union alignment surprises.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigModeInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigDeviceInfoHeader
    {
        public DisplayConfigDeviceInfoType Type;
        public uint                        Size;
        public Luid                        AdapterId;
        public uint                        Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint                          Flags;
        public int                           OutputTechnology;
        public ushort                        EdidManufactureId;
        public ushort                        EdidProductCodeId;
        public uint                          ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string                        MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string                        MonitorDevicePath;
    }

    // ── Source device name (GDI device name per adapter/source) ─────────────────

    /// <summary>
    /// Used with DisplayConfigDeviceInfoGetSourceName (type = 1) to retrieve the
    /// GDI device name (e.g. "\\.\DISPLAY1") for a display source. This is the
    /// same string that <c>GetMonitorInfo.szDevice</c> returns, allowing us to
    /// cross-reference QueryDisplayConfig paths with EnumDisplayMonitors handles.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;   // e.g. "\\.\DISPLAY1"
    }

    // ── Functions ─────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(
        QueryDeviceConfigFlags flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(
        QueryDeviceConfigFlags flags,
        ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(
        ref DisplayConfigTargetDeviceName deviceName);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(
        ref DisplayConfigSourceDeviceName deviceName);
}
