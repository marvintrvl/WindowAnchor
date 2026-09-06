using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Central, side-effect-free extraction of saved and live window identity evidence.</summary>
public static partial class WindowIdentityExtractor
{
    public static SavedWindowIdentity FromSaved(WorkspaceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string launchPath = entry.LaunchArg ?? "";
        string folderPath = entry.Position?.FolderPath ?? "";
        bool projectLike = IsProjectLike(entry.ProcessName, launchPath);

        return new SavedWindowIdentity
        {
            EntryId = entry.EntryId,
            ExecutablePath = NormalizePath(entry.ExecutablePath),
            ProcessName = ProcessIdentityNormalizer.Normalize(entry.ProcessName),
            WindowClassName = entry.WindowClassName ?? "",
            AppUserModelId = entry.AppUserModelId ?? "",
            PackageFamilyName = PackageFamily(entry.AppUserModelId),
            DocumentPath = NormalizePath(entry.FilePath),
            LaunchTargetPath = projectLike ? "" : NormalizePath(launchPath),
            FolderPath = NormalizePath(folderPath),
            ProjectOrWorkspacePath = projectLike ? NormalizePath(launchPath) : "",
            BrowserFamily = BrowserFamily(entry.ProcessName),
            BrowserProfile = BrowserProfile(entry.WebAppLaunchArguments),
            BrowserUrl = entry.BrowserUrl ?? "",
            BrowserSiteHost = SiteHost(entry.BrowserUrl),
            PwaIdentity = entry.IsWebApp ? entry.AppUserModelId ?? "" : "",
            SafeLaunchArguments = LogRedactor.RedactValue(
                entry.WebAppLaunchArguments,
                LogSensitivity.CommandLine,
                LogRedactionMode.Redacted),
            Title = entry.Position?.TitleSnippet ?? "",
            NormalizedTitleTokens = NormalizeTitleTokens(entry.Position?.TitleSnippet),
            SavedMonitorId = entry.MonitorId ?? "",
            SavedMonitorIndex = entry.MonitorIndex,
            PreviousBounds = Bounds(entry.Position),
            IsWebApp = entry.IsWebApp,
            IsDedicatedBrowserWindow = entry.IsDedicatedBrowserWindow
        };
    }

    public static LiveWindowIdentity FromLive(IntPtr hwnd, uint processId, WindowRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string aumid = record.AppUserModelId ?? "";
        return new LiveWindowIdentity
        {
            Hwnd = hwnd,
            ProcessId = processId,
            ExecutablePath = NormalizePath(record.ExecutablePath),
            ProcessName = ProcessIdentityNormalizer.Normalize(record.ProcessName),
            WindowClassName = record.ClassName ?? "",
            AppUserModelId = aumid,
            PackageFamilyName = PackageFamily(aumid),
            FolderPath = NormalizePath(record.FolderPath),
            BrowserFamily = BrowserFamily(record.ProcessName),
            BrowserUrl = record.BrowserUrl ?? "",
            BrowserSiteHost = SiteHost(record.BrowserUrl),
            PwaIdentity = WebAppService.LooksLikeWebAppAumid(aumid) ? aumid : "",
            NormalizedTitleTokens = NormalizeTitleTokens(record.TitleSnippet),
            MonitorId = record.MonitorId ?? "",
            MonitorIndex = record.MonitorIndex,
            Bounds = Bounds(record),
            ShowCmd = record.ShowCmd,
            Title = record.TitleSnippet ?? "",
            IsWebApp = WebAppService.LooksLikeWebAppAumid(aumid),
            IsDedicatedBrowserWindow = !string.IsNullOrWhiteSpace(record.BrowserUrl)
        };
    }

    /// <summary>
    /// Creates a persistable composite identity from a live candidate without copying HWND/PID.
    /// </summary>
    public static WindowIdentityHint ToHint(LiveWindowIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new WindowIdentityHint
        {
            ExecutablePath = identity.ExecutablePath,
            ProcessName = identity.ProcessName,
            WindowClassName = identity.WindowClassName,
            AppUserModelId = identity.AppUserModelId,
            PackageFamilyName = identity.PackageFamilyName,
            FolderPath = identity.FolderPath,
            BrowserFamily = identity.BrowserFamily,
            BrowserSiteHost = identity.BrowserSiteHost,
            PwaIdentity = identity.PwaIdentity,
            TitleTokens = identity.NormalizedTitleTokens
                .OrderBy(token => token, StringComparer.Ordinal)
                .ToArray()
        };
    }

    public static IReadOnlyList<string> NormalizeTitleTokens(string? title) =>
        TitleTokenRegex().Matches(title ?? "")
            .Select(match => match.Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    internal static string NormalizePath(string? path) =>
        (path ?? "")
            .Trim()
            .Trim('"')
            .Replace('/', '\\')
            .TrimEnd('\\')
            .ToLowerInvariant();

    internal static string PackageFamily(string? appUserModelId)
    {
        string value = appUserModelId ?? "";
        int separator = value.IndexOf('!');
        return separator > 0 ? value[..separator].ToLowerInvariant() : "";
    }

    private static string BrowserFamily(string? processName) =>
        WebAppService.IsChromiumBrowser(processName ?? "")
            ? ProcessIdentityNormalizer.Normalize(processName)
            : "";

    private static string BrowserProfile(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return "";
        Match match = BrowserProfileRegex().Match(arguments);
        return match.Success ? match.Groups["profile"].Value.Trim('"', '\'').ToLowerInvariant() : "";
    }

    private static string SiteHost(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? parsed.IdnHost.ToLowerInvariant()
            : "";

    private static bool IsProjectLike(string? processName, string path)
    {
        string process = ProcessIdentityNormalizer.Normalize(processName);
        return process is "code" or "cursor" ||
               path.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase);
    }

    private static WindowIdentityBounds Bounds(WindowRecord? record) => record == null
        ? default
        : new WindowIdentityBounds(
            record.NormalLeft,
            record.NormalTop,
            record.NormalRight,
            record.NormalBottom);

    [GeneratedRegex("[\\p{L}\\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex TitleTokenRegex();

    [GeneratedRegex("(?i)(?:--profile-directory(?:=|\\s+))(?<profile>\\\"[^\\\"]+\\\"|'[^']+'|[^\\s]+)")]
    private static partial Regex BrowserProfileRegex();
}
