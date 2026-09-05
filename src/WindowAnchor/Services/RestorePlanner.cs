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
        var protectedHwnds = new HashSet<long>();
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

        RunningApplicationIdentity[] runningApplications = liveWindows
            .Where(window => !window.IsWebApp && !window.IsDedicatedBrowserWindow)
            .Select(window => new RunningApplicationIdentity(
                window.ExecutablePath,
                window.ProcessName,
                window.AppUserModelId))
            .Concat(liveInventory.RunningApplications)
            .ToArray();
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
            RestoreResourceObservation? observedPackage = GetResource(
                resources,
                entryIndex,
                RestoreResourceKind.PackagedApplication);
            if (observedPackage is
                {
                    Availability: RestoreResourceAvailability.Available,
                    ResolvedTarget.Length: > 0
                })
            {
                savedIdentity = savedIdentity with
                {
                    AppUserModelId = observedPackage.ResolvedTarget,
                    PackageFamilyName = WindowIdentityExtractor.PackageFamily(
                        observedPackage.ResolvedTarget)
                };
            }
            var warnings = new List<RestorePlanIssue>();
            var blockingErrors = new List<RestorePlanIssue>();
            RestoreTargetPlacement placement = BuildPlacement(
                entry,
                snapshot.Monitors,
                monitors,
                monitorTopology.IsExactMatch,
                warnings);

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

            WindowMatchHint? learnedHint = liveInventory.MatchHints.FirstOrDefault(hint =>
                hint.WorkspaceId.Equals(snapshot.WorkspaceId, StringComparison.OrdinalIgnoreCase) &&
                hint.EntryId.Equals(entry.EntryId, StringComparison.OrdinalIgnoreCase));
            WindowMatchResolution matchResolution = WindowMatcher.Resolve(
                savedIdentity,
                liveWindows.Where(window => !consumedHwnds.Contains(window.Hwnd)),
                learnedHint?.Identity);
            RestorePlanCandidate[] candidates = ToPlanCandidates(matchResolution.Candidates);
            WindowMatchCandidate? selectedMatch = matchResolution.SelectedCandidate;
            RestorePlanCandidate? selected = selectedMatch is null
                ? null
                : candidates.Single(candidate => candidate.WindowHandle == selectedMatch.Hwnd.ToInt64());
            var entryActions = new List<RestoreAction>();

            if (matchResolution.IsAmbiguous)
            {
                RestorePlanCandidate[] ambiguousCandidates = candidates
                    .Where(candidate => candidate.IsWithinAmbiguityMargin)
                    .ToArray();
                foreach (RestorePlanCandidate candidate in ambiguousCandidates)
                    protectedHwnds.Add(candidate.WindowHandle);
                warnings.Add(Warning(
                    RestorePlanIssueCode.AmbiguousMatch,
                    matchResolution.Explanation +
                    " Choose a candidate in Restore Preview or skip this entry."));
                planEntries.Add(CreateEntry(
                    entryIndex,
                    entry,
                    RestorePlanEntryOutcome.Blocked,
                    "No live window was assigned because multiple candidates are too close.",
                    savedIdentity,
                    candidates,
                    selected: null,
                    placement,
                    RestoreLaunchRequirement.None(
                        "Launch is suppressed until the ambiguity is explicitly resolved."),
                    Array.Empty<RestoreAction>(),
                    warnings,
                    blockingErrors));
                continue;
            }

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
                runningApplications,
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
                explanation = matchResolution.Explanation;
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
            else if (launch.NoRestorableWindow)
            {
                outcome = RestorePlanEntryOutcome.Excluded;
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
            ProtectedWindowHandles = protectedHwnds,
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
                    Warnings = entry.Warnings
                        .Where(issue => issue.Code != RestorePlanIssueCode.AmbiguousMatch)
                        .ToArray(),
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

    /// <summary>
    /// Returns a new plan with one ambiguous entry assigned to the candidate explicitly selected
    /// by the user. No matching or external observation is repeated.
    /// </summary>
    public static RestorePlan ResolveAmbiguousMatch(
        RestorePlan preview,
        int entryIndex,
        long windowHandle)
    {
        ArgumentNullException.ThrowIfNull(preview);
        RestorePlanEntry entry = preview.Entries.SingleOrDefault(item =>
                item.EntryIndex == entryIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(entryIndex));
        bool isUnresolvedAmbiguity = entry.SelectedMatch is null && entry.Warnings.Any(issue =>
            issue.Code == RestorePlanIssueCode.AmbiguousMatch);
        bool isUserResolvedAmbiguity = entry.SelectedMatch?.IsUserSelected == true;
        if (!isUnresolvedAmbiguity && !isUserResolvedAmbiguity)
        {
            throw new InvalidOperationException(
                "Only an ambiguous entry or a prior user-selected match can accept a candidate selection.");
        }
        RestorePlanCandidate candidate = entry.Candidates.SingleOrDefault(item =>
                item.WindowHandle == windowHandle && item.IsEligible)
            ?? throw new ArgumentException(
                "The requested window is not an eligible candidate for this entry.",
                nameof(windowHandle));
        bool ownedByAnotherEntry = preview.Entries.Any(item =>
            item.EntryIndex != entryIndex &&
            item.SelectedMatch?.WindowHandle == windowHandle &&
            !preview.DisabledEntryIndexes.Contains(item.EntryIndex));
        if (ownedByAnotherEntry)
        {
            throw new InvalidOperationException(
                "The selected window is already assigned to another workspace entry.");
        }

        WindowMatchEvidence selectionEvidence = new(
            WindowMatchEvidenceKind.UserSelectedCandidate,
            true,
            0,
            "The user explicitly selected this candidate in Restore Preview.");
        RestorePlanCandidate selected = candidate with
        {
            Confidence = candidate.Confidence == WindowMatchConfidence.Exact
                ? WindowMatchConfidence.Exact
                : WindowMatchConfidence.Strong,
            Evidence = candidate.Evidence.Append(selectionEvidence).ToArray(),
            IsUserSelected = true
        };
        RestoreAction placementAction = new(
            entryIndex,
            RestoreActionKind.RestoreExistingWindow,
            selected.WindowHandle,
            "",
            "",
            false,
            entry.TargetPlacement,
            "Assign the candidate explicitly selected in Restore Preview and apply its target placement.");
        RestorePlanEntry resolvedEntry = entry with
        {
            Outcome = RestorePlanEntryOutcome.Matched,
            Explanation = "The user resolved the ambiguous match by selecting a specific live window.",
            SelectedMatch = selected,
            LaunchRequirement = RestoreLaunchRequirement.None(
                "The selected live window is the user-confirmed target for this entry."),
            Actions = [placementAction],
            Warnings = entry.Warnings
                .Where(issue => issue.Code != RestorePlanIssueCode.AmbiguousMatch)
                .ToArray()
        };
        RestorePlanEntry[] entries = preview.Entries
            .Select(item => item.EntryIndex == entryIndex ? resolvedEntry : item)
            .ToArray();
        var actions = preview.Actions
            .Where(action => action.EntryIndex != entryIndex)
            .ToList();
        int terminalMinimize = actions.FindIndex(action =>
            action.Kind == RestoreActionKind.MinimizeOtherWindows);
        if (terminalMinimize >= 0)
            actions.Insert(terminalMinimize, placementAction);
        else
            actions.Add(placementAction);

        var currentEntryHandles = entry.Candidates
            .Where(item => item.IsWithinAmbiguityMargin)
            .Select(item => item.WindowHandle)
            .ToHashSet();
        HashSet<long> protectedHandles = preview.ProtectedWindowHandles
            .Where(handle => !currentEntryHandles.Contains(handle))
            .Concat(entries
                .Where(item => item.EntryIndex != entryIndex &&
                    item.Warnings.Any(issue => issue.Code == RestorePlanIssueCode.AmbiguousMatch))
                .SelectMany(item => item.Candidates)
                .Where(item => item.IsWithinAmbiguityMargin)
                .Select(item => item.WindowHandle))
            .ToHashSet();
        RestorePlanIssue[] entryWarnings = entries.SelectMany(item => item.Warnings).ToArray();
        List<RestorePlanIssue> globalWarnings = preview.Warnings.ToList();
        foreach (RestorePlanIssue warning in preview.Entries.SelectMany(item => item.Warnings))
            globalWarnings.Remove(warning);

        return preview with
        {
            Entries = entries,
            Actions = actions.ToArray(),
            Warnings = globalWarnings.Concat(entryWarnings).ToArray(),
            ProtectedWindowHandles = protectedHandles
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
        IReadOnlyList<RunningApplicationIdentity> runningApplications,
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

        if (IsApplicationRunning(entry, runningApplications))
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
            IsStoreApp(entry) ? entry.AppUserModelId : "");
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
            NoRestorableWindow: false,
            warnings.ToArray(),
            errors.ToArray());

    private static LaunchDecision NoRestorableWindow(
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

    private static LaunchDecision Blocked(
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

    private static RestoreTargetPlacement BuildPlacement(
        WorkspaceEntry entry,
        IReadOnlyList<MonitorInfo> savedMonitors,
        IReadOnlyList<RestoreMonitor> monitors,
        bool topologyIsExact,
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
        bool dpiChanged = savedDpi != targetDpi;
        var rectangle = new PlacementRectangle(
            position.NormalLeft,
            position.NormalTop,
            position.NormalRight,
            position.NormalBottom);
        RestorePlacementStrategy strategy = RestorePlacementStrategy.Unavailable;
        WindowLayoutKind semanticKind = position.NormalizedLayout?.Kind ?? WindowLayoutKind.Custom;
        bool wasClamped = false;

        bool useExactPixels = target is not null &&
            topologyIsExact &&
            mapping == RestoreMonitorMappingKind.ExactStableId;
        if (useExactPixels)
        {
            strategy = RestorePlacementStrategy.ExactPixels;
        }
        else if (target is not null)
        {
            MonitorInfo? sourceMonitor = savedMonitors.FirstOrDefault(monitor =>
                savedMonitorId.Length > 0 &&
                string.Equals(monitor.MonitorId, savedMonitorId, StringComparison.OrdinalIgnoreCase))
                ?? savedMonitors.FirstOrDefault(monitor => monitor.Index == savedMonitorIndex);
            NormalizedWindowLayout? layout = WindowLayoutGeometry.IsValid(position.NormalizedLayout)
                ? position.NormalizedLayout
                : sourceMonitor is not null
                    ? WindowLayoutGeometry.Capture(position, sourceMonitor)
                    : null;

            if (WindowLayoutGeometry.IsValid(layout))
            {
                rectangle = AdaptNormalized(layout!, target);
                semanticKind = layout!.Kind;
                strategy = layout!.Kind == WindowLayoutKind.Custom
                    ? RestorePlacementStrategy.Normalized
                    : RestorePlacementStrategy.Semantic;
            }
            else
            {
                double scale = (double)targetDpi / savedDpi;
                rectangle = new PlacementRectangle(
                    Scale(position.NormalLeft, scale),
                    Scale(position.NormalTop, scale),
                    Scale(position.NormalRight, scale),
                    Scale(position.NormalBottom, scale));
                strategy = RestorePlacementStrategy.LegacyDpiScaledAndClamped;
            }

            rectangle = ClampToWorkArea(rectangle, target, out wasClamped);
            if (wasClamped)
            {
                warnings.Add(Warning(
                    RestorePlanIssueCode.PlacementClamped,
                    "The adapted placement was clamped to the visible monitor work area."));
            }
        }

        int left = rectangle.Left;
        int top = rectangle.Top;
        int right = rectangle.Right;
        int bottom = rectangle.Bottom;
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
            dpiChanged,
            strategy,
            semanticKind,
            wasClamped);
    }

    private static PlacementRectangle AdaptNormalized(
        NormalizedWindowLayout layout,
        RestoreMonitor target)
    {
        (double x, double y, double width, double height) = layout.Kind switch
        {
            WindowLayoutKind.Full => (0, 0, 1, 1),
            WindowLayoutKind.LeftHalf => (0, 0, .5, 1),
            WindowLayoutKind.RightHalf => (.5, 0, .5, 1),
            WindowLayoutKind.TopHalf => (0, 0, 1, .5),
            WindowLayoutKind.BottomHalf => (0, .5, 1, .5),
            WindowLayoutKind.LeftThird => (0, 0, 1d / 3d, 1),
            WindowLayoutKind.CenterThird => (1d / 3d, 0, 1d / 3d, 1),
            WindowLayoutKind.RightThird => (2d / 3d, 0, 1d / 3d, 1),
            WindowLayoutKind.Centered => (
                .5 - layout.Width / 2,
                .5 - layout.Height / 2,
                layout.Width,
                layout.Height),
            _ => (layout.X, layout.Y, layout.Width, layout.Height)
        };

        int workWidth = Math.Max(1, target.WorkAreaWidth);
        int workHeight = Math.Max(1, target.WorkAreaHeight);
        int left = target.EffectiveWorkAreaLeft + Scale(workWidth, x);
        int top = target.EffectiveWorkAreaTop + Scale(workHeight, y);
        int windowWidth = Math.Max(1, Scale(workWidth, width));
        int windowHeight = Math.Max(1, Scale(workHeight, height));
        return new PlacementRectangle(left, top, left + windowWidth, top + windowHeight);
    }

    private static PlacementRectangle ClampToWorkArea(
        PlacementRectangle rectangle,
        RestoreMonitor target,
        out bool wasClamped)
    {
        int workLeft = target.EffectiveWorkAreaLeft;
        int workTop = target.EffectiveWorkAreaTop;
        int workRight = target.EffectiveWorkAreaRight;
        int workBottom = target.EffectiveWorkAreaBottom;
        int workWidth = Math.Max(1, workRight - workLeft);
        int workHeight = Math.Max(1, workBottom - workTop);

        int width = rectangle.Right - rectangle.Left;
        int height = rectangle.Bottom - rectangle.Top;
        if (width <= 0) width = Math.Min(800, workWidth);
        if (height <= 0) height = Math.Min(600, workHeight);
        width = Math.Clamp(width, 1, workWidth);
        height = Math.Clamp(height, 1, workHeight);
        int left = Math.Clamp(rectangle.Left, workLeft, workRight - width);
        int top = Math.Clamp(rectangle.Top, workTop, workBottom - height);
        var clamped = new PlacementRectangle(left, top, left + width, top + height);
        wasClamped = clamped != rectangle;
        return clamped;
    }

    private static int Scale(int value, double scale) =>
        (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);

    private readonly record struct PlacementRectangle(int Left, int Top, int Right, int Bottom);

    private static bool IncludedByMode(
        WorkspaceEntry entry,
        RestoreModeKind mode,
        IReadOnlySet<string> selectedMonitorIds) =>
        mode != RestoreModeKind.Selective ||
        selectedMonitorIds.Contains(FirstNonEmpty(entry.MonitorId, entry.Position?.MonitorId));

    private static RestorePlanCandidate[] ToPlanCandidates(
        IReadOnlyList<WindowMatchCandidate> candidates) => candidates
        .Select(candidate => new RestorePlanCandidate(
            candidate.Hwnd.ToInt64(),
            candidate.ProcessId,
            candidate.IsEligible,
            candidate.Score,
            candidate.Confidence,
            candidate.Evidence.ToArray(),
            candidate.TitleSimilarityScore,
            candidate.IsTopScoreTie,
            candidate.Title,
            candidate.ProcessName,
            candidate.WindowClassName,
            candidate.MonitorId,
            candidate.Bounds,
            candidate.IdentityHint,
            candidate.IsWithinAmbiguityMargin,
            candidate.IsLearnedHintMatch,
            IsUserSelected: false,
            CanRememberChoice: candidate.IsEligible && candidates.Count(other =>
                WindowMatcher.MatchesHint(candidate.IdentityHint, other.IdentityHint)) == 1))
        .ToArray();

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

    private static string NormalizeProcessName(string? processName)
    {
        string value = (processName ?? "").Trim();
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
    }

    private static bool IsApplicationRunning(
        WorkspaceEntry entry,
        IEnumerable<RunningApplicationIdentity> runningApplications)
    {
        string expectedPath = WindowIdentityExtractor.NormalizePath(entry.ExecutablePath);
        string expectedProcess = NormalizeProcessName(entry.ProcessName);
        string expectedAumid = entry.AppUserModelId?.Trim() ?? "";
        return runningApplications.Any(application =>
        {
            string observedPath = WindowIdentityExtractor.NormalizePath(application.ExecutablePath);
            string observedProcess = NormalizeProcessName(application.ProcessName);
            string observedAumid = application.AppUserModelId?.Trim() ?? "";
            if (expectedAumid.Length > 0 && observedAumid.Length > 0 &&
                string.Equals(expectedAumid, observedAumid, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (expectedPath.Length > 0 && observedPath.Length > 0)
                return string.Equals(expectedPath, observedPath, StringComparison.OrdinalIgnoreCase);

            // Process names are deliberately a fallback only when one side lacks a usable path;
            // equal filenames from two different installations are not sufficient identity.
            return expectedProcess.Length > 0 && observedProcess.Length > 0 &&
                   string.Equals(expectedProcess, observedProcess, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static RestorePlanIssue Warning(RestorePlanIssueCode code, string explanation) =>
        new(code, RestorePlanIssueSeverity.Warning, explanation);

    private static RestorePlanIssue Error(RestorePlanIssueCode code, string explanation) =>
        new(code, RestorePlanIssueSeverity.BlockingError, explanation);

    private sealed record LaunchDecision(
        RestoreLaunchRequirement Requirement,
        IReadOnlyList<RestoreAction> Actions,
        bool AwaitingBrowserSession,
        bool AwaitingRunningApplication,
        bool NoRestorableWindow,
        IReadOnlyList<RestorePlanIssue> Warnings,
        IReadOnlyList<RestorePlanIssue> BlockingErrors);
}
