using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace WindowAnchor.Services;

public sealed record VirtualDesktopInfo(Guid Id, string Name, bool IsCurrent);

/// <summary>
/// Best-effort, read-only Explorer metadata, also used by PowerToys' desktop helper.
/// This registry layout is undocumented: unavailable/malformed data must never hide windows.
/// Window membership continues to come from the documented IVirtualDesktopManager API.
/// </summary>
internal static class VirtualDesktopCatalog
{
    private const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    internal static IReadOnlyList<VirtualDesktopInfo> Read()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ExplorerKey + @"\VirtualDesktops");
            if (root is null) return [];
            var current = root.GetValue("CurrentVirtualDesktop") as byte[];
            if (current?.Length != 16)
            {
                using var process = Process.GetCurrentProcess();
                using var session = Registry.CurrentUser.OpenSubKey(
                    ExplorerKey + @"\SessionInfo\" + process.SessionId + @"\VirtualDesktops");
                current = session?.GetValue("CurrentVirtualDesktop") as byte[];
            }
            return Parse(root.GetValue("VirtualDesktopIDs") as byte[], current, id =>
            {
                using var desktop = root.OpenSubKey(@"Desktops\" + id.ToString("B"));
                return desktop?.GetValue("Name") as string;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<VirtualDesktopInfo> Parse(
        byte[]? ids, byte[]? current, Func<Guid, string?> readName)
    {
        if (ids is null || ids.Length == 0 || ids.Length % 16 != 0 || ids.Length > 16 * 1024)
            return [];
        Guid currentId = current?.Length == 16 ? new Guid(current) : Guid.Empty;
        var seen = new HashSet<Guid>();
        var result = new List<VirtualDesktopInfo>();
        for (int offset = 0; offset < ids.Length; offset += 16)
        {
            Guid id = new(ids.AsSpan(offset, 16));
            if (id == Guid.Empty || !seen.Add(id)) return [];
            string? name = readName(id);
            result.Add(new(id, string.IsNullOrWhiteSpace(name) ? $"Desktop {result.Count + 1}" : name,
                id == currentId));
        }
        return result;
    }
}
