using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace WindowAnchor.Services;

/// <summary>Detects supported Chromium browsers and manages their native-host registration.</summary>
public static class BrowserIntegrationService
{
    private const string HostName = "com.windowanchor.browser";
    public const string ChromeExtensionId = "liiklnjpifhhmjncifbjjfgplonkkinh";
    public const string ChromeWebStoreUrl =
        "https://chromewebstore.google.com/detail/windowanchor-browser-conn/liiklnjpifhhmjncifbjjfgplonkkinh";

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
                string destination = browser.Name.Equals("Google Chrome", StringComparison.OrdinalIgnoreCase)
                    ? ChromeWebStoreUrl
                    : browser.ManagementUrl;
                if (browser.Name.Equals("Google Chrome", StringComparison.OrdinalIgnoreCase))
                    RegisterChromeNativeHost();
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = $"--new-tab \"{destination}\"",
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "browser_integration.management_page_failed",
                    "Could not open the browser connector setup page",
                    ex,
                    LogField.Public("browserName", browser.Name),
                    LogField.Public("errorCategory", "browser_management_page"));
            }
            return;
        }
    }

    private static void RegisterChromeNativeHost()
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new InvalidOperationException("WindowAnchor's executable path is unavailable for browser setup.");

        string hostDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowAnchor");
        Directory.CreateDirectory(hostDirectory);
        string manifestPath = Path.Combine(hostDirectory, "native-host-manifest.json");
        string manifest = JsonSerializer.Serialize(new
        {
            name = HostName,
            description = "WindowAnchor browser-session native messaging host",
            path = executablePath,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ChromeExtensionId}/" }
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(manifestPath, manifest);

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            @"Software\Google\Chrome\NativeMessagingHosts\" + HostName,
            writable: true);
        key.SetValue("", manifestPath);
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
