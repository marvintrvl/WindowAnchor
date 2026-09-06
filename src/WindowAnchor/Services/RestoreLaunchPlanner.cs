using System;
using System.Collections.Generic;
using System.Linq;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Pure launch and resource-availability policy for one restore-plan entry.
/// It emits action data only and never touches processes, files, browsers, or native windows.
/// </summary>
internal static class RestoreLaunchPlanner
{
    internal static RestoreLaunchDecision Plan(
        int entryIndex,
        WorkspaceEntry entry,
        bool hasSelectedMatch,
        bool correctResourceMatched,
        bool browserSessionScheduled,
        IReadOnlyList<RunningApplicationIdentity> runningApplications,
        IReadOnlySet<string> pendingDocumentExecutables,
        IReadOnlyDictionary<(int EntryIndex, RestoreResourceKind Kind), RestoreResourceObservation> resources,
        RestoreTargetPlacement placement)
    {
        var warnings = new List<RestorePlanIssue>();
        var errors = new List<RestorePlanIssue>();

        if (hasSelectedMatch && (string.IsNullOrWhiteSpace(entry.LaunchArg) || correctResourceMatched))
        {
            return new RestoreLaunchDecision(
                RestoreLaunchRequirement.None("The selected live window already satisfies this entry."),
                Array.Empty<RestoreAction>(),
                false,
                false,
                false,
                warnings,
                errors);
        }

        if (!hasSelectedMatch && browserSessionScheduled && RestorePlannerPolicies.IsBrowserProcess(entry.ProcessName))
        {
            RestoreResourceObservation? browserExecutable = GetResource(
                resources,
                entryIndex,
                RestoreResourceKind.Executable);
            var fallbackAction = new RestoreAction(
                entryIndex,
                RestoreActionKind.LaunchApplication,
                WindowHandle: null,
                Target: FirstNonEmpty(browserExecutable?.ResolvedTarget, entry.ExecutablePath),
                Arguments: "--restore-last-session",
                UseShellExecute: false,
                TargetPlacement: null,
                "Launch the browser directly only when browser-session restoration is unavailable.",
                LogSensitivity.Path,
                LogSensitivity.CommandLine,
                RestoreActionCondition.BrowserSessionUnavailable);
            var awaitAction = new RestoreAction(
                entryIndex,
                RestoreActionKind.AwaitWindowAppearance,
                WindowHandle: null,
                Target: "",
                Arguments: "",
                UseShellExecute: false,
                placement,
                "Wait for the browser-session connector to recreate the saved browser window.");
            return new RestoreLaunchDecision(
                RestoreLaunchRequirement.None("Browser-session restoration replaces a direct process launch."),
                [fallbackAction, awaitAction],
                AwaitingBrowserSession: true,
                AwaitingRunningApplication: false,
                NoRestorableWindow: false,
                warnings,
                errors);
        }

        if (entry.IsWebApp)
        {
            RestoreResourceObservation? shortcut = GetResource(
                resources,
                entryIndex,
                RestoreResourceKind.WebAppShortcut);
            string savedShortcut = entry.WebAppShortcutPath ?? "";
            if (shortcut?.Availability == RestoreResourceAvailability.Missing)
            {
                warnings.Add(Warning(
                    RestorePlanIssueCode.MissingResource,
                    "The saved web-app shortcut is missing; planning will use a fallback target when available."));
            }
            else if (shortcut?.Availability == RestoreResourceAvailability.Stale)
            {
                warnings.Add(Warning(
                    RestorePlanIssueCode.StaleResource,
                    "The saved web-app shortcut is stale; planning will use a fallback target when available."));
            }
            if (shortcut?.Availability == RestoreResourceAvailability.Available)
            {
                string target = FirstNonEmpty(shortcut.ResolvedTarget, savedShortcut);
                if (target.Length > 0)
                    return Launch(
                        entryIndex,
                        RestoreLaunchKind.WebApp,
                        RestoreActionKind.LaunchWebApp,
                        target,
                        "",
                        useShellExecute: true,
                        shortcut.Availability,
                        "Launch the installed web app through its observed shortcut.",
                        LogSensitivity.Path,
                        LogSensitivity.CommandLine,
                        warnings,
                        errors);
            }

            string fallbackTarget = FirstNonEmpty(entry.WebAppLaunchTarget, entry.ExecutablePath);
            if (fallbackTarget.Length == 0 && savedShortcut.Length > 0 &&
                shortcut?.Availability is not (RestoreResourceAvailability.Missing or RestoreResourceAvailability.Stale))
            {
                fallbackTarget = savedShortcut;
                AddUnknownAvailabilityWarning(warnings);
                return Launch(
                    entryIndex,
                    RestoreLaunchKind.WebApp,
                    RestoreActionKind.LaunchWebApp,
                    fallbackTarget,
                    "",
                    useShellExecute: true,
                    RestoreResourceAvailability.Unknown,
                    "Launch the installed web app through its saved shortcut.",
                    LogSensitivity.Path,
                    LogSensitivity.CommandLine,
                    warnings,
                    errors);
            }

            RestoreResourceObservation? executable = GetResource(
                resources,
                entryIndex,
                RestoreResourceKind.Executable);
            if (fallbackTarget.Length == 0)
            {
                errors.Add(Error(
                    RestorePlanIssueCode.MissingWebAppLaunchTarget,
                    "The saved web app has neither a usable shortcut nor a fallback launch target."));
                return Blocked(errors, warnings, "The web app has no launch target.");
            }
            if (IsUnavailable(executable, errors))
                return Blocked(errors, warnings, "The web-app launch target is unavailable.");
            AddUnknownAvailabilityWarning(warnings, executable);
            return Launch(
                entryIndex,
                RestoreLaunchKind.WebApp,
                RestoreActionKind.LaunchWebApp,
                FirstNonEmpty(executable?.ResolvedTarget, fallbackTarget),
                entry.WebAppLaunchArguments ?? "",
                useShellExecute: false,
                executable?.Availability ?? RestoreResourceAvailability.Unknown,
                "Launch the installed web app through its saved target and app identity arguments.",
                LogSensitivity.Path,
                LogSensitivity.CommandLine,
                warnings,
                errors);
        }

        if (entry.IsDedicatedBrowserWindow)
        {
            if (string.IsNullOrWhiteSpace(entry.BrowserUrl))
            {
                errors.Add(Error(
                    RestorePlanIssueCode.MissingBrowserUrl,
                    "The dedicated browser entry has no saved URL."));
                return Blocked(errors, warnings, "The dedicated browser URL is missing.");
            }

            RestoreResourceObservation? executable = GetResource(
                resources,
                entryIndex,
                RestoreResourceKind.Executable);
            if (string.IsNullOrWhiteSpace(entry.ExecutablePath))
            {
                errors.Add(Error(
                    RestorePlanIssueCode.MissingExecutable,
                    "The dedicated browser entry has no executable path."));
                return Blocked(errors, warnings, "The dedicated browser executable is missing.");
            }
            if (IsUnavailable(executable, errors))
                return Blocked(errors, warnings, "The dedicated browser executable is unavailable.");
            AddUnknownAvailabilityWarning(warnings, executable);
            return Launch(
                entryIndex,
                RestoreLaunchKind.DedicatedBrowser,
                RestoreActionKind.LaunchDedicatedBrowser,
                FirstNonEmpty(executable?.ResolvedTarget, entry.ExecutablePath),
                $"--new-window \"{entry.BrowserUrl}\"",
                useShellExecute: false,
                executable?.Availability ?? RestoreResourceAvailability.Unknown,
                "Open the saved site in its own browser window.",
                LogSensitivity.Path,
                LogSensitivity.CommandLine,
                warnings,
                errors);
        }

        if (!string.IsNullOrWhiteSpace(entry.LaunchArg))
        {
            RestoreResourceObservation? resource = GetResource(
                resources,
                entryIndex,
                RestoreResourceKind.LaunchTarget);
            if (IsUnavailable(resource, errors))
                return Blocked(errors, warnings, "The saved document, folder, or resource is unavailable.");
            AddUnknownAvailabilityWarning(warnings, resource);
            string target = FirstNonEmpty(resource?.ResolvedTarget, entry.LaunchArg);

            if (entry.ProcessName.Equals("Code", StringComparison.OrdinalIgnoreCase))
            {
                RestoreResourceObservation? executable = GetResource(
                    resources,
                    entryIndex,
                    RestoreResourceKind.Executable);
                if (string.IsNullOrWhiteSpace(entry.ExecutablePath))
                {
                    errors.Add(Error(
                        RestorePlanIssueCode.MissingExecutable,
                        "The project entry has no executable path."));
                    return Blocked(errors, warnings, "The project application executable is missing.");
                }
                if (IsUnavailable(executable, errors))
                    return Blocked(errors, warnings, "The project application executable is unavailable.");
                return Launch(
                    entryIndex,
                    RestoreLaunchKind.Resource,
                    RestoreActionKind.OpenResource,
                    FirstNonEmpty(executable?.ResolvedTarget, entry.ExecutablePath),
                    $"\"{target}\"",
                    useShellExecute: false,
                    resource?.Availability ?? RestoreResourceAvailability.Unknown,
                    "Open the saved project or folder through the application CLI.",
                    LogSensitivity.Path,
                    LogSensitivity.CommandLine,
                    warnings,
                    errors);
            }

            return Launch(
                entryIndex,
                RestoreLaunchKind.Resource,
                RestoreActionKind.OpenResource,
                target,
                "",
                useShellExecute: true,
                resource?.Availability ?? RestoreResourceAvailability.Unknown,
                "Open the saved resource through its registered handler.",
                LogSensitivity.Path,
                LogSensitivity.CommandLine,
                warnings,
                errors);
        }

        if (hasSelectedMatch)
        {
            return new RestoreLaunchDecision(
                RestoreLaunchRequirement.None("The selected live window already satisfies this entry."),
                Array.Empty<RestoreAction>(),
                false,
                false,
                false,
                warnings,
                errors);
        }

        RestoreResourceObservation? appExecutable = GetResource(
            resources,
            entryIndex,
            RestoreResourceKind.Executable);
        string effectiveExecutable = FirstNonEmpty(
            appExecutable?.ResolvedTarget,
            entry.ExecutablePath);
        string normalizedExecutable = WindowIdentityExtractor.NormalizePath(effectiveExecutable);
        if (normalizedExecutable.Length == 0)
        {
            errors.Add(Error(
                RestorePlanIssueCode.MissingExecutable,
                "The saved entry has no executable path or alternate launch identity."));
            return Blocked(errors, warnings, "No executable launch target is available.");
        }

        if (RestorePlannerPolicies.IsApplicationRunning(
                entry,
                runningApplications,
                effectiveExecutable))
        {
            warnings.Add(Warning(
                RestorePlanIssueCode.RunningApplicationHasNoRestorableWindow,
                "The application process is running, but it exposes no unassigned user-facing task window for this entry."));
            return NoRestorableWindow(
                "Skip this entry instead of relaunching or waiting for a background-only, tray-only, or already-assigned process.",
                warnings,
                errors);
        }

        if (pendingDocumentExecutables.Contains(normalizedExecutable))
        {
            return AwaitRunningApplication(
                entryIndex,
                placement,
                "A pending resource action for the same executable is expected to start the application.",
                warnings,
                errors);
        }

        RestoreResourceObservation? packagedIdentity = GetResource(
            resources,
            entryIndex,
            RestoreResourceKind.PackagedApplication);
        string packagedAumid = FirstNonEmpty(
            packagedIdentity?.ResolvedTarget,
            RestorePlannerPolicies.IsStoreApp(entry) ? entry.AppUserModelId : "");
        if (packagedIdentity?.Availability == RestoreResourceAvailability.Available ||
            packagedAumid.Length > 0)
        {
            RestoreResourceObservation? versionedExecutable = GetResource(
                resources,
                entryIndex,
                RestoreResourceKind.Executable);
            if (versionedExecutable?.Availability is RestoreResourceAvailability.Missing or
                RestoreResourceAvailability.Stale)
            {
                warnings.Add(Warning(
                    RestorePlanIssueCode.StaleResource,
                    "The versioned package executable changed; the stable package identity will be used."));
            }
            return Launch(
                entryIndex,
                RestoreLaunchKind.PackagedApplication,
                RestoreActionKind.ActivatePackagedApplication,
                "explorer.exe",
                $"shell:AppsFolder\\{packagedAumid}",
                useShellExecute: true,
                RestoreResourceAvailability.Available,
                "Activate the packaged application through its AppUserModelID.",
                LogSensitivity.Path,
                LogSensitivity.Identifier,
                warnings,
                errors);
        }

        if (IsUnavailable(appExecutable, errors))
            return Blocked(errors, warnings, "The application executable is unavailable.");
        AddUnknownAvailabilityWarning(warnings, appExecutable);
        string arguments = RestorePlannerPolicies.IsBrowserProcess(entry.ProcessName) ? "--restore-last-session" : "";
        return Launch(
            entryIndex,
            RestoreLaunchKind.Application,
            RestoreActionKind.LaunchApplication,
            FirstNonEmpty(appExecutable?.ResolvedTarget, entry.ExecutablePath),
            arguments,
            useShellExecute: !RestorePlannerPolicies.IsBrowserProcess(entry.ProcessName),
            appExecutable?.Availability ?? RestoreResourceAvailability.Unknown,
            RestorePlannerPolicies.IsBrowserProcess(entry.ProcessName)
                ? "Launch the browser with its session-restore flag."
                : "Launch the saved application.",
            LogSensitivity.Path,
            LogSensitivity.CommandLine,
            warnings,
            errors);
    }

    private static RestoreLaunchDecision Launch(
        int entryIndex,
        RestoreLaunchKind launchKind,
        RestoreActionKind actionKind,
        string target,
        string arguments,
        bool useShellExecute,
        RestoreResourceAvailability availability,
        string explanation,
        LogSensitivity targetSensitivity,
        LogSensitivity argumentsSensitivity,
        IReadOnlyList<RestorePlanIssue> warnings,
        IReadOnlyList<RestorePlanIssue> errors)
    {
        var requirement = new RestoreLaunchRequirement(
            true,
            launchKind,
            target,
            arguments,
            useShellExecute,
            availability,
            explanation,
            targetSensitivity,
            argumentsSensitivity);
        var action = new RestoreAction(
            entryIndex,
            actionKind,
            WindowHandle: null,
            target,
            arguments,
            useShellExecute,
            TargetPlacement: null,
            explanation,
            targetSensitivity,
            argumentsSensitivity);
        return new RestoreLaunchDecision(
            requirement,
            [action],
            false,
            false,
            false,
            warnings.ToArray(),
            errors.ToArray());
    }

    private static RestoreLaunchDecision AwaitRunningApplication(
        int entryIndex,
        RestoreTargetPlacement placement,
        string explanation,
        IReadOnlyList<RestorePlanIssue> warnings,
        IReadOnlyList<RestorePlanIssue> errors) => new(
            RestoreLaunchRequirement.None(explanation),
            [new RestoreAction(
                 entryIndex,
                 RestoreActionKind.AwaitWindowAppearance,
                 WindowHandle: null,
                 Target: "",
                 Arguments: "",
                 UseShellExecute: false,
                 placement,
                 explanation)],
            AwaitingBrowserSession: false,
            AwaitingRunningApplication: true,
            NoRestorableWindow: false,
            warnings.ToArray(),
            errors.ToArray());

    private static RestoreLaunchDecision NoRestorableWindow(
        string explanation,
        IReadOnlyList<RestorePlanIssue> warnings,
        IReadOnlyList<RestorePlanIssue> errors) => new(
            RestoreLaunchRequirement.None(explanation),
            Array.Empty<RestoreAction>(),
            AwaitingBrowserSession: false,
            AwaitingRunningApplication: false,
            NoRestorableWindow: true,
            warnings.ToArray(),
            errors.ToArray());

    private static RestoreLaunchDecision Blocked(
        IReadOnlyList<RestorePlanIssue> errors,
        IReadOnlyList<RestorePlanIssue> warnings,
        string explanation) => new(
            RestoreLaunchRequirement.None(explanation),
            Array.Empty<RestoreAction>(),
            false,
            false,
            false,
            warnings.ToArray(),
            errors.ToArray());

    private static bool IsUnavailable(
        RestoreResourceObservation? resource,
        ICollection<RestorePlanIssue> errors)
    {
        if (resource?.Availability == RestoreResourceAvailability.Missing)
        {
            errors.Add(Error(
                RestorePlanIssueCode.MissingResource,
                "A required launch resource was observed as missing."));
            return true;
        }
        if (resource?.Availability == RestoreResourceAvailability.Stale)
        {
            errors.Add(Error(
                RestorePlanIssueCode.StaleResource,
                "A required launch resource was observed as stale."));
            return true;
        }
        return false;
    }

    private static void AddUnknownAvailabilityWarning(
        ICollection<RestorePlanIssue> warnings,
        RestoreResourceObservation? resource = null)
    {
        if (resource is null || resource.Availability == RestoreResourceAvailability.Unknown)
        {
            warnings.Add(Warning(
                RestorePlanIssueCode.ResourceAvailabilityUnknown,
                "The launch target was not probed before planning; the executor must validate it before use."));
        }
    }
    private static RestoreResourceObservation? GetResource(
        IReadOnlyDictionary<(int EntryIndex, RestoreResourceKind Kind), RestoreResourceObservation> resources,
        int entryIndex,
        RestoreResourceKind kind) =>
        resources.TryGetValue((entryIndex, kind), out RestoreResourceObservation? resource)
            ? resource
            : null;

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static RestorePlanIssue Warning(RestorePlanIssueCode code, string explanation) =>
        new(code, RestorePlanIssueSeverity.Warning, explanation);

    private static RestorePlanIssue Error(RestorePlanIssueCode code, string explanation) =>
        new(code, RestorePlanIssueSeverity.BlockingError, explanation);
}

internal sealed record RestoreLaunchDecision(
    RestoreLaunchRequirement Requirement,
    IReadOnlyList<RestoreAction> Actions,
    bool AwaitingBrowserSession,
    bool AwaitingRunningApplication,
    bool NoRestorableWindow,
    IReadOnlyList<RestorePlanIssue> Warnings,
    IReadOnlyList<RestorePlanIssue> BlockingErrors);
