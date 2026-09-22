using System;
using System.Collections.Generic;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Captures and restores Chromium installed web apps through their stable AUMID identity.</summary>
internal sealed class ChromiumWebAppAdapter : IAppAdapter
{
    private readonly WebAppService _webAppService;

    internal ChromiumWebAppAdapter(WebAppService webAppService) =>
        _webAppService = webAppService ?? throw new ArgumentNullException(nameof(webAppService));

    public string Name => "chromium-web-app";
    public IAppReadinessStrategy? ReadinessStrategy => null;
    public IWindowPlacementVerificationStrategy? PlacementVerificationStrategy => null;

    public bool CanHandle(WindowRecord window) =>
        WebAppService.IsChromiumBrowser(window.ProcessName) &&
        !string.IsNullOrEmpty(window.AppUserModelId);

    public bool CanHandle(WorkspaceEntry entry) => entry.IsWebApp;

    public WorkspaceEntry? TryCapture(AppAdapterCaptureContext context)
    {
        WindowRecord window = context.Window;
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
        WorkspaceEntry entry = AppAdapterEntryFactory.CreateBaseEntry(window);
        entry.FileSource = source;
        entry.IsWebApp = true;
        entry.WebAppName = name;
        entry.WebAppShortcutPath = shortcutPath;
        entry.WebAppLaunchTarget = target;
        entry.WebAppLaunchArguments = arguments;
        return entry;
    }

    public SavedWindowIdentity EnrichIdentity(WorkspaceEntry entry, SavedWindowIdentity identity) =>
        identity with { AppAdapterIdentity = Name };

    public bool TryPlanLaunch(AppAdapterLaunchContext context, out RestoreLaunchDecision decision)
    {
        WorkspaceEntry entry = context.Entry;
        var warnings = new List<RestorePlanIssue>();
        var errors = new List<RestorePlanIssue>();
        RestoreResourceObservation? shortcut = RestoreLaunchPlanner.GetResource(
            context.Resources, context.EntryIndex, RestoreResourceKind.WebAppShortcut);
        string savedShortcut = entry.WebAppShortcutPath ?? "";
        if (shortcut?.Availability == RestoreResourceAvailability.Missing)
        {
            warnings.Add(RestoreLaunchPlanner.Warning(
                RestorePlanIssueCode.MissingResource,
                "The saved web-app shortcut is missing; planning will use a fallback target when available."));
        }
        else if (shortcut?.Availability == RestoreResourceAvailability.Stale)
        {
            warnings.Add(RestoreLaunchPlanner.Warning(
                RestorePlanIssueCode.StaleResource,
                "The saved web-app shortcut is stale; planning will use a fallback target when available."));
        }
        if (shortcut?.Availability == RestoreResourceAvailability.Available)
        {
            string target = RestoreLaunchPlanner.FirstNonEmpty(shortcut.ResolvedTarget, savedShortcut);
            if (target.Length > 0)
            {
                decision = RestoreLaunchPlanner.Launch(
                    context.EntryIndex, RestoreLaunchKind.WebApp, RestoreActionKind.LaunchWebApp,
                    target, "", true, shortcut.Availability,
                    "Launch the installed web app through its observed shortcut.",
                    LogSensitivity.Path, LogSensitivity.CommandLine, warnings, errors);
                return true;
            }
        }

        string fallbackTarget = RestoreLaunchPlanner.FirstNonEmpty(entry.WebAppLaunchTarget, entry.ExecutablePath);
        if (fallbackTarget.Length == 0 && savedShortcut.Length > 0 &&
            shortcut?.Availability is not (RestoreResourceAvailability.Missing or RestoreResourceAvailability.Stale))
        {
            RestoreLaunchPlanner.AddUnknownAvailabilityWarning(warnings);
            decision = RestoreLaunchPlanner.Launch(
                context.EntryIndex, RestoreLaunchKind.WebApp, RestoreActionKind.LaunchWebApp,
                savedShortcut, "", true, RestoreResourceAvailability.Unknown,
                "Launch the installed web app through its saved shortcut.",
                LogSensitivity.Path, LogSensitivity.CommandLine, warnings, errors);
            return true;
        }

        RestoreResourceObservation? executable = RestoreLaunchPlanner.GetResource(
            context.Resources, context.EntryIndex, RestoreResourceKind.Executable);
        if (fallbackTarget.Length == 0)
        {
            errors.Add(RestoreLaunchPlanner.Error(
                RestorePlanIssueCode.MissingWebAppLaunchTarget,
                "The saved web app has neither a usable shortcut nor a fallback launch target."));
            decision = RestoreLaunchPlanner.Blocked(errors, warnings, "The web app has no launch target.");
            return true;
        }
        if (RestoreLaunchPlanner.IsUnavailable(executable, errors))
        {
            decision = RestoreLaunchPlanner.Blocked(errors, warnings, "The web-app launch target is unavailable.");
            return true;
        }
        RestoreLaunchPlanner.AddUnknownAvailabilityWarning(warnings, executable);
        decision = RestoreLaunchPlanner.Launch(
            context.EntryIndex, RestoreLaunchKind.WebApp, RestoreActionKind.LaunchWebApp,
            RestoreLaunchPlanner.FirstNonEmpty(executable?.ResolvedTarget, fallbackTarget),
            entry.WebAppLaunchArguments ?? "", false,
            executable?.Availability ?? RestoreResourceAvailability.Unknown,
            "Launch the installed web app through its saved target and app identity arguments.",
            LogSensitivity.Path, LogSensitivity.CommandLine, warnings, errors);
        return true;
    }
}
