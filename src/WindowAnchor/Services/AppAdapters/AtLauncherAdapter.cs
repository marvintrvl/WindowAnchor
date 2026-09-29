using System;
using System.Collections.Generic;
using System.IO;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// ATLauncher exposes its windows from a bundled javaw process. Restoring javaw without the
/// original Java command line is invalid; the stable native launcher must be started instead.
/// </summary>
internal sealed class AtLauncherAdapter : IAppAdapter
{
    internal const string AdapterName = "atlauncher";
    public string Name => AdapterName;
    public IAppReadinessStrategy? ReadinessStrategy => null;
    public IWindowPlacementVerificationStrategy? PlacementVerificationStrategy => null;

    public bool CanHandle(WindowRecord window) => IsAtLauncherJava(window.ProcessName, window.ExecutablePath);
    public bool CanHandle(WorkspaceEntry entry) => IsAtLauncherJava(entry.ProcessName, entry.ExecutablePath);

    public WorkspaceEntry? TryCapture(AppAdapterCaptureContext context) =>
        AppAdapterEntryFactory.CreateBaseEntry(context.Window);

    public SavedWindowIdentity EnrichIdentity(WorkspaceEntry entry, SavedWindowIdentity identity) =>
        identity with { AppAdapterIdentity = AdapterName };

    public bool TryPlanLaunch(AppAdapterLaunchContext context, out RestoreLaunchDecision decision)
    {
        if (context.HasSelectedMatch)
        {
            decision = null!;
            return false;
        }

        var warnings = new List<RestorePlanIssue>();
        var errors = new List<RestorePlanIssue>();
        if (context.Entry.Position.TitleSnippet.Contains("Console", StringComparison.OrdinalIgnoreCase))
        {
            decision = new RestoreLaunchDecision(
                RestoreLaunchRequirement.None(
                    "The primary ATLauncher entry starts the launcher; wait for its console window."),
                [new RestoreAction(
                    context.EntryIndex, RestoreActionKind.AwaitWindowAppearance, null, "", "", false,
                    context.Placement,
                    "Wait for the ATLauncher process to create its saved console window.")],
                AwaitingBrowserSession: false,
                AwaitingRunningApplication: true,
                NoRestorableWindow: false,
                warnings,
                errors);
            return true;
        }

        RestoreResourceObservation? launcher = RestoreLaunchPlanner.GetResource(
            context.Resources, context.EntryIndex, RestoreResourceKind.AppAdapterLauncher);
        if (RestoreLaunchPlanner.IsUnavailable(launcher, errors))
        {
            decision = RestoreLaunchPlanner.Blocked(errors, warnings,
                "ATLauncher.exe is unavailable; its bundled javaw.exe cannot be launched by itself.");
            return true;
        }
        RestoreLaunchPlanner.AddUnknownAvailabilityWarning(warnings, launcher);
        decision = RestoreLaunchPlanner.Launch(
            context.EntryIndex,
            RestoreLaunchKind.Application,
            RestoreActionKind.LaunchApplication,
            launcher?.ResolvedTarget ?? ResolveLauncherPath(context.Entry),
            "",
            useShellExecute: true,
            launcher?.Availability ?? RestoreResourceAvailability.Unknown,
            "Start ATLauncher through its stable Windows launcher instead of its Java runtime.",
            LogSensitivity.Path,
            LogSensitivity.CommandLine,
            warnings,
            errors);
        return true;
    }

    internal static string ResolveLauncherPath(WorkspaceEntry entry) =>
        ResolveLauncherPath(entry.ProcessName, entry.ExecutablePath);

    private static string ResolveLauncherPath(string processName, string executablePath)
    {
        if (!IsAtLauncherJava(processName, executablePath)) return "";
        try
        {
            string? bin = Path.GetDirectoryName(executablePath);
            return bin is null ? "" : Path.GetFullPath(Path.Combine(bin, "..", "..", "ATLauncher.exe"));
        }
        catch { return ""; }
    }

    private static bool IsAtLauncherJava(string processName, string executablePath) =>
        processName.Equals("javaw", StringComparison.OrdinalIgnoreCase) &&
        executablePath.Replace('/', '\\').Contains("\\ATLauncher\\jre\\bin\\javaw.exe",
            StringComparison.OrdinalIgnoreCase);
}
