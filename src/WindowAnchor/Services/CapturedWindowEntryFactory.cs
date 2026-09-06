using System;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Creates the persisted entry shape for one already-enumerated window.</summary>
internal sealed class CapturedWindowEntryFactory
{
    private readonly WebAppService _webAppService;
    private readonly CaptureResourceResolver _resourceResolver;

    internal CapturedWindowEntryFactory(
        WebAppService webAppService,
        CaptureResourceResolver resourceResolver)
    {
        _webAppService = webAppService ?? throw new ArgumentNullException(nameof(webAppService));
        _resourceResolver = resourceResolver ?? throw new ArgumentNullException(nameof(resourceResolver));
    }

    internal WorkspaceEntry Create(
        WindowRecord window,
        bool saveFiles,
        CaptureResourceSearchBudget folderSearchBudget,
        IProgress<SaveProgressReport>? progress,
        int resourceProgressCurrent,
        int resourceProgressTotal,
        bool buildFullJumpListCache)
    {
        WorkspaceEntry? specialEntry = TryCreateWebApp(window) ??
                                       TryCreateDedicatedBrowserWindow(window);
        if (specialEntry != null)
            return specialEntry;

        if (window.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(window.FolderPath))
        {
            return CreateExplorer(window, saveFiles);
        }

        CapturedResource resource = _resourceResolver.Resolve(
            window,
            saveFiles,
            folderSearchBudget,
            progress,
            resourceProgressCurrent,
            resourceProgressTotal,
            buildFullJumpListCache);
        WorkspaceEntry entry = CreateBaseEntry(window);
        entry.FilePath = resource.FilePath;
        entry.FileConfidence = resource.Confidence;
        entry.FileSource = resource.Source;
        entry.LaunchArg = resource.LaunchArgument;
        return entry;
    }

    private WorkspaceEntry? TryCreateWebApp(WindowRecord window)
    {
        if (!WebAppService.IsChromiumBrowser(window.ProcessName) ||
            string.IsNullOrEmpty(window.AppUserModelId))
        {
            return null;
        }

        WebAppInfo? info = _webAppService.FindByAumid(window.AppUserModelId);
        if (info == null && !WebAppService.LooksLikeWebAppAumid(window.AppUserModelId))
            return null;

        string? shortcutPath;
        string? target;
        string? arguments;
        string name;
        string source;

        if (info != null)
        {
            shortcutPath = info.ShortcutPath;
            target = info.TargetPath;
            arguments = info.Arguments;
            name = info.DisplayName;
            source = "WEB_APP_SHORTCUT";
        }
        else
        {
            string? appId = WebAppService.ExtractAppIdFromAumid(window.AppUserModelId);
            if (appId == null) return null;

            shortcutPath = null;
            target = window.ExecutablePath;
            arguments = $"--app-id={appId}";
            name = window.TitleSnippet;
            source = "WEB_APP_AUMID";
            AppLogger.Warn(
                "web_app.shortcut_not_found",
                "No web-app shortcut was found; using an AUMID-derived launch command",
                LogField.Identifier("appUserModelId", window.AppUserModelId),
                LogField.Path("launchTarget", target),
                LogField.CommandLine("launchArguments", arguments));
        }

        AppLogger.Info(
            "web_app.detected",
            "Detected an installed browser web app",
            LogField.Title("webAppName", name),
            LogField.Identifier("appUserModelId", window.AppUserModelId),
            LogField.Public("source", source));

        WorkspaceEntry entry = CreateBaseEntry(window);
        entry.FileSource = source;
        entry.IsWebApp = true;
        entry.WebAppName = name;
        entry.WebAppShortcutPath = shortcutPath;
        entry.WebAppLaunchTarget = target;
        entry.WebAppLaunchArguments = arguments;
        return entry;
    }

    private static WorkspaceEntry? TryCreateDedicatedBrowserWindow(WindowRecord window)
    {
        if (string.IsNullOrEmpty(window.BrowserUrl)) return null;

        AppLogger.Info(
            "browser_url.dedicated_window_captured",
            "Captured a dedicated browser window",
            LogField.Url("url", window.BrowserUrl),
            LogField.Public("processName", window.ProcessName));

        WorkspaceEntry entry = CreateBaseEntry(window);
        entry.FileSource = "BROWSER_URL";
        entry.IsDedicatedBrowserWindow = true;
        entry.BrowserUrl = window.BrowserUrl;
        return entry;
    }

    private static WorkspaceEntry CreateExplorer(WindowRecord window, bool saveFiles)
    {
        WorkspaceEntry entry = CreateBaseEntry(window);
        entry.FilePath = saveFiles ? window.FolderPath : null;
        entry.FileConfidence = saveFiles ? 95 : 0;
        entry.FileSource = saveFiles ? "EXPLORER_FOLDER" : "NONE";
        entry.LaunchArg = saveFiles ? window.FolderPath : null;
        return entry;
    }

    private static WorkspaceEntry CreateBaseEntry(WindowRecord window) => new()
    {
        ExecutablePath = window.ExecutablePath,
        ProcessName = window.ProcessName,
        WindowClassName = window.ClassName,
        AppUserModelId = window.AppUserModelId,
        Position = window,
        MonitorId = window.MonitorId,
        MonitorIndex = window.MonitorIndex,
        MonitorName = window.MonitorName
    };
}
