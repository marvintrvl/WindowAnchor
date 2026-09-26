using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace WindowAnchor.Services;

internal enum BrowserConnectorKind
{
    Chromium,
    Firefox,
}

/// <summary>Detects supported browsers and manages their browser-specific native-host registration.</summary>
public static class BrowserIntegrationService
{
    internal const string HostName = "com.windowanchor.browser";
    public const string ChromeExtensionId = "liiklnjpifhhmjncifbjjfgplonkkinh";
    public const string FirefoxExtensionId = "windowanchor-browser-connector@windowanchor.app";
    public const string ChromeWebStoreUrl =
        "https://chromewebstore.google.com/detail/windowanchor-browser-conn/liiklnjpifhhmjncifbjjfgplonkkinh";
    public const string FirefoxAddOnSearchUrl =
        "https://addons.mozilla.org/firefox/search/?q=WindowAnchor%20Browser%20Connector";

    private sealed record BrowserDefinition(
        string Name,
        string[] Paths,
        string RegistryRoot,
        string ManagementUrl,
        BrowserConnectorKind ConnectorKind);

    private static readonly BrowserDefinition[] Browsers =
    [
        new("Google Chrome",
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
        ], @"Software\Google\Chrome\NativeMessagingHosts\", ChromeWebStoreUrl, BrowserConnectorKind.Chromium),
        new("Microsoft Edge",
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
        ], @"Software\Microsoft\Edge\NativeMessagingHosts\", "edge://extensions/", BrowserConnectorKind.Chromium),
        new("Brave",
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
        ], @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\", "brave://extensions/", BrowserConnectorKind.Chromium),
        new("Opera",
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Opera", "launcher.exe"),
        ], @"Software\Opera Software\NativeMessagingHosts\", "opera://extensions/", BrowserConnectorKind.Chromium),
        new("Mozilla Firefox",
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mozilla Firefox", "firefox.exe"),
        ], @"Software\Mozilla\NativeMessagingHosts\", FirefoxAddOnSearchUrl, BrowserConnectorKind.Firefox),
    ];

    public static IReadOnlyList<string> GetInstalledBrowserNames()
    {
        var result = new List<string>();
        foreach (BrowserDefinition browser in Browsers)
            if (Array.Exists(browser.Paths, File.Exists)) result.Add(browser.Name);
        return result;
    }

    public static void OpenManagementPage(string browserName)
    {
        foreach (BrowserDefinition browser in Browsers)
        {
            if (!browser.Name.Equals(browserName, StringComparison.OrdinalIgnoreCase)) continue;
            string? executable = Array.Find(browser.Paths, File.Exists);
            if (executable == null) return;

            try
            {
                RegisterNativeHost(browser);
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = browser.ConnectorKind == BrowserConnectorKind.Firefox
                        ? $"-new-tab \"{browser.ManagementUrl}\""
                        : $"--new-tab \"{browser.ManagementUrl}\"",
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

    internal static string? ResolveNativeMessagingPipe(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0) return null;
        foreach (string argument in arguments)
        {
            if (argument.Equals(FirefoxExtensionId, StringComparison.OrdinalIgnoreCase))
                return BrowserSessionBridge.FirefoxPipeName;
        }
        string origin = arguments[0];
        if (origin.Equals("--native-messaging", StringComparison.OrdinalIgnoreCase) ||
            origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
        {
            return BrowserSessionBridge.ChromiumPipeName;
        }
        return null;
    }

    internal static string CreateNativeHostManifest(string executablePath, BrowserConnectorKind connectorKind)
    {
        object manifest = connectorKind == BrowserConnectorKind.Firefox
            ? new
            {
                name = HostName,
                description = "WindowAnchor Firefox session native messaging host",
                path = executablePath,
                type = "stdio",
                allowed_extensions = new[] { FirefoxExtensionId }
            }
            : new
            {
                name = HostName,
                description = "WindowAnchor Chromium session native messaging host",
                path = executablePath,
                type = "stdio",
                allowed_origins = new[] { $"chrome-extension://{ChromeExtensionId}/" }
            };
        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void RegisterNativeHost(BrowserDefinition browser)
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new InvalidOperationException("WindowAnchor's executable path is unavailable for browser setup.");

        string hostDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowAnchor");
        Directory.CreateDirectory(hostDirectory);
        string family = browser.ConnectorKind == BrowserConnectorKind.Firefox ? "firefox" : "chromium";
        string manifestPath = Path.Combine(hostDirectory, $"native-host-manifest-{family}.json");
        File.WriteAllText(manifestPath, CreateNativeHostManifest(executablePath, browser.ConnectorKind));

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            browser.RegistryRoot + HostName,
            writable: true);
        key.SetValue("", manifestPath);
    }

    public static int RemoveNativeHostRegistrations()
    {
        int removed = 0;
        foreach (BrowserDefinition browser in Browsers)
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
