using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WindowAnchor.Models;
using WindowAnchor.Native;

namespace WindowAnchor.Services;

public class MonitorService
{
    // ── Monitor fingerprint ──────────────────────────────────────────────────

    public string GetCurrentMonitorFingerprint()
    {
        var monitorIds = new List<string>();

        uint pathCount, modeCount;
        int error = NativeMethodsDisplay.GetDisplayConfigBufferSizes(
            NativeMethodsDisplay.QueryDeviceConfigFlags.QdcOnlyActivePaths,
            out pathCount, out modeCount);

        if (error != 0) return "error_buffer_size";

        var paths = new NativeMethodsDisplay.DisplayConfigPathInfo[pathCount];
        var modes = new NativeMethodsDisplay.DisplayConfigModeInfo[modeCount];

        error = NativeMethodsDisplay.QueryDisplayConfig(
            NativeMethodsDisplay.QueryDeviceConfigFlags.QdcOnlyActivePaths,
            ref pathCount, paths,
            ref modeCount, modes,
            IntPtr.Zero);

        if (error != 0) return "error_query_config";

        for (int i = 0; i < pathCount; i++)
        {
            var targetName = new NativeMethodsDisplay.DisplayConfigTargetDeviceName();
            targetName.Header.Type = NativeMethodsDisplay.DisplayConfigDeviceInfoType.DisplayConfigDeviceInfoGetTargetName;
            targetName.Header.Size = (uint)Marshal.SizeOf(typeof(NativeMethodsDisplay.DisplayConfigTargetDeviceName));
            targetName.Header.AdapterId = paths[i].TargetInfo.AdapterId;
            targetName.Header.Id = paths[i].TargetInfo.Id;

            error = NativeMethodsDisplay.DisplayConfigGetDeviceInfo(ref targetName);

            if (error == 0)
            {
                bool edidValid = (targetName.Flags & 0x1) != 0;
                if (edidValid)
                    monitorIds.Add($"{targetName.EdidManufactureId:X4}:{targetName.EdidProductCodeId:X4}:{targetName.ConnectorInstance}");
                else
                    monitorIds.Add($"noedid:{targetName.MonitorDevicePath}");
            }
        }

        if (monitorIds.Count == 0) return "no_monitors";

        monitorIds.Sort();
        string joined = string.Join("|", monitorIds);
        using var sha256 = SHA256.Create();
        byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(joined));
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLower().Substring(0, 8);
    }

    // ── Enumerate monitors with stable IDs ───────────────────────────────────

    /// <summary>
    /// Returns all currently active monitors with stable EDID-based IDs, friendly names,
    /// geometry, and primary flag. Sorted: primary first, then left-to-right by position.
    /// </summary>
    public List<MonitorInfo> GetCurrentMonitors()
    {
        // Step 1: enumerate GDI monitors → geometry + device name ("\\\\.\\DISPLAY1")
        var gdiMap   = new Dictionary<string, NativeMethodsWindow.MonitorInfoEx>(StringComparer.OrdinalIgnoreCase);
        var gdiOrder = new List<string>();

        NativeMethodsWindow.MonitorEnumProc monitorCallback =
            (IntPtr hMon, IntPtr hdcMon, ref NativeMethodsWindow.Rect lprc, IntPtr dwData) =>
            {
                var mi = new NativeMethodsWindow.MonitorInfoEx
                {
                    cbSize = Marshal.SizeOf(typeof(NativeMethodsWindow.MonitorInfoEx))
                };
                if (NativeMethodsWindow.GetMonitorInfo(hMon, ref mi))
                {
                    gdiMap[mi.szDevice] = mi;
                    gdiOrder.Add(mi.szDevice);
                }
                return true;
            };
        NativeMethodsWindow.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, monitorCallback, IntPtr.Zero);

        // Step 2: QueryDisplayConfig → EDID stable IDs + friendly names + source GDI device
        uint pathCount, modeCount;
        int err = NativeMethodsDisplay.GetDisplayConfigBufferSizes(
            NativeMethodsDisplay.QueryDeviceConfigFlags.QdcOnlyActivePaths,
            out pathCount, out modeCount);
        if (err != 0) return BuildFallbackMonitors(gdiMap, gdiOrder);

        var paths = new NativeMethodsDisplay.DisplayConfigPathInfo[pathCount];
        var modes = new NativeMethodsDisplay.DisplayConfigModeInfo[modeCount];
        err = NativeMethodsDisplay.QueryDisplayConfig(
            NativeMethodsDisplay.QueryDeviceConfigFlags.QdcOnlyActivePaths,
            ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (err != 0) return BuildFallbackMonitors(gdiMap, gdiOrder);

        var result = new List<MonitorInfo>();

        for (int i = 0; i < pathCount; i++)
        {
            // Target info: EDID + friendly name
            var tgt = new NativeMethodsDisplay.DisplayConfigTargetDeviceName();
            tgt.Header.Type        = NativeMethodsDisplay.DisplayConfigDeviceInfoType.DisplayConfigDeviceInfoGetTargetName;
            tgt.Header.Size        = (uint)Marshal.SizeOf(typeof(NativeMethodsDisplay.DisplayConfigTargetDeviceName));
            tgt.Header.AdapterId   = paths[i].TargetInfo.AdapterId;
            tgt.Header.Id          = paths[i].TargetInfo.Id;
            if (NativeMethodsDisplay.DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;

            // Source info: GDI device name (cross-reference with EnumDisplayMonitors)
            var src = new NativeMethodsDisplay.DisplayConfigSourceDeviceName();
            src.Header.Type        = NativeMethodsDisplay.DisplayConfigDeviceInfoType.DisplayConfigDeviceInfoGetSourceName;
            src.Header.Size        = (uint)Marshal.SizeOf(typeof(NativeMethodsDisplay.DisplayConfigSourceDeviceName));
            src.Header.AdapterId   = paths[i].SourceInfo.AdapterId;
            src.Header.Id          = paths[i].SourceInfo.Id;
            if (NativeMethodsDisplay.DisplayConfigGetDeviceInfo(ref src) != 0) continue;

            string gdiDevice = src.ViewGdiDeviceName;   // e.g. "\\\\.\\DISPLAY1"

            // Build stable monitor ID (same format as fingerprinting)
            bool   edidValid = (tgt.Flags & 0x1) != 0;
            string monitorId = edidValid
                ? $"{tgt.EdidManufactureId:X4}:{tgt.EdidProductCodeId:X4}:{tgt.ConnectorInstance}"
                : $"noedid:{tgt.MonitorDevicePath}";

            // Geometry + primary from GDI
            bool isPrimary   = false;
            int  w = 0, h = 0;
            if (gdiMap.TryGetValue(gdiDevice, out var gdi))
            {
                isPrimary = (gdi.dwFlags & NativeMethodsWindow.MONITORINFOF_PRIMARY) != 0;
                w         = gdi.rcMonitor.Right  - gdi.rcMonitor.Left;
                h         = gdi.rcMonitor.Bottom - gdi.rcMonitor.Top;
            }

            string friendly = !string.IsNullOrWhiteSpace(tgt.MonitorFriendlyDeviceName)
                ? tgt.MonitorFriendlyDeviceName
                : "Generic PnP Monitor";

            result.Add(new MonitorInfo
            {
                MonitorId    = monitorId,
                FriendlyName = friendly,
                DeviceName   = gdiDevice,
                Index        = gdiOrder.IndexOf(gdiDevice),   // temporary; re-assigned after sort
                WidthPixels  = w,
                HeightPixels = h,
                IsPrimary    = isPrimary,
            });
        }

        if (result.Count == 0) return BuildFallbackMonitors(gdiMap, gdiOrder);

        // Sort: primary first, then left edge ascending
        result = result
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => gdiMap.TryGetValue(m.DeviceName, out var g) ? g.rcMonitor.Left : m.Index)
            .ToList();

        // Assign final 0-based indices
        for (int i = 0; i < result.Count; i++)
            result[i].Index = i;

        return result;
    }

    // ── Per-window monitor lookup ─────────────────────────────────────────────

    /// <summary>
    /// Returns the stable <see cref="MonitorInfo.MonitorId"/> for the monitor that contains
    /// the given window. Pass the monitor list from <see cref="GetCurrentMonitors()"/> to
    /// avoid re-enumerating displays on every call.
    /// </summary>
    public static string GetMonitorIdForWindow(IntPtr hwnd, List<MonitorInfo> monitors)
    {
        IntPtr hMon = NativeMethodsWindow.MonitorFromWindow(hwnd, NativeMethodsWindow.MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return "";

        var info = new NativeMethodsWindow.MonitorInfoEx
        {
            cbSize = Marshal.SizeOf(typeof(NativeMethodsWindow.MonitorInfoEx))
        };
        if (!NativeMethodsWindow.GetMonitorInfo(hMon, ref info)) return "";

        var match = monitors.FirstOrDefault(m =>
            string.Equals(m.DeviceName, info.szDevice, StringComparison.OrdinalIgnoreCase));

        return match?.MonitorId ?? $"gdi:{info.szDevice}";
    }

    /// <summary>
    /// Returns the <see cref="MonitorInfo"/> for the monitor that contains the given window,
    /// or <c>null</c> if it cannot be determined.
    /// </summary>
    public static MonitorInfo? GetMonitorForWindow(IntPtr hwnd, List<MonitorInfo> monitors)
    {
        IntPtr hMon = NativeMethodsWindow.MonitorFromWindow(hwnd, NativeMethodsWindow.MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return null;

        var info = new NativeMethodsWindow.MonitorInfoEx
        {
            cbSize = Marshal.SizeOf(typeof(NativeMethodsWindow.MonitorInfoEx))
        };
        if (!NativeMethodsWindow.GetMonitorInfo(hMon, ref info)) return null;

        return monitors.FirstOrDefault(m =>
            string.Equals(m.DeviceName, info.szDevice, StringComparison.OrdinalIgnoreCase));
    }

    // ── Fallback: GDI-only (used when QueryDisplayConfig fails) ─────────────

    private static List<MonitorInfo> BuildFallbackMonitors(
        Dictionary<string, NativeMethodsWindow.MonitorInfoEx> gdiMap,
        List<string> order)
    {
        return order
            .OrderByDescending(d => (gdiMap[d].dwFlags & NativeMethodsWindow.MONITORINFOF_PRIMARY) != 0)
            .ThenBy(d => gdiMap[d].rcMonitor.Left)
            .Select((dev, idx) =>
            {
                var g = gdiMap[dev];
                return new MonitorInfo
                {
                    MonitorId    = $"gdi:{dev}",
                    FriendlyName = $"Monitor {idx + 1}",
                    DeviceName   = dev,
                    Index        = idx,
                    WidthPixels  = g.rcMonitor.Right  - g.rcMonitor.Left,
                    HeightPixels = g.rcMonitor.Bottom - g.rcMonitor.Top,
                    IsPrimary    = (g.dwFlags & NativeMethodsWindow.MONITORINFOF_PRIMARY) != 0,
                };
            })
            .ToList();
    }
}


