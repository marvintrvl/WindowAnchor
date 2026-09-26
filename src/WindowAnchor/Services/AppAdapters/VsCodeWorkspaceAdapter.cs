using System;
using System.Collections.Generic;
using System.IO;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Captures VS Code workspace context from the existing bounded resource resolver and restores it
/// through the documented Code CLI. Cursor deliberately remains on its established generic path.
/// </summary>
internal sealed class VsCodeWorkspaceAdapter : IAppAdapter
{
    private readonly CaptureResourceResolver? _resourceResolver;

    internal VsCodeWorkspaceAdapter(CaptureResourceResolver? resourceResolver = null) =>
        _resourceResolver = resourceResolver;

    public string Name => "vs-code-workspace";
    public IAppReadinessStrategy? ReadinessStrategy => null;
    public IWindowPlacementVerificationStrategy? PlacementVerificationStrategy => null;

    public bool CanHandle(WindowRecord window) => IsVsCode(window.ProcessName);

    public bool CanHandle(WorkspaceEntry entry) =>
        IsVsCode(entry.ProcessName) &&
        (entry.EditorWorkspaceKind != EditorWorkspaceKind.None ||
         !string.IsNullOrWhiteSpace(entry.LaunchArg));

    public WorkspaceEntry? TryCapture(AppAdapterCaptureContext context)
    {
        if (_resourceResolver is null)
            return null;

        CapturedResource resource = _resourceResolver.Resolve(
            context.Window,
            context.SaveFiles,
            context.FolderSearchBudget,
            context.Progress,
            context.ResourceProgressCurrent,
            context.ResourceProgressTotal,
            context.BuildFullJumpListCache);
        WorkspaceEntry entry = AppAdapterEntryFactory.CreateBaseEntry(context.Window);
        entry.FilePath = resource.FilePath;
        entry.FileConfidence = resource.Confidence;
        entry.FileSource = resource.Source;
        entry.LaunchArg = resource.LaunchArgument;
        entry.EditorWorkspaceKind = ClassifyWorkspace(resource.LaunchArgument);
        return entry;
    }

    public SavedWindowIdentity EnrichIdentity(WorkspaceEntry entry, SavedWindowIdentity identity) =>
        identity with { AppAdapterIdentity = Name };

    public bool TryPlanLaunch(AppAdapterLaunchContext context, out RestoreLaunchDecision decision)
    {
        WorkspaceEntry entry = context.Entry;
        var warnings = new List<RestorePlanIssue>();
        var errors = new List<RestorePlanIssue>();
        RestoreResourceObservation? workspace = RestoreLaunchPlanner.GetResource(
            context.Resources,
            context.EntryIndex,
            RestoreResourceKind.LaunchTarget);
        if (RestoreLaunchPlanner.IsUnavailable(workspace, errors))
        {
            decision = RestoreLaunchPlanner.Blocked(
                errors,
                warnings,
                "The saved VS Code workspace or folder is unavailable.");
            return true;
        }

        string workspaceTarget = RestoreLaunchPlanner.FirstNonEmpty(
            workspace?.ResolvedTarget,
            entry.LaunchArg);
        if (workspaceTarget.Length == 0)
        {
            decision = null!;
            return false;
        }

        RestoreResourceObservation? executable = RestoreLaunchPlanner.GetResource(
            context.Resources,
            context.EntryIndex,
            RestoreResourceKind.Executable);
        if (string.IsNullOrWhiteSpace(entry.ExecutablePath))
        {
            errors.Add(RestoreLaunchPlanner.Error(
                RestorePlanIssueCode.MissingExecutable,
                "The VS Code workspace entry has no executable path."));
            decision = RestoreLaunchPlanner.Blocked(errors, warnings, "The VS Code executable is missing.");
            return true;
        }
        if (RestoreLaunchPlanner.IsUnavailable(executable, errors))
        {
            decision = RestoreLaunchPlanner.Blocked(errors, warnings, "The VS Code executable is unavailable.");
            return true;
        }

        RestoreLaunchPlanner.AddUnknownAvailabilityWarning(warnings, workspace);
        RestoreLaunchPlanner.AddUnknownAvailabilityWarning(warnings, executable);
        string windowMode = context.PreferFreshInstance || !context.HasSelectedMatch
            ? "--new-window"
            : "--reuse-window";
        decision = RestoreLaunchPlanner.Launch(
            context.EntryIndex,
            RestoreLaunchKind.Resource,
            RestoreActionKind.OpenResource,
            RestoreLaunchPlanner.FirstNonEmpty(executable?.ResolvedTarget, entry.ExecutablePath),
            $"{windowMode} \"{workspaceTarget}\"",
            useShellExecute: false,
            workspace?.Availability ?? RestoreResourceAvailability.Unknown,
            context.HasSelectedMatch && !context.PreferFreshInstance
                ? "Open the saved VS Code workspace in the assigned editor window."
                : "Open the saved VS Code workspace in a new editor window.",
            LogSensitivity.Path,
            LogSensitivity.CommandLine,
            warnings,
            errors);
        return true;
    }

    internal static EditorWorkspaceKind ClassifyWorkspace(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathRooted(target))
            return EditorWorkspaceKind.None;
        return target.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase)
            ? EditorWorkspaceKind.WorkspaceFile
            : EditorWorkspaceKind.Folder;
    }

    private static bool IsVsCode(string? processName) =>
        string.Equals(processName, "Code", StringComparison.OrdinalIgnoreCase);
}
