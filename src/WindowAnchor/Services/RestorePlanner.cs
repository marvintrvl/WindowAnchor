using System;
using System.Collections.Generic;
using System.Linq;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Builds deterministic restore intent from saved and already-observed facts. This type performs
/// no persistence, browser, process, file-system, or native-window operations.
/// </summary>
public static class RestorePlanner
{
    /// <summary>Builds an immutable, explained restore plan without executing any action.</summary>
    public static RestorePlan Build(
        WorkspaceSnapshot snapshot,
        RestoreLiveInventory liveInventory,
        RestoreMonitorTopology monitorTopology,
        RestoreMode mode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(liveInventory);
        ArgumentNullException.ThrowIfNull(monitorTopology);
        ArgumentNullException.ThrowIfNull(mode);

        WorkspaceEntry[] savedEntries = snapshot.Entries.ToArray();
        LiveWindowIdentity[] liveWindows = liveInventory.Windows
            .Where(window => window is not null)
            .OrderBy(window => window.Hwnd.ToInt64())
            .ToArray();
        RestoreMonitor[] monitors = monitorTopology.Monitors
            .Where(monitor => monitor is not null)
            .OrderBy(monitor => monitor.MonitorIndex)
            .ThenBy(monitor => monitor.MonitorId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Dictionary<(int EntryIndex, RestoreResourceKind Kind), RestoreResourceObservation>
            resources = IndexResources(liveInventory.Resources);
        string[] selectedMonitorIds = mode.SelectedMonitorIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedMonitorSet = selectedMonitorIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var consumedHwnds = new HashSet<IntPtr>();
        var planEntries = new List<RestorePlanEntry>(savedEntries.Length);
        var actions = new List<RestoreAction>();
        var globalWarnings = new List<RestorePlanIssue>();

        if (mode.CancellationRequested)
        {
            globalWarnings.Add(Warning(
                RestorePlanIssueCode.CancellationRequested,
                "Restore planning observed cancellation; no executable actions were produced."));
        }

        bool hasBrowserSessions = snapshot.BrowserSessions.Count > 0;
        bool browserSessionScheduled = !mode.CancellationRequested &&
            hasBrowserSessions &&
            liveInventory.BrowserSessionRestore == BrowserSessionRestoreAvailability.Available;
        if (!mode.CancellationRequested && hasBrowserSessions)
        {
            if (browserSessionScheduled)
            {
                actions.Add(new RestoreAction(
                    EntryIndex: null,
                    RestoreActionKind.RestoreBrowserSession,
                    WindowHandle: null,
                    Target: snapshot.Name,
                    Arguments: "",
                    UseShellExecute: false,
                    TargetPlacement: null,
                    "Request restoration through the browser-session connector.",
                    LogSensitivity.WorkspaceName));
            }
            else
            {
                globalWarnings.Add(Warning(
                    RestorePlanIssueCode.BrowserSessionUnavailable,
                    "Browser-session restoration is unavailable; browser entries use ordinary matching and launch fallbacks."));
            }
        }

        HashSet<string> runningExecutables = liveWindows
            .Where(window => !window.IsWebApp && !window.IsDedicatedBrowserWindow)
            .Select(window => WindowIdentityExtractor.NormalizePath(window.ExecutablePath))
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> pendingDocumentExecutables = savedEntries
            .Select((entry, index) => (Entry: entry, Index: index))
            .Where(item => IncludedByMode(item.Entry, mode.Kind, selectedMonitorSet))
            .Where(item => !string.IsNullOrWhiteSpace(item.Entry.LaunchArg))
            .Select(item => WindowIdentityExtractor.NormalizePath(item.Entry.ExecutablePath))
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> duplicateEntryIds = savedEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.EntryId))
            .GroupBy(entry => entry.EntryId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int entryIndex = 0; entryIndex < savedEntries.Length; entryIndex++)
        {
            WorkspaceEntry entry = savedEntries[entryIndex];
            SavedWindowIdentity savedIdentity = WindowIdentityExtractor.FromSaved(entry);
            var warnings = new List<RestorePlanIssue>();
            var blockingErrors = new List<RestorePlanIssue>();
            RestoreTargetPlacement placement = BuildPlacement(entry, monitors, warnings);

            if (duplicateEntryIds.Contains(entry.EntryId))
            {
                warnings.Add(Warning(
                    RestorePlanIssueCode.DuplicateEntryId,
                    "Another saved entry has the same stable entry ID; list position is used for deterministic planning."));
            }

            if (mode.CancellationRequested)
            {
                planEntries.Add(CreateEntry(
                    entryIndex,
                    entry,
                    RestorePlanEntryOutcome.Cancelled,
                    "The restore was cancelled before any planned action could execute.",
                    savedIdentity,
                    Array.Empty<RestorePlanCandidate>(),
                    selected: null,
                    placement,
                    RestoreLaunchRequirement.None("No launch is planned after cancellation."),
                    Array.Empty<RestoreAction>(),
                    warnings,
                    blockingErrors));
                continue;
            }

            if (!IncludedByMode(entry, mode.Kind, selectedMonitorSet))
            {
                planEntries.Add(CreateEntry(
                    entryIndex,
                    entry,
                    RestorePlanEntryOutcome.Excluded,
                    "The entry is outside the monitors selected for this restore.",
                    savedIdentity,
                    Array.Empty<RestorePlanCandidate>(),
                    selected: null,
                    placement,
                    RestoreLaunchRequirement.None("Selective restore excludes this entry."),
                    Array.Empty<RestoreAction>(),
                    warnings,
                    blockingErrors));
                continue;
            }

            IReadOnlyList<WindowMatchCandidate> matchedCandidates = WindowMatcher.FindCandidates(
                savedIdentity,
                liveWindows.Where(window => !consumedHwnds.Contains(window.Hwnd)));
            RestorePlanCandidate[] candidates = matchedCandidates
                .Select(ToPlanCandidate)
                .ToArray();
            WindowMatchCandidate? selectedMatch = matchedCandidates
                .FirstOrDefault(candidate => candidate.IsEligible);
            RestorePlanCandidate? selected = selectedMatch is null
                ? null
                : ToPlanCandidate(selectedMatch);
            var entryActions = new List<RestoreAction>();

            if (selectedMatch is not null)
            {
                consumedHwnds.Add(selectedMatch.Hwnd);
                entryActions.Add(new RestoreAction(
                    entryIndex,
                    RestoreActionKind.RestoreExistingWindow,
                    selectedMatch.Hwnd.ToInt64(),
                    "",
                    "",
                    false,
                    placement,
                    "Assign the selected live window and apply the target placement."));

                if (selectedMatch.IsTopScoreTie)
                {
                    warnings.Add(Warning(
                        RestorePlanIssueCode.AmbiguousMatch,
                        "Multiple live windows share the highest score; deterministic HWND ordering selected one candidate."));
                }
            }

            bool correctResourceMatched = selectedMatch?.Evidence.Any(evidence =>
                evidence.Matched && evidence.Kind is
                    WindowMatchEvidenceKind.DocumentNameInTitle or
                    WindowMatchEvidenceKind.PwaIdentityExact or
                    WindowMatchEvidenceKind.DedicatedBrowserSiteExact) == true;
            LaunchDecision launch = PlanLaunch(
                entryIndex,
                entry,
                selectedMatch is not null,
                correctResourceMatched,
                browserSessionScheduled,
                runningExecutables,
                pendingDocumentExecutables,
                resources,
                placement);
            warnings.AddRange(launch.Warnings);
            blockingErrors.AddRange(launch.BlockingErrors);
            entryActions.AddRange(launch.Actions);
            if (selectedMatch is null && launch.Requirement.IsRequired)
            {
                entryActions.Add(new RestoreAction(
                    entryIndex,
                    RestoreActionKind.AwaitWindowAppearance,
                    WindowHandle: null,
                    Target: "",
                    Arguments: "",
                    UseShellExecute: false,
                    placement,
                    "Wait for the launched application to create an eligible window."));
            }

            RestorePlanEntryOutcome outcome;
            string explanation;
            if (blockingErrors.Count > 0)
            {
                // A blocked entry is descriptive only. Do not leave a partially executable
                // placement or launch action attached to it.
                entryActions.Clear();
                outcome = RestorePlanEntryOutcome.Blocked;
                explanation = "One or more required resources are missing or stale.";
            }
            else if (selectedMatch is not null && launch.Requirement.IsRequired)
            {
                outcome = RestorePlanEntryOutcome.MatchedAndLaunchRequired;
                explanation = "A live window can be positioned, but the saved resource must also be opened.";
            }
            else if (selectedMatch is not null)
            {
                outcome = RestorePlanEntryOutcome.Matched;
                explanation = "A live window was selected deterministically from scored identity evidence.";
            }
            else if (launch.AwaitingBrowserSession)
            {
                outcome = RestorePlanEntryOutcome.AwaitingBrowserSession;
                explanation = "The browser-session connector is expected to recreate this window.";
            }
            else if (launch.AwaitingRunningApplication)
            {
                outcome = RestorePlanEntryOutcome.AwaitingRunningApplication;
                explanation = launch.Requirement.Explanation;
            }
            else
            {
                outcome = RestorePlanEntryOutcome.LaunchRequired;
                explanation = "No eligible live window exists, so a launch action is required.";
            }

            planEntries.Add(CreateEntry(
                entryIndex,
                entry,
                outcome,
                explanation,
                savedIdentity,
                candidates,
                selected,
                placement,
                launch.Requirement,
                entryActions,
                warnings,
                blockingErrors));
            actions.AddRange(entryActions);
        }

        if (!mode.CancellationRequested && mode.Kind == RestoreModeKind.AlignAndMinimize)
        {
            actions.Add(new RestoreAction(
                EntryIndex: null,
                RestoreActionKind.MinimizeOtherWindows,
                WindowHandle: null,
                Target: "",
                Arguments: "",
                UseShellExecute: false,
                TargetPlacement: null,
                "After reconciliation, minimize windows outside the final assigned HWND set."));
        }

        RestorePlanIssue[] entryWarnings = planEntries.SelectMany(entry => entry.Warnings).ToArray();
        RestorePlanIssue[] entryErrors = planEntries.SelectMany(entry => entry.BlockingErrors).ToArray();
        return new RestorePlan
        {
            WorkspaceId = snapshot.WorkspaceId,
            WorkspaceName = snapshot.Name,
            SnapshotSavedAt = snapshot.SavedAt,
            Mode = mode.Kind,
            SelectedMonitorIds = selectedMonitorIds,
            BrowserSessions = snapshot.BrowserSessions.Select(ToRestoreBrowserSession).ToArray(),
            WasCancelled = mode.CancellationRequested,
            Entries = planEntries.ToArray(),
            Actions = actions.ToArray(),
            Warnings = globalWarnings.Concat(entryWarnings).ToArray(),
            BlockingErrors = entryErrors
        };
    }

    /// <summary>
    /// Derives an approved plan from an immutable preview without observing the environment or
    /// recomputing any match. Disabled entries retain their preview evidence but contribute no
    /// executable actions or blocking errors.
    /// </summary>
    public static RestorePlan DeriveApprovedPlan(
        RestorePlan preview,
        IEnumerable<int> disabledEntryIndexes)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(disabledEntryIndexes);

        HashSet<int> disabled = preview.DisabledEntryIndexes
            .Concat(disabledEntryIndexes)
            .Where(index => index >= 0 && index < preview.Entries.Count)
            .ToHashSet();
        bool disabledOrdinaryBrowser = preview.Entries.Any(entry =>
            disabled.Contains(entry.EntryIndex) &&
            !entry.SavedIdentity.IsWebApp &&
            !entry.SavedIdentity.IsDedicatedBrowserWindow &&
            !string.IsNullOrWhiteSpace(entry.SavedIdentity.BrowserFamily));

        RestoreAction AdaptAction(RestoreAction action) =>
            disabledOrdinaryBrowser &&
            action.Condition == RestoreActionCondition.BrowserSessionUnavailable
                ? action with
                {
                    Condition = RestoreActionCondition.Always,
                    Explanation = "Launch the browser directly because browser-session " +
                                  "restoration was disabled with a browser entry."
                }
                : action;

        RestorePlanEntry[] entries = preview.Entries.Select(entry =>
        {
            if (disabled.Contains(entry.EntryIndex))
            {
                return entry with
                {
                    Outcome = RestorePlanEntryOutcome.Excluded,
                    Explanation = "The user disabled this entry in the approved preview.",
                    LaunchRequirement = RestoreLaunchRequirement.None(
                        "No launch is approved for a disabled entry."),
                    Actions = Array.Empty<RestoreAction>(),
                    BlockingErrors = Array.Empty<RestorePlanIssue>()
                };
            }

            return entry with { Actions = entry.Actions.Select(AdaptAction).ToArray() };
        }).ToArray();

        bool hasApprovedEntry = entries.Any(entry => entry.Outcome is not (
            RestorePlanEntryOutcome.Excluded or
            RestorePlanEntryOutcome.Cancelled or
            RestorePlanEntryOutcome.Blocked));
        RestoreAction[] actions = preview.Actions
            .Where(action => action.EntryIndex is not int index || !disabled.Contains(index))
            .Where(action => !disabledOrdinaryBrowser ||
                action.Kind != RestoreActionKind.RestoreBrowserSession)
            .Where(action => hasApprovedEntry ||
                action.Kind != RestoreActionKind.MinimizeOtherWindows)
            .Select(AdaptAction)
            .ToArray();
        RestorePlanIssue[] entryWarnings = preview.Entries
            .SelectMany(entry => entry.Warnings)
            .ToArray();
        List<RestorePlanIssue> globalWarnings = preview.Warnings.ToList();
        foreach (RestorePlanIssue warning in entryWarnings)
            globalWarnings.Remove(warning);
        RestorePlanIssue[] approvedWarnings = globalWarnings
            .Concat(entries.SelectMany(entry => entry.Warnings))
            .ToArray();
        RestorePlanIssue[] approvedErrors = entries
            .SelectMany(entry => entry.BlockingErrors)
            .ToArray();
        HashSet<long> protectedHandles = preview.ProtectedWindowHandles
            .Concat(preview.Entries
                .Where(entry => disabled.Contains(entry.EntryIndex))
                .Select(entry => entry.SelectedMatch?.WindowHandle)
                .OfType<long>())
            .ToHashSet();

        return preview with
        {
            DisabledEntryIndexes = disabled,
            ProtectedWindowHandles = protectedHandles,
            BrowserSessions = disabledOrdinaryBrowser
                ? Array.Empty<RestoreBrowserSession>()
                : preview.BrowserSessions.ToArray(),
            Entries = entries,
            Actions = actions,
            Warnings = approvedWarnings,
            BlockingErrors = approvedErrors
        };
    }

    private static RestorePlanEntry CreateEntry(
        int entryIndex,
        WorkspaceEntry entry,
        RestorePlanEntryOutcome outcome,
        string explanation,
        SavedWindowIdentity identity,
        IReadOnlyList<RestorePlanCandidate> candidates,
        RestorePlanCandidate? selected,
        RestoreTargetPlacement placement,
        RestoreLaunchRequirement launch,
        IReadOnlyList<RestoreAction> actions,
        IReadOnlyList<RestorePlanIssue> warnings,
        IReadOnlyList<RestorePlanIssue> blockingErrors) => new(
            entryIndex,
            entry.EntryId,
            outcome,
            explanation,
            identity,
            candidates.ToArray(),
            selected,
            placement,
            launch,
            actions.ToArray(),
            warnings.ToArray(),
            blockingErrors.ToArray());

    private static LaunchDecision PlanLaunch(
        int entryIndex,
        WorkspaceEntry entry,
        bool hasSelectedMatch,
        bool correctResourceMatched,
        bool browserSessionScheduled,
        IReadOnlySet<string> runningExecutables,
        IReadOnlySet<string> pendingDocumentExecutables,
        IReadOnlyDictionary<(int EntryIndex, RestoreResourceKind Kind), RestoreResourceObservation> resources,
        RestoreTargetPlacement placement)
    {
        var warnings = new List<RestorePlanIssue>();
        var errors = new List<RestorePlanIssue>();

        if (hasSelectedMatch && (string.IsNullOrWhiteSpace(entry.LaunchArg) || correctResourceMatched))
        {
            return new LaunchDecision(
                RestoreLaunchRequirement.None("The selected live window already satisfies this entry."),
                Array.Empty<RestoreAction>(),
                false,
                false,
                warnings,
                errors);
        }

        if (!hasSelectedMatch && browserSessionScheduled && IsBrowserProcess(entry.ProcessName))
        {
            var fallbackAction = new RestoreAction(
                entryIndex,
                RestoreActionKind.LaunchApplication,
                WindowHandle: null,
                Target: entry.ExecutablePath,
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
            return new LaunchDecision(
                RestoreLaunchRequirement.None("Browser-session restoration replaces a direct process launch."),
                [fallbackAction, awaitAction],
                AwaitingBrowserSession: true,
                AwaitingRunningApplication: false,
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
                        placement,
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
                    placement,
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
                placement,
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
                placement,
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
                    placement,
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
                placement,
                warnings,
                errors);
        }

        if (hasSelectedMatch)
        {
            return new LaunchDecision(
                RestoreLaunchRequirement.None("The selected live window already satisfies this entry."),
                Array.Empty<RestoreAction>(),
                false,
                false,
                warnings,
                errors);
        }

        string normalizedExecutable = WindowIdentityExtractor.NormalizePath(entry.ExecutablePath);
        if (normalizedExecutable.Length == 0)
        {
            errors.Add(Error(
                RestorePlanIssueCode.MissingExecutable,
                "The saved entry has no executable path or alternate launch identity."));
            return Blocked(errors, warnings, "No executable launch target is available.");
        }

        if (runningExecutables.Contains(normalizedExecutable))
        {
            warnings.Add(Warning(
                RestorePlanIssueCode.RunningApplicationHasNoMatchingWindow,
                "The application is running, but no eligible live window matches this entry."));
            return AwaitRunningApplication(
                entryIndex,
                placement,
                "Do not launch a duplicate process while the saved application is already running.",
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

        if (IsStoreApp(entry))
        {
            return Launch(
                entryIndex,
                RestoreLaunchKind.PackagedApplication,
                RestoreActionKind.ActivatePackagedApplication,
                "explorer.exe",
                $"shell:AppsFolder\\{entry.AppUserModelId}",
                useShellExecute: true,
                RestoreResourceAvailability.Available,
                "Activate the packaged application through its AppUserModelID.",
                LogSensitivity.Path,
                LogSensitivity.Identifier,
                placement,
                warnings,
                errors);
        }

        RestoreResourceObservation? appExecutable = GetResource(
            resources,
            entryIndex,
            RestoreResourceKind.Executable);
        if (IsUnavailable(appExecutable, errors))
            return Blocked(errors, warnings, "The application executable is unavailable.");
        AddUnknownAvailabilityWarning(warnings, appExecutable);
        string arguments = IsBrowserProcess(entry.ProcessName) ? "--restore-last-session" : "";
        return Launch(
            entryIndex,
            RestoreLaunchKind.Application,
            RestoreActionKind.LaunchApplication,
            FirstNonEmpty(appExecutable?.ResolvedTarget, entry.ExecutablePath),
            arguments,
            useShellExecute: !IsBrowserProcess(entry.ProcessName),
            appExecutable?.Availability ?? RestoreResourceAvailability.Unknown,
            IsBrowserProcess(entry.ProcessName)
                ? "Launch the browser with its session-restore flag."
                : "Launch the saved application.",
            LogSensitivity.Path,
            LogSensitivity.CommandLine,
            placement,
            warnings,
            errors);
    }

    private static LaunchDecision Launch(
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
        RestoreTargetPlacement placement,
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
        return new LaunchDecision(
            requirement,
            [action],
            false,
            false,
            warnings.ToArray(),
            errors.ToArray());
    }

    private static LaunchDecision AwaitRunningApplication(
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
            warnings.ToArray(),
            errors.ToArray());

    private static LaunchDecision Blocked(
        IReadOnlyList<RestorePlanIssue> errors,
        IReadOnlyList<RestorePlanIssue> warnings,
        string explanation) => new(
            RestoreLaunchRequirement.None(explanation),
            Array.Empty<RestoreAction>(),
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

    private static RestoreTargetPlacement BuildPlacement(
        WorkspaceEntry entry,
        IReadOnlyList<RestoreMonitor> monitors,
        ICollection<RestorePlanIssue> warnings)
    {
        WindowRecord position = entry.Position ?? new WindowRecord();
        string savedMonitorId = FirstNonEmpty(entry.MonitorId, position.MonitorId);
        int savedMonitorIndex = !string.IsNullOrWhiteSpace(entry.MonitorId)
            ? entry.MonitorIndex
            : position.MonitorIndex;
        RestoreMonitor? target = monitors.FirstOrDefault(monitor =>
            savedMonitorId.Length > 0 &&
            string.Equals(monitor.MonitorId, savedMonitorId, StringComparison.OrdinalIgnoreCase));
        RestoreMonitorMappingKind mapping = RestoreMonitorMappingKind.ExactStableId;

        if (target is null)
        {
            target = monitors.FirstOrDefault(monitor => monitor.MonitorIndex == savedMonitorIndex);
            mapping = target is null
                ? RestoreMonitorMappingKind.PrimaryFallback
                : RestoreMonitorMappingKind.SavedIndexFallback;
        }
        if (target is null)
            target = monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors.FirstOrDefault();
        if (target is null)
            mapping = RestoreMonitorMappingKind.Unavailable;

        if (monitors.Count == 0)
        {
            warnings.Add(Warning(
                RestorePlanIssueCode.MonitorTopologyUnavailable,
                "No current monitor topology was supplied; the saved DPI and coordinates are retained."));
        }
        else if (savedMonitorId.Length > 0 && mapping != RestoreMonitorMappingKind.ExactStableId)
        {
            warnings.Add(Warning(
                RestorePlanIssueCode.SavedMonitorUnavailable,
                "The saved monitor is unavailable; a deterministic topology fallback was selected."));
        }

        uint savedDpi = position.SavedDpi > 0 ? position.SavedDpi : 96;
        uint targetDpi = target is { Dpi: > 0 } ? target.Dpi : savedDpi;
        double scale = (double)targetDpi / savedDpi;
        int left = Scale(position.NormalLeft, scale);
        int top = Scale(position.NormalTop, scale);
        int right = Scale(position.NormalRight, scale);
        int bottom = Scale(position.NormalBottom, scale);
        if (right <= left || bottom <= top)
        {
            warnings.Add(Warning(
                RestorePlanIssueCode.InvalidSavedPlacement,
                "The saved placement has non-positive width or height."));
        }

        return new RestoreTargetPlacement(
            target?.MonitorId ?? savedMonitorId,
            target?.MonitorIndex ?? savedMonitorIndex,
            mapping,
            left,
            top,
            right,
            bottom,
            position.ShowCmd,
            savedDpi,
            targetDpi,
            savedDpi != targetDpi);
    }

    private static int Scale(int coordinate, double scale) => (int)(coordinate * scale);

    private static bool IncludedByMode(
        WorkspaceEntry entry,
        RestoreModeKind mode,
        IReadOnlySet<string> selectedMonitorIds) =>
        mode != RestoreModeKind.Selective ||
        selectedMonitorIds.Contains(FirstNonEmpty(entry.MonitorId, entry.Position?.MonitorId));

    private static RestorePlanCandidate ToPlanCandidate(WindowMatchCandidate candidate) => new(
        candidate.Hwnd.ToInt64(),
        candidate.ProcessId,
        candidate.IsEligible,
        candidate.Score,
        candidate.Confidence,
        candidate.Evidence.ToArray(),
        candidate.TitleSimilarityScore,
        candidate.IsTopScoreTie);

    private static RestoreBrowserSession ToRestoreBrowserSession(BrowserSession session) => new(
        session.Browser,
        session.ActiveTitle,
        session.WindowIndex,
        session.Left,
        session.Top,
        session.Width,
        session.Height,
        session.State,
        session.Tabs.Select(tab => new RestoreBrowserTab(
            tab.Url,
            tab.Title,
            tab.Index,
            tab.Active,
            tab.Pinned,
            tab.GroupIndex)).ToArray(),
        session.Groups.Select(group => new RestoreBrowserTabGroup(
            group.Index,
            group.Title,
            group.Color,
            group.Collapsed)).ToArray());

    private static Dictionary<(int EntryIndex, RestoreResourceKind Kind), RestoreResourceObservation>
        IndexResources(IEnumerable<RestoreResourceObservation> resources)
    {
        var indexed = new Dictionary<
            (int EntryIndex, RestoreResourceKind Kind),
            RestoreResourceObservation>();
        foreach (RestoreResourceObservation resource in resources)
            indexed[(resource.EntryIndex, resource.Kind)] = resource;
        return indexed;
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

    private static bool IsStoreApp(WorkspaceEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.AppUserModelId) &&
        entry.AppUserModelId.Contains('!') &&
        entry.ExecutablePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    private static bool IsBrowserProcess(string? processName) =>
        processName?.ToLowerInvariant() is "chrome" or "msedge" or "opera" or "brave";

    private static RestorePlanIssue Warning(RestorePlanIssueCode code, string explanation) =>
        new(code, RestorePlanIssueSeverity.Warning, explanation);

    private static RestorePlanIssue Error(RestorePlanIssueCode code, string explanation) =>
        new(code, RestorePlanIssueSeverity.BlockingError, explanation);

    private sealed record LaunchDecision(
        RestoreLaunchRequirement Requirement,
        IReadOnlyList<RestoreAction> Actions,
        bool AwaitingBrowserSession,
        bool AwaitingRunningApplication,
        IReadOnlyList<RestorePlanIssue> Warnings,
        IReadOnlyList<RestorePlanIssue> BlockingErrors);
}
