using System;
using System.IO;

namespace WindowAnchor.Services;

/// <summary>Locates the per-user wt.exe app execution alias without guessing a package version.</summary>
internal static class WindowsTerminalLauncherResolver
{
    internal static string Find()
    {
        string localAlias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(localAlias)) return localAlias;

        foreach (string part in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                string candidate = Path.Combine(part.Trim().Trim('"'), "wt.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ignore one malformed PATH component.
            }
        }
        return "";
    }
}
