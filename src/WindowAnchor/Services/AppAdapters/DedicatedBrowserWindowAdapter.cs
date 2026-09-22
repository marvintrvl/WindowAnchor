using System;
using System.Collections.Generic;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Captures and restores a browser site that must reopen in its own window.</summary>
internal sealed class DedicatedBrowserWindowAdapter : IAppAdapter
{
    public string Name => "dedicated-browser-window";
    public IAppReadinessStrategy? ReadinessStrategy => null;
    public IWindowPlacementVerificationStrategy? PlacementVerificationStrategy => null;

    public bool CanHandle(WindowRecord window) => !string.IsNullOrEmpty(window.BrowserUrl);
    public bool CanHandle(WorkspaceEntry entry) => entry.IsDedicatedBrowserWindow;

    public WorkspaceEntry? TryCapture(AppAdapterCaptureContext context)
    {
        AppLogger.Info(
            "browser_url.dedicated_window_captured",
            "Captured a dedicated browser window",
            LogField.Url("url", context.Window.BrowserUrl),
            LogField.Public("processName", context.Window.ProcessName));
        WorkspaceEntry entry = AppAdapterEntryFactory.CreateBaseEntry(context.Window);
        entry.FileSource = "BROWSER_URL";
        entry.IsDedicatedBrowserWindow = true;
        entry.BrowserUrl = context.Window.BrowserUrl;
        return entry;
    }

    public SavedWindowIdentity EnrichIdentity(WorkspaceEntry entry, SavedWindowIdentity identity) =>
        identity with { AppAdapterIdentity = Name };

    public bool TryPlanLaunch(AppAdapterLaunchContext context, out RestoreLaunchDecision decision)
    {
        WorkspaceEntry entry = context.Entry;
        var warnings = new List<RestorePlanIssue>();
        var errors = new List<RestorePlanIssue>();
        if (string.IsNullOrWhiteSpace(entry.BrowserUrl))
        {
            errors.Add(RestoreLaunchPlanner.Error(
                RestorePlanIssueCode.MissingBrowserUrl,
                "The dedicated browser entry has no saved URL."));
            decision = RestoreLaunchPlanner.Blocked(errors, warnings, "The dedicated browser URL is missing.");
            return true;
        }

        RestoreResourceObservation? executable = RestoreLaunchPlanner.GetResource(
            context.Resources, context.EntryIndex, RestoreResourceKind.Executable);
        if (string.IsNullOrWhiteSpace(entry.ExecutablePath))
        {
            errors.Add(RestoreLaunchPlanner.Error(
                RestorePlanIssueCode.MissingExecutable,
                "The dedicated browser entry has no executable path."));
            decision = RestoreLaunchPlanner.Blocked(errors, warnings, "The dedicated browser executable is missing.");
            return true;
        }
        if (RestoreLaunchPlanner.IsUnavailable(executable, errors))
        {
            decision = RestoreLaunchPlanner.Blocked(errors, warnings, "The dedicated browser executable is unavailable.");
            return true;
        }
        RestoreLaunchPlanner.AddUnknownAvailabilityWarning(warnings, executable);
        decision = RestoreLaunchPlanner.Launch(
            context.EntryIndex, RestoreLaunchKind.DedicatedBrowser,
            RestoreActionKind.LaunchDedicatedBrowser,
            RestoreLaunchPlanner.FirstNonEmpty(executable?.ResolvedTarget, entry.ExecutablePath),
            $"--new-window \"{entry.BrowserUrl}\"", false,
            executable?.Availability ?? RestoreResourceAvailability.Unknown,
            "Open the saved site in its own browser window.",
            LogSensitivity.Path, LogSensitivity.CommandLine, warnings, errors);
        return true;
    }
}
