using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WindowAnchor.Native;

namespace WindowAnchor.Services;

public class MonitorService
{
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
                // Check if EDID is valid (bit 0 of flags)
                bool edidValid = (targetName.Flags & 0x1) != 0;
                if (edidValid)
                {
                    monitorIds.Add($"{targetName.EdidManufactureId:X4}:{targetName.EdidProductCodeId:X4}:{targetName.ConnectorInstance}");
                }
                else
                {
                    monitorIds.Add($"noedid:{targetName.MonitorDevicePath}");
                }
            }
        }

        if (monitorIds.Count == 0) return "no_monitors";

        // Sort alphabetically to be order-independent
        monitorIds.Sort();

        string joined = string.Join("|", monitorIds);
        using (var sha256 = SHA256.Create())
        {
            byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(joined));
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLower().Substring(0, 8);
        }
    }
}
