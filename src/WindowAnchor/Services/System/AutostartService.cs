using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WindowAnchor.Services;

/// <summary>
/// Manages the HKCU Run key to launch WindowAnchor silently on Windows login.
/// </summary>
public static class AutostartService
{
    private const string RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName       = "WindowAnchor";

    /// <summary>Returns true if the Run key entry exists and points to this executable.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex)
        {
            AppLogger.Debug(
                "autostart.status_query_failed",
                "Could not query the autostart registration",
                ex,
                LogField.Public("errorCategory", "autostart_query"));
            return false;
        }
    }

    /// <summary>Writes the Run key entry: "{exePath}" --minimized</summary>
    public static void Enable()
    {
        try
        {
            string exePath = GetExePath();
            using var key  = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
            key.SetValue(ValueName, $"\"{exePath}\" --minimized");
            AppLogger.Info(
                "autostart.enabled",
                "Enabled autostart",
                LogField.CommandLine("commandLine", $"\"{exePath}\" --minimized"));
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "autostart.enable_failed",
                "Could not enable autostart",
                ex,
                LogField.Public("errorCategory", "autostart_enable"));
        }
    }

    /// <summary>Removes the Run key entry.</summary>
    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName);
                AppLogger.Info(
                    "autostart.disabled",
                    "Disabled autostart");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "autostart.disable_failed",
                "Could not disable autostart",
                ex,
                LogField.Public("errorCategory", "autostart_disable"));
        }
    }

    private static string GetExePath()
    {
        // Process.MainModule.FileName is the correct approach and works for single-file publish.
        string? path = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(path)) return path;

        // Fallback: derive from AppContext.BaseDirectory (safe for single-file apps)
        return Path.Combine(AppContext.BaseDirectory, AppDomain.CurrentDomain.FriendlyName + ".exe");
    }
}
