using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class RestorePlanPreviewTests
{
    [Fact]
    public void Preview_projects_exact_adapted_ambiguous_missing_and_destructive_outcomes()
    {
        RestorePlanEntry exact = Entry(
            0,
            RestorePlanEntryOutcome.Matched,
            Placement(),
            Candidate(10, WindowMatchConfidence.Exact),
            actions: [Move(0, 10)]);
        RestorePlanEntry adapted = Entry(
            1,
            RestorePlanEntryOutcome.Matched,
            Placement(RestoreMonitorMappingKind.PrimaryFallback, scaled: true),
            Candidate(11, WindowMatchConfidence.Strong),
            actions: [Move(1, 11)]);
        RestorePlanEntry ambiguous = Entry(
            2,
            RestorePlanEntryOutcome.Matched,
            Placement(),
            Candidate(12, WindowMatchConfidence.Strong),
            actions: [Move(2, 12)],
            warnings:
            [
                new(
                    RestorePlanIssueCode.AmbiguousMatch,
                    RestorePlanIssueSeverity.Warning,
                    "Two candidates have the same score.")
            ]);
        RestorePlanEntry missing = Entry(
            3,
            RestorePlanEntryOutcome.Blocked,
            Placement(),
            selected: null,
            blockingErrors:
            [
                new(
                    RestorePlanIssueCode.MissingResource,
                    RestorePlanIssueSeverity.BlockingError,
                    "The saved document is missing.")
            ]);
        RestoreAction minimize = new(
            null,
            RestoreActionKind.MinimizeOtherWindows,
            null,
            "",
            "",
            false,
            null,
            "Minimize unrelated windows.");
        var plan = new RestorePlan
        {
            WorkspaceName = "Preview fixture",
            Mode = RestoreModeKind.AlignAndMinimize,
            Entries = [exact, adapted, ambiguous, missing],
            Actions = exact.Actions.Concat(adapted.Actions).Concat(ambiguous.Actions).Append(minimize).ToArray(),
            Warnings = ambiguous.Warnings,
            BlockingErrors = missing.BlockingErrors
        };

        RestorePlanPreview preview = RestorePlanPreviewBuilder.Build(plan);

        Assert.Collection(
            preview.Entries,
            entry => Assert.Equal(RestorePreviewOutcomeKind.Exact, entry.Outcome),
            entry => Assert.Equal(RestorePreviewOutcomeKind.Adapted, entry.Outcome),
            entry => Assert.Equal(RestorePreviewOutcomeKind.Ambiguous, entry.Outcome),
            entry => Assert.Equal(RestorePreviewOutcomeKind.Missing, entry.Outcome));
        Assert.All(preview.Entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.AccessibilityLabel)));
        Assert.Equal(1, preview.BlockingErrorCount);
        RestorePlanPreviewAction destructive = Assert.Single(
            preview.GlobalActions,
            action => action.Kind == RestorePreviewActionKind.Minimize);
        Assert.True(destructive.IsDestructive);
        Assert.Contains("No windows will be closed", preview.DestructiveSummary);
        Assert.Equal(RestorePreviewActionKind.Skip, Assert.Single(preview.Entries[3].Actions).Kind);
    }

    [Fact]
    public void Disabling_entry_derives_new_plan_without_mutating_preview()
    {
        RestorePlanEntry enabled = Entry(
            0,
            RestorePlanEntryOutcome.Matched,
            Placement(),
            Candidate(20, WindowMatchConfidence.Exact),
            actions: [Move(0, 20)]);
        RestorePlanIssue missing = new(
            RestorePlanIssueCode.MissingResource,
            RestorePlanIssueSeverity.BlockingError,
            "Missing document.");
        RestorePlanEntry blocked = Entry(
            1,
            RestorePlanEntryOutcome.Blocked,
            Placement(),
            Candidate(21, WindowMatchConfidence.Strong),
            blockingErrors: [missing]);
        RestoreAction minimize = new(
            null,
            RestoreActionKind.MinimizeOtherWindows,
            null,
            "",
            "",
            false,
            null,
            "Minimize unrelated windows.");
        var preview = new RestorePlan
        {
            Entries = [enabled, blocked],
            Actions = [Move(0, 20), minimize],
            BlockingErrors = [missing]
        };

        RestorePlan approved = RestorePlanner.DeriveApprovedPlan(preview, [1]);

        Assert.NotSame(preview, approved);
        Assert.Equal(RestorePlanEntryOutcome.Blocked, preview.Entries[1].Outcome);
        Assert.Single(preview.BlockingErrors);
        Assert.Empty(preview.DisabledEntryIndexes);
        Assert.Equal(RestorePlanEntryOutcome.Excluded, approved.Entries[1].Outcome);
        Assert.Empty(approved.Entries[1].Actions);
        Assert.Empty(approved.BlockingErrors);
        Assert.True(approved.CanExecute);
        Assert.Contains(1, approved.DisabledEntryIndexes);
        Assert.Contains(21L, approved.ProtectedWindowHandles);
        Assert.DoesNotContain(approved.Actions, action => action.EntryIndex == 1);
    }

    [Fact]
    public void Disabling_every_entry_removes_terminal_minimize_action()
    {
        RestorePlanEntry entry = Entry(
            0,
            RestorePlanEntryOutcome.Matched,
            Placement(),
            Candidate(30, WindowMatchConfidence.Exact),
            actions: [Move(0, 30)]);
        RestoreAction minimize = new(
            null,
            RestoreActionKind.MinimizeOtherWindows,
            null,
            "",
            "",
            false,
            null,
            "Minimize unrelated windows.");
        var preview = new RestorePlan
        {
            Mode = RestoreModeKind.AlignAndMinimize,
            Entries = [entry],
            Actions = [Move(0, 30), minimize]
        };

        RestorePlan approved = RestorePlanner.DeriveApprovedPlan(preview, [0]);

        Assert.Empty(approved.Actions);
        Assert.Contains(30L, approved.ProtectedWindowHandles);
        Assert.Equal(RestorePlanEntryOutcome.Excluded, approved.Entries[0].Outcome);
    }

    [Fact]
    public void Disabling_browser_entry_removes_global_session_action_and_activates_safe_fallbacks()
    {
        RestoreAction browserSession = new(
            null,
            RestoreActionKind.RestoreBrowserSession,
            null,
            "Workspace",
            "",
            false,
            null,
            "Restore browser sessions.");
        RestoreAction firstFallback = BrowserFallback(0);
        RestoreAction secondFallback = BrowserFallback(1);
        RestorePlanEntry first = BrowserEntry(0, firstFallback);
        RestorePlanEntry second = BrowserEntry(1, secondFallback);
        var preview = new RestorePlan
        {
            Entries = [first, second],
            Actions = [browserSession, firstFallback, secondFallback],
            BrowserSessions =
            [
                new RestoreBrowserSession(
                    "brave", "Session", 0, 0, 0, 800, 600, "normal", [], [])
            ]
        };

        RestorePlan approved = RestorePlanner.DeriveApprovedPlan(preview, [0]);

        Assert.Contains(preview.Actions, action => action.Kind == RestoreActionKind.RestoreBrowserSession);
        Assert.DoesNotContain(approved.Actions, action => action.Kind == RestoreActionKind.RestoreBrowserSession);
        Assert.Empty(approved.BrowserSessions);
        RestoreAction approvedFallback = Assert.Single(
            approved.Actions,
            action => action.EntryIndex == 1);
        Assert.Equal(RestoreActionCondition.Always, approvedFallback.Condition);
        Assert.Equal(RestoreActionCondition.BrowserSessionUnavailable, secondFallback.Condition);
    }

    [Fact]
    public void Ambiguous_preview_exposes_distinguishing_candidates_and_resolution_updates_plan()
    {
        RestorePlanCandidate north = Candidate(41, WindowMatchConfidence.Strong) with
        {
            Title = "Alpha report north",
            ProcessName = "editor",
            WindowClassName = "EditorWindow",
            MonitorId = "primary",
            Bounds = new WindowIdentityBounds(10, 20, 810, 620),
            IdentityHint = new WindowIdentityHint
            {
                ExecutablePath = @"c:\apps\editor.exe",
                ProcessName = "editor",
                WindowClassName = "EditorWindow",
                TitleTokens = ["alpha", "north", "report"]
            },
            IsWithinAmbiguityMargin = true,
            CanRememberChoice = true,
            Evidence =
            [
                new WindowMatchEvidence(
                    WindowMatchEvidenceKind.TitleSimilarity,
                    true,
                    6800,
                    "The saved and live titles are strongly similar.")
            ]
        };
        RestorePlanCandidate south = north with
        {
            WindowHandle = 42,
            ProcessId = 1042,
            Title = "Alpha report south",
            IdentityHint = north.IdentityHint! with
            {
                TitleTokens = ["alpha", "report", "south"]
            }
        };
        RestorePlanIssue ambiguity = new(
            RestorePlanIssueCode.AmbiguousMatch,
            RestorePlanIssueSeverity.Warning,
            "Two candidates are inside the safety margin.");
        RestorePlanEntry entry = Entry(
            0,
            RestorePlanEntryOutcome.Blocked,
            Placement(),
            selected: null,
            warnings: [ambiguity],
            candidates: [north, south]);
        var plan = new RestorePlan
        {
            WorkspaceId = "11111111-1111-4111-8111-111111111111",
            Entries = [entry],
            Warnings = [ambiguity],
            ProtectedWindowHandles = new HashSet<long> { 41, 42 }
        };

        RestorePlanPreviewEntry preview = Assert.Single(RestorePlanPreviewBuilder.Build(plan).Entries);

        Assert.Equal(RestorePreviewOutcomeKind.Ambiguous, preview.Outcome);
        Assert.Collection(
            preview.Candidates,
            candidate =>
            {
                Assert.Equal(41, candidate.WindowHandle);
                Assert.Contains("EditorWindow", candidate.IdentityLabel);
                Assert.Contains("strongly similar", Assert.Single(candidate.Reasons));
                Assert.True(candidate.CanRememberChoice);
            },
            candidate => Assert.Equal(42, candidate.WindowHandle));

        RestorePlan resolved = RestorePlanner.ResolveAmbiguousMatch(plan, 0, 42);
        RestorePlanPreviewEntry resolvedEntry = Assert.Single(
            RestorePlanPreviewBuilder.Build(resolved).Entries);
        Assert.Equal(RestorePreviewOutcomeKind.Ready, resolvedEntry.Outcome);
        Assert.True(Assert.Single(resolvedEntry.Candidates).IsSelected);
    }

    private static RestorePlanEntry BrowserEntry(int index, RestoreAction action) => new(
        index,
        $"browser-{index}",
        RestorePlanEntryOutcome.AwaitingBrowserSession,
        "Await browser session.",
        new SavedWindowIdentity
        {
            EntryId = $"browser-{index}",
            ProcessName = "brave",
            BrowserFamily = "brave",
            Title = $"Browser {index}"
        },
        [],
        null,
        Placement(),
        RestoreLaunchRequirement.None("Browser session action."),
        [action],
        [],
        []);

    private static RestoreAction BrowserFallback(int index) => new(
        index,
        RestoreActionKind.LaunchApplication,
        null,
        @"C:\Apps\brave.exe",
        "--restore-last-session",
        false,
        null,
        "Fallback browser launch.",
        Condition: RestoreActionCondition.BrowserSessionUnavailable);

    private static RestorePlanEntry Entry(
        int index,
        RestorePlanEntryOutcome outcome,
        RestoreTargetPlacement placement,
        RestorePlanCandidate? selected,
        IReadOnlyList<RestoreAction>? actions = null,
        IReadOnlyList<RestorePlanIssue>? warnings = null,
        IReadOnlyList<RestorePlanIssue>? blockingErrors = null,
        IReadOnlyList<RestorePlanCandidate>? candidates = null) => new(
            index,
            $"entry-{index}",
            outcome,
            $"Entry {index} explanation.",
            new SavedWindowIdentity
            {
                EntryId = $"entry-{index}",
                ProcessName = $"app-{index}",
                Title = $"Window {index}"
            },
            candidates ?? (selected is null ? [] : [selected]),
            selected,
            placement,
            RestoreLaunchRequirement.None("No launch."),
            actions ?? [],
            warnings ?? [],
            blockingErrors ?? []);

    private static RestorePlanCandidate Candidate(long hwnd, WindowMatchConfidence confidence) => new(
        hwnd,
        (uint)(1000 + hwnd),
        true,
        100,
        confidence,
        [],
        null,
        false);

    private static RestoreAction Move(int entryIndex, long hwnd) => new(
        entryIndex,
        RestoreActionKind.RestoreExistingWindow,
        hwnd,
        "",
        "",
        false,
        Placement(),
        "Move existing window.");

    private static RestoreTargetPlacement Placement(
        RestoreMonitorMappingKind mapping = RestoreMonitorMappingKind.ExactStableId,
        bool scaled = false) => new(
            "primary",
            0,
            mapping,
            0,
            0,
            800,
            600,
            1,
            96,
            scaled ? 144u : 96u,
            scaled);
}
