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
        var assignmentPlanner = new RestoreAssignmentPlanner(
            snapshot.WorkspaceId,
            liveWindows,
            liveInventory.MatchHints);
        ResolvedEntryRestorePolicy[] entryPolicies = savedEntries
            .Select(entry => RestorePolicyResolver.Resolve(mode.Kind, entry.RestorePolicy))
            .ToArray();
        var planEntries = new List<RestorePlanEntry>(savedEntries.Length);
        var actions = new List<RestoreAction>();
        var globalWarnings = new List<RestorePlanIssue>();
        var policyProtectedWindowHandles = new HashSet<long>();

        if (mode.CancellationRequested)
        {
            globalWarnings.Add(Warning(
                RestorePlanIssueCode.CancellationRequested,
                "Restore planning observed cancellation; no executable actions were produced."));
        }

        bool hasBrowserSessions = snapshot.BrowserSessions.Count > 0;
        bool browserSessionPermitted = savedEntries
            .Select((entry, index) => (Entry: entry, Policy: entryPolicies[index]))
            .Where(item => IncludedByMode(item.Entry, mode.Kind, selectedMonitorSet))
            .Where(item => RestorePlannerPolicies.IsBrowserProcess(item.Entry.ProcessName) &&
                !item.Entry.IsWebApp && !item.Entry.IsDedicatedBrowserWindow)
            .All(item => item.Policy.LaunchIfMissing && !item.Policy.PreferFreshInstance);
        bool browserSessionScheduled = !mode.CancellationRequested &&
            hasBrowserSessions &&
            browserSessionPermitted &&
            liveInventory.BrowserSessionRestore == BrowserSessionRestoreAvailability.Available;
        if (!mode.CancellationRequested && hasBrowserSessions && browserSessionPermitted)
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
            .Where(item => entryPolicies[item.Index].LaunchIfMissing)
            .Where(item => !string.IsNullOrWhiteSpace(item.Entry.LaunchArg))
            .Select(item => WindowIdentityExtractor.NormalizePath(
                FirstNonEmpty(
                    GetResource(resources, item.Index, RestoreResourceKind.Executable)?.ResolvedTarget,
                    item.Entry.ExecutablePath)))
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
            ResolvedEntryRestorePolicy policy = entryPolicies[entryIndex];
            SavedWindowIdentity savedIdentity = WindowIdentityExtractor.FromSaved(entry);
            var warnings = new List<RestorePlanIssue>();
            var blockingErrors = new List<RestorePlanIssue>();
            RestoreResourceObservation? observedExecutable = GetResource(
                resources,
                entryIndex,
                RestoreResourceKind.Executable);
            if (observedExecutable is
                {
                    Availability: RestoreResourceAvailability.Available,
                    ResolvedTarget.Length: > 0
                } &&
                !string.Equals(
                    entry.ExecutablePath,
                    observedExecutable.ResolvedTarget,
                    StringComparison.OrdinalIgnoreCase))
            {
                savedIdentity = savedIdentity with
                {
                    ExecutablePath = WindowIdentityExtractor.NormalizePath(
                        observedExecutable.ResolvedTarget)
                };
                warnings.Add(Warning(
                    RestorePlanIssueCode.UpdatedExecutablePath,
                    "The saved executable version is no longer installed; the newest compatible " +
                    "Squirrel app-version directory will be used for matching and launch."));
            }
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
            RestoreTargetPlacement placement = RestorePlacementGeometry.Build(
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
                    blockingErrors,
                    policy));
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
                    blockingErrors,
                    policy));
                continue;
            }

            RestoreAssignmentResult assignment = assignmentPlanner.Resolve(entry, savedIdentity);
            WindowMatchResolution matchResolution = assignment.Resolution;
            IReadOnlyList<RestorePlanCandidate> candidates = assignment.Candidates;
            WindowMatchCandidate? selectedMatch = assignment.SelectedMatch;
            RestorePlanCandidate? selected = assignment.SelectedPlanCandidate;
            var entryActions = new List<RestoreAction>();
            long[] preexistingCandidates = candidates
                .Where(candidate => candidate.IsEligible)
                .Select(candidate => candidate.WindowHandle)
                .ToArray();

            if (policy.NeverClose)
                policyProtectedWindowHandles.UnionWith(preexistingCandidates);

            if (policy.IgnoreDuringSwitch && RestorePolicyResolver.IsSwitch(mode.Kind))
            {
                planEntries.Add(CreateEntry(
                    entryIndex,
                    entry,
                    RestorePlanEntryOutcome.Excluded,
                    "The entry is ignored during exact switches; matching windows are preserved without being moved.",
                    savedIdentity,
                    candidates,
                    selected,
                    placement,
                    RestoreLaunchRequirement.None("Switch policy suppresses restore and launch actions."),
                    Array.Empty<RestoreAction>(),
                    warnings,
                    blockingErrors,
                    policy));
                continue;
            }

            bool supportsFreshLaunch = SupportsFreshLaunch(entry);
            bool useFreshLaunch = policy.PreferFreshInstance && supportsFreshLaunch;
            if (policy.PreferFreshInstance && !supportsFreshLaunch &&
                preexistingCandidates.Length > 0)
            {
                warnings.Add(Warning(
                    RestorePlanIssueCode.UnsupportedAlwaysLaunchNew,
                    "This entry has no launch contract that guarantees a distinct window; " +
                    "the safest existing match will be reused."));
            }

            if (matchResolution.IsAmbiguous && !useFreshLaunch)
            {
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
                    blockingErrors,
                    policy));
                continue;
            }

            IReadOnlySet<long> readinessExcludedWindowHandles = new HashSet<long>();
            if (useFreshLaunch)
            {
                readinessExcludedWindowHandles = preexistingCandidates.ToHashSet();
                selectedMatch = null;
                selected = null;
            }

            bool placementNeeded = selectedMatch is not null &&
                (!policy.RepairOnly || PlacementNeedsRepair(selected!, placement));
            if (placementNeeded)
            {
                entryActions.Add(new RestoreAction(
                    entryIndex,
                    RestoreActionKind.RestoreExistingWindow,
                    selectedMatch!.Hwnd.ToInt64(),
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
            RestoreLaunchDecision launch = policy.LaunchIfMissing
                ? RestoreLaunchPlanner.Plan(
                    entryIndex,
                    entry,
                    selectedMatch is not null,
                    correctResourceMatched,
                    browserSessionScheduled,
                    runningApplications,
                    pendingDocumentExecutables,
                    resources,
                    placement)
                : new RestoreLaunchDecision(
                    RestoreLaunchRequirement.None("Launch is disabled by the resolved entry policy."),
                    Array.Empty<RestoreAction>(),
                    AwaitingBrowserSession: false,
                    AwaitingRunningApplication: false,
                    NoRestorableWindow: false,
                    Array.Empty<RestorePlanIssue>(),
                    Array.Empty<RestorePlanIssue>());

            if (selectedMatch is null && !policy.LaunchIfMissing)
            {
                planEntries.Add(CreateEntry(
                    entryIndex,
                    entry,
                    RestorePlanEntryOutcome.Excluded,
                    "No eligible live window exists and the resolved restore policy forbids launching it.",
                    savedIdentity,
                    candidates,
                    selected,
                    placement,
                    RestoreLaunchRequirement.None("Launch is disabled by the resolved entry policy."),
                    Array.Empty<RestoreAction>(),
                    warnings,
                    blockingErrors,
                    policy,
                    readinessExcludedWindowHandles));
                continue;
            }

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
                explanation = policy.RepairOnly && !placementNeeded
                    ? "The existing window already matches the requested placement; no repair is needed."
                    : matchResolution.Explanation;
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
                blockingErrors,
                policy,
                readinessExcludedWindowHandles));
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
            ProtectedWindowHandles = assignmentPlanner.ProtectedWindowHandles
                .Concat(policyProtectedWindowHandles)
                .ToHashSet(),
            WasCancelled = mode.CancellationRequested,
            Entries = planEntries.ToArray(),
            Actions = actions.ToArray(),
            Warnings = globalWarnings.Concat(entryWarnings).ToArray(),
            BlockingErrors = entryErrors
        };
    }

    /// <summary>
    /// Derives an approved plan from an immutable preview without observing the environment or
    /// recomputing any match.
    /// </summary>
    public static RestorePlan DeriveApprovedPlan(
        RestorePlan preview,
        IEnumerable<int> disabledEntryIndexes) =>
        RestoreApprovalProjector.DeriveApprovedPlan(preview, disabledEntryIndexes);

    /// <summary>Applies one explicit ambiguity choice without repeating observation or scoring.</summary>
    public static RestorePlan ResolveAmbiguousMatch(
        RestorePlan preview,
        int entryIndex,
        long windowHandle) =>
        RestoreApprovalProjector.ResolveAmbiguousMatch(preview, entryIndex, windowHandle);

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
        IReadOnlyList<RestorePlanIssue> blockingErrors,
        ResolvedEntryRestorePolicy policy,
        IReadOnlySet<long>? readinessExcludedWindowHandles = null) => new(
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
            blockingErrors.ToArray())
        {
            RestorePolicy = policy,
            ReadinessExcludedWindowHandles = readinessExcludedWindowHandles?.ToHashSet() ??
                new HashSet<long>()
        };

    private static bool IncludedByMode(
        WorkspaceEntry entry,
        RestoreModeKind mode,
        IReadOnlySet<string> selectedMonitorIds) =>
        mode != RestoreModeKind.Selective ||
        selectedMonitorIds.Contains(FirstNonEmpty(entry.MonitorId, entry.Position?.MonitorId));

    private static bool SupportsFreshLaunch(WorkspaceEntry entry) =>
        entry.IsDedicatedBrowserWindow && !string.IsNullOrWhiteSpace(entry.BrowserUrl);

    private static bool PlacementNeedsRepair(
        RestorePlanCandidate current,
        RestoreTargetPlacement target)
    {
        if (current.ShowCmd != target.ShowCmd ||
            !current.Bounds.IsValid ||
            target.Strategy != RestorePlacementStrategy.ExactPixels)
        {
            return true;
        }

        const int tolerance = 2;
        return Math.Abs(current.Bounds.Left - target.Left) > tolerance ||
               Math.Abs(current.Bounds.Top - target.Top) > tolerance ||
               Math.Abs(current.Bounds.Right - target.Right) > tolerance ||
               Math.Abs(current.Bounds.Bottom - target.Bottom) > tolerance;
    }

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

    private static RestorePlanIssue Warning(RestorePlanIssueCode code, string explanation) =>
        new(code, RestorePlanIssueSeverity.Warning, explanation);
}
