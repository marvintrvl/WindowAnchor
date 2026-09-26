using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Restores explicitly captured Windows Terminal tabs with wt's multi-command syntax.</summary>
internal sealed class WindowsTerminalAdapter : IAppAdapter
{
    public string Name => "windows-terminal";
    public IAppReadinessStrategy? ReadinessStrategy => null;
    public IWindowPlacementVerificationStrategy? PlacementVerificationStrategy => null;

    public bool CanHandle(WindowRecord window) =>
        window.ProcessName.Equals("windowsterminal", StringComparison.OrdinalIgnoreCase);

    public bool CanHandle(WorkspaceEntry entry) =>
        entry.ProcessName.Equals("windowsterminal", StringComparison.OrdinalIgnoreCase) &&
        entry.TerminalTabs.Count > 0;

    public WorkspaceEntry? TryCapture(AppAdapterCaptureContext context)
    {
        WorkspaceEntry entry = AppAdapterEntryFactory.CreateBaseEntry(context.Window);
        entry.FileSource = context.SaveFiles ? "TERMINAL_TABS" : "NONE";
        if (context.SaveFiles)
        {
            entry.TerminalTabs = context.Window.TerminalTabs.Select(tab => new TerminalTab
            {
                Profile = tab.Profile,
                StartingDirectory = tab.StartingDirectory
            }).ToList();
            entry.TerminalActiveTabIndex = entry.TerminalTabs.Count == 0 ? 0 :
                Math.Clamp(context.Window.TerminalActiveTabIndex, 0, entry.TerminalTabs.Count - 1);
        }
        return entry;
    }

    public SavedWindowIdentity EnrichIdentity(WorkspaceEntry entry, SavedWindowIdentity identity) =>
        identity with { AppAdapterIdentity = Name };

    public bool TryPlanLaunch(AppAdapterLaunchContext context, out RestoreLaunchDecision decision)
    {
        var warnings = new List<RestorePlanIssue>();
        var errors = new List<RestorePlanIssue>();
        if (context.Entry.TerminalTabs.Any(tab => string.IsNullOrWhiteSpace(tab.StartingDirectory)))
        {
            errors.Add(RestoreLaunchPlanner.Error(RestorePlanIssueCode.MissingResource,
                "A saved Terminal tab has no starting directory."));
            decision = RestoreLaunchPlanner.Blocked(errors, warnings, "Terminal tab directories are incomplete.");
            return true;
        }

        RestoreResourceObservation? directories = RestoreLaunchPlanner.GetResource(
            context.Resources, context.EntryIndex, RestoreResourceKind.TerminalDirectories);
        if (RestoreLaunchPlanner.IsUnavailable(directories, errors))
        {
            decision = RestoreLaunchPlanner.Blocked(errors, warnings,
                "A saved Terminal tab directory is unavailable.");
            return true;
        }
        RestoreLaunchPlanner.AddUnknownAvailabilityWarning(warnings, directories);

        RestoreResourceObservation? launcher = RestoreLaunchPlanner.GetResource(
            context.Resources, context.EntryIndex, RestoreResourceKind.TerminalLauncher);
        if (RestoreLaunchPlanner.IsUnavailable(launcher, errors))
        {
            decision = RestoreLaunchPlanner.Blocked(errors, warnings,
                "The Windows Terminal wt.exe execution alias is unavailable.");
            return true;
        }
        RestoreLaunchPlanner.AddUnknownAvailabilityWarning(warnings, launcher);
        RestoreLaunchDecision launch = RestoreLaunchPlanner.Launch(
            context.EntryIndex, RestoreLaunchKind.Application, RestoreActionKind.LaunchApplication,
            launcher?.ResolvedTarget ?? "",
            BuildArguments(context.Entry.TerminalTabs, context.Entry.TerminalActiveTabIndex),
            useShellExecute: true,
            launcher?.Availability ?? RestoreResourceAvailability.Unknown,
            "Open the saved Windows Terminal tabs in a new window.",
            LogSensitivity.Path, LogSensitivity.CommandLine, warnings, errors);
        decision = launch with
        {
            Actions = launch.Actions.Select(action => action with
            {
                TerminalDirectories = context.Entry.TerminalTabs
                    .Select(tab => tab.StartingDirectory).ToArray()
            }).ToArray()
        };
        return true;
    }

    internal static string BuildArguments(IReadOnlyList<TerminalTab> tabs, int activeIndex)
    {
        // -w -1 is Terminal's documented sentinel for a guaranteed new window. "new" is a
        // user-defined window name and may target an existing window instead.
        var result = new StringBuilder("-w -1 ");
        for (int index = 0; index < tabs.Count; index++)
        {
            if (index > 0) result.Append(" ; ");
            result.Append("new-tab");
            if (!string.IsNullOrWhiteSpace(tabs[index].Profile))
                result.Append(" --profile ").Append(Quote(tabs[index].Profile));
            result.Append(" --startingDirectory ").Append(Quote(tabs[index].StartingDirectory));
        }
        if (tabs.Count > 1)
            result.Append(" ; focus-tab -t ").Append(Math.Clamp(activeIndex, 0, tabs.Count - 1));
        return result.ToString();
    }

    private static string Quote(string value)
    {
        // Windows CreateProcess quoting, including a path that ends in a backslash.
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }
}
