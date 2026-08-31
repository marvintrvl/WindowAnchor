using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WindowAnchor.Services;

/// <summary>Detects supported Chromium browsers and manages their native-host registration.</summary>
public static class BrowserIntegrationService
{
    private const string HostName = "com.windowanchor.browser";

    private static readonly (string Name, string[] Paths, string RegistryRoot, string ManagementUrl)[] Browsers =
    {
        ("Google Chrome", new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
        }, @"Software\Google\Chrome\NativeMessagingHosts\", "chrome://extensions/"),
        ("Microsoft Edge", new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
        }, @"Software\Microsoft\Edge\NativeMessagingHosts\", "edge://extensions/"),
        ("Brave", new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
        }, @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\", "brave://extensions/"),
        ("Opera", new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Opera", "launcher.exe"),
        }, @"Software\Opera Software\NativeMessagingHosts\", "opera://extensions/"),
    };

    public static IReadOnlyList<string> GetInstalledBrowserNames()
    {
        var result = new List<string>();
        foreach (var browser in Browsers)
            if (Array.Exists(browser.Paths, File.Exists)) result.Add(browser.Name);
        return result;
    }

    public static void OpenManagementPage(string browserName)
    {
        foreach (var browser in Browsers)
        {
            if (!browser.Name.Equals(browserName, StringComparison.OrdinalIgnoreCase)) continue;
            string? executable = Array.Find(browser.Paths, File.Exists);
            if (executable == null) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = $"--new-tab \"{browser.ManagementUrl}\"",
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "browser_integration.management_page_failed",
                    "Could not open a browser extension-management page",
                    ex,
                    LogField.Public("browserName", browser.Name),
                    LogField.Public("errorCategory", "browser_management_page"));
            }
            return;
        }
    }

    public static int RemoveNativeHostRegistrations()
    {
        int removed = 0;
        foreach (var browser in Browsers)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(browser.RegistryRoot + HostName, writable: true);
                if (key == null) continue;
                Registry.CurrentUser.DeleteSubKeyTree(browser.RegistryRoot + HostName, throwOnMissingSubKey: false);
                removed++;
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "browser_integration.registration_remove_failed",
                    "Could not remove a browser native-host registration",
                    ex,
                    LogField.Public("browserName", browser.Name),
                    LogField.Public("errorCategory", "browser_registration_remove"));
            }
        }
        return removed;
    }
}
