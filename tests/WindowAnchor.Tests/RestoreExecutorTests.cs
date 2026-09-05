using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class RestoreExecutorTests
{
    [Fact]
    public void Default_readiness_policy_allows_slow_desktop_app_startup()
    {
        Assert.Equal(TimeSpan.FromSeconds(45), AppReadinessPolicy.Default.Timeout);
    }

    [Fact]
    public async Task Approved_window_action_is_revalidated_and_executed_once()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        LiveWindowIdentity live = Live(10, 1010, entry.ExecutablePath, "Notes");
        RestorePlan plan = Plan(Snapshot(entry), [live]);
        var inventory = Inventory((10, 1010, entry.ExecutablePath, "Notes"));
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(inventory, mutation).ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.Completed, result.Status);
        var restored = Assert.Single(mutation.Restores);
        Assert.Equal(new IntPtr(10), restored.Hwnd);
        Assert.True(restored.Record.CoordinatesAreFinal);
        Assert.Equal(4, inventory.LiveInventoryCalls);
        RestoreExecutionEntryResult entryResult = Assert.Single(result.Entries);
        Assert.Equal(RestoreExecutionEntryStatus.Restored, entryResult.Status);
        Assert.Equal(WindowPlacementVerificationState.Applied, entryResult.PlacementVerification);
        Assert.Equal(0, entryResult.PlacementRetryCount);
        Assert.Equal(
            Enumerable.Range(0, plan.Actions.Count),
            result.Actions.Select(action => action.ActionIndex));
    }

    [Fact]
    public async Task Placement_noise_inside_dpi_aware_tolerance_does_not_retry()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(Snapshot(entry), [Live(11, 1111, entry.ExecutablePath, "Notes")]);
        RestoreTargetPlacement target = Assert.Single(plan.Entries).TargetPlacement;
        var probe = new FakeWindowPlacementProbe { DefaultObservation = Placement(target, 7) };
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(
            Inventory((11, 1111, entry.ExecutablePath, "Notes")),
            mutation,
            placementProbe: probe).ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.Completed, result.Status);
        Assert.Single(mutation.Restores);
        RestoreExecutionEntryResult restored = Assert.Single(result.Entries);
        Assert.Equal(WindowPlacementVerificationState.Applied, restored.PlacementVerification);
        Assert.Equal(8, restored.PlacementTolerancePixels);
        Assert.Equal(0, restored.PlacementRetryCount);
    }

    [Fact]
    public async Task Mismatched_placement_is_retried_on_the_same_assigned_hwnd_and_verified()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(Snapshot(entry), [Live(12, 1212, entry.ExecutablePath, "Notes")]);
        RestoreTargetPlacement target = Assert.Single(plan.Entries).TargetPlacement;
        var probe = new FakeWindowPlacementProbe
        {
            ObservationProvider = (call, _) => call <= 2
                ? Placement(target, 100)
                : Placement(target)
        };
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(
            Inventory((12, 1212, entry.ExecutablePath, "Notes")),
            mutation,
            placementProbe: probe,
            placementPolicy: VerificationPolicy(maxRetries: 2)).ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.Completed, result.Status);
        Assert.Equal(2, mutation.Restores.Count);
        Assert.All(mutation.Restores, restore => Assert.Equal(new IntPtr(12), restore.Hwnd));
        RestoreExecutionEntryResult restored = Assert.Single(result.Entries);
        Assert.Equal(WindowPlacementVerificationState.Applied, restored.PlacementVerification);
        Assert.Equal(1, restored.PlacementRetryCount);
    }

    [Fact]
    public async Task Rejected_placement_stops_at_the_configured_retry_bound_and_is_reported()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(Snapshot(entry), [Live(13, 1313, entry.ExecutablePath, "Notes")]);
        RestoreTargetPlacement target = Assert.Single(plan.Entries).TargetPlacement;
        var probe = new FakeWindowPlacementProbe { DefaultObservation = Placement(target, 100) };
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(
            Inventory((13, 1313, entry.ExecutablePath, "Notes")),
            mutation,
            placementProbe: probe,
            placementPolicy: VerificationPolicy(maxRetries: 2)).ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.CompletedWithFailures, result.Status);
        Assert.Equal(3, mutation.Restores.Count);
        RestoreExecutionEntryResult failed = Assert.Single(result.Entries);
        Assert.Equal(RestoreExecutionEntryStatus.Failed, failed.Status);
        Assert.Equal(WindowPlacementVerificationState.Rejected, failed.PlacementVerification);
        Assert.Equal(2, failed.PlacementRetryCount);
        RestoreExecutionActionResult action = Assert.Single(result.Actions);
        Assert.Equal(RestoreExecutionActionStatus.Failed, action.Status);
        Assert.Equal(WindowPlacementVerificationState.Rejected, action.PlacementVerification);
    }

    [Fact]
    public async Task App_that_moves_an_applied_window_is_distinguished_from_rejection()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(Snapshot(entry), [Live(14, 1414, entry.ExecutablePath, "Notes")]);
        RestoreTargetPlacement target = Assert.Single(plan.Entries).TargetPlacement;
        var probe = new FakeWindowPlacementProbe
        {
            ObservationProvider = (call, _) => call is 1 or 3
                ? Placement(target)
                : Placement(target, 100)
        };
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(
            Inventory((14, 1414, entry.ExecutablePath, "Notes")),
            mutation,
            placementProbe: probe,
            placementPolicy: VerificationPolicy(maxRetries: 1)).ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.CompletedWithFailures, result.Status);
        Assert.Equal(2, mutation.Restores.Count);
        RestoreExecutionEntryResult failed = Assert.Single(result.Entries);
        Assert.Equal(WindowPlacementVerificationState.MovedByApp, failed.PlacementVerification);
        Assert.Equal(1, failed.PlacementRetryCount);
    }

    [Fact]
    public async Task Window_closed_during_verification_is_reported_without_retrying_a_new_handle()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(Snapshot(entry), [Live(16, 1616, entry.ExecutablePath, "Notes")]);
        var mutation = new RecordingWindowMutation();
        var probe = new FakeWindowPlacementProbe
        {
            DefaultObservation = WindowPlacementObservation.Gone
        };

        RestoreExecutionResult result = await Executor(
            Inventory((16, 1616, entry.ExecutablePath, "Notes")),
            mutation,
            placementProbe: probe,
            placementPolicy: VerificationPolicy(maxRetries: 2)).ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.CompletedWithFailures, result.Status);
        Assert.Single(mutation.Restores);
        RestoreExecutionEntryResult failed = Assert.Single(result.Entries);
        Assert.Equal(WindowPlacementVerificationState.WindowGone, failed.PlacementVerification);
        Assert.Equal(0, failed.PlacementRetryCount);
    }

    [Fact]
    public void Non_final_mismatch_is_classified_as_settling()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestoreTargetPlacement target = Assert.Single(Plan(Snapshot(entry)).Entries).TargetPlacement;

        WindowPlacementEvaluation evaluation = WindowPlacementVerifier.Evaluate(
            target,
            Placement(target, 100),
            VerificationPolicy(maxRetries: 2),
            finalObservation: false,
            wasPreviouslyApplied: false);

        Assert.Equal(WindowPlacementVerificationState.Settling, evaluation.State);
    }

    [Fact]
    public async Task Placement_adapter_can_override_generic_tolerance_without_changing_matching()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\special.exe", "Special");
        RestorePlan plan = Plan(Snapshot(entry), [Live(15, 1515, entry.ExecutablePath, "Special")]);
        RestoreTargetPlacement target = Assert.Single(plan.Entries).TargetPlacement;
        var probe = new FakeWindowPlacementProbe { DefaultObservation = Placement(target, 15) };
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(
            Inventory((15, 1515, entry.ExecutablePath, "Special")),
            mutation,
            placementProbe: probe,
            placementStrategies: [new WideToleranceSpecialPlacementStrategy()]).ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.Completed, result.Status);
        Assert.Single(mutation.Restores);
        RestoreExecutionEntryResult restored = Assert.Single(result.Entries);
        Assert.Equal(WindowPlacementVerificationState.Applied, restored.PlacementVerification);
        Assert.Equal("special-placement", restored.PlacementVerificationStrategy);
        Assert.Equal(20, restored.PlacementTolerancePixels);
    }

    [Theory]
    [InlineData(false, RestorePlanStaleReason.WindowClosed)]
    [InlineData(true, RestorePlanStaleReason.WindowReplaced)]
    public async Task Closed_or_replaced_hwnd_is_reported_stale_without_mutation(
        bool replaceHandle,
        RestorePlanStaleReason expectedReason)
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(
            Snapshot(entry),
            [Live(20, 2020, entry.ExecutablePath, "Notes")]);
        var inventory = new FakeWindowInventory
        {
            Live = replaceHandle
                ? Records((20, 9090, entry.ExecutablePath, "Replacement"))
                : new Dictionary<IntPtr, (uint Pid, WindowRecord Record)>(),
            IsAliveProvider = _ => replaceHandle
        };
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(inventory, mutation).ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.StalePlan, result.Status);
        RestoreExecutionActionResult action = Assert.Single(result.Actions);
        Assert.Equal(RestoreExecutionActionStatus.Stale, action.Status);
        Assert.Equal(expectedReason, action.StaleReason);
        Assert.Equal(RestoreExecutionEntryStatus.Stale, Assert.Single(result.Entries).Status);
        Assert.Empty(mutation.Restores);
    }

    [Fact]
    public async Task Resource_removed_after_plan_approval_becomes_stale_without_process_launch()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(
            Snapshot(entry),
            resources:
            [
                new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    entry.ExecutablePath)
            ]);
        var resources = new FakeRestoreResourceBoundary();
        resources.AvailabilityByTarget[entry.ExecutablePath] = RestoreResourceAvailability.Missing;
        var process = new RecordingRestoreProcessLauncher();

        RestoreExecutionResult result = await Executor(
            new FakeWindowInventory(),
            new RecordingWindowMutation(),
            process,
            resources: resources).ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.StalePlan, result.Status);
        Assert.Contains(
            result.Actions,
            action => action.Kind == RestoreActionKind.LaunchApplication &&
                      action.StaleReason == RestorePlanStaleReason.ResourceMissing);
        Assert.Equal(RestoreExecutionEntryStatus.Stale, Assert.Single(result.Entries).Status);
        Assert.Empty(process.Launches);
    }

    [Fact]
    public async Task Eligible_window_appearing_after_preview_marks_plan_stale_before_any_launch()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(
            Snapshot(entry),
            resources:
            [
                new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    entry.ExecutablePath)
            ]);
        var inventory = Inventory((25, 2525, entry.ExecutablePath, "Notes"));
        var mutation = new RecordingWindowMutation();
        var process = new RecordingRestoreProcessLauncher();

        RestoreExecutionResult result = await Executor(
            inventory,
            mutation,
            process).ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.StalePlan, result.Status);
        Assert.Contains(
            result.Actions,
            action => action.StaleReason == RestorePlanStaleReason.WindowInventoryChanged);
        Assert.Empty(process.Launches);
        Assert.Empty(mutation.Restores);
    }

    [Fact]
    public async Task Unselected_candidate_closing_after_preview_does_not_stale_selected_assignment()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Alpha report north");
        RestorePlan plan = Plan(
            Snapshot(entry),
            [
                Live(26, 2626, entry.ExecutablePath, "Alpha report north"),
                Live(27, 2727, entry.ExecutablePath, "Alpha report south")
            ]);
        RestorePlanEntry plannedEntry = Assert.Single(plan.Entries);
        Assert.Equal(26, plannedEntry.SelectedMatch?.WindowHandle);
        Assert.Equal(2, plannedEntry.Candidates.Count(candidate => candidate.IsEligible));
        var inventory = Inventory((26, 2626, entry.ExecutablePath, "Alpha report north"));
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(inventory, mutation).ExecuteAsync(plan);

        Assert.Equal(RestoreExecutionStatus.Completed, result.Status);
        Assert.Equal(new IntPtr(26), Assert.Single(mutation.Restores).Hwnd);
    }

    [Fact]
    public async Task Launch_waits_for_responsive_stable_identity_instead_of_fixed_sleeps()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(
            Snapshot(entry),
            resources:
            [
                new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    entry.ExecutablePath)
            ]);
        var inventory = new FakeWindowInventory();
        var process = new RecordingRestoreProcessLauncher
        {
            OnLaunch = _ => inventory.Live = Records((30, 3030, entry.ExecutablePath, "Loading"))
        };
        var clock = new FakeRestoreClock
        {
            OnDelay = count =>
            {
                if (count == 1)
                    inventory.Live = Records((30, 3030, entry.ExecutablePath, "Notes"));
            }
        };
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(
            inventory,
            mutation,
            process,
            clock).ExecuteAsync(plan);

        Assert.Equal(
            [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250),
             TimeSpan.FromMilliseconds(350)],
            clock.Delays);
        Assert.Equal(RestoreExecutionStatus.Completed, result.Status);
        Assert.Equal(new IntPtr(30), Assert.Single(mutation.Restores).Hwnd);
        Assert.Equal(RestoreExecutionEntryStatus.Restored, Assert.Single(result.Entries).Status);
        Assert.Equal(plan.Actions.Count, result.Actions.Count);
        Assert.All(result.Actions, action => Assert.NotEqual(RestoreExecutionActionStatus.Stale, action.Status));
        RestoreExecutionActionResult readiness = Assert.Single(
            result.Actions,
            action => action.Kind == RestoreActionKind.AwaitWindowAppearance);
        Assert.Equal(AppReadinessState.Ready, readiness.ReadinessState);
        Assert.Equal("generic", readiness.ReadinessStrategy);
    }

    [Fact]
    public async Task Ready_entry_is_positioned_while_another_entry_continues_to_timeout()
    {
        WorkspaceEntry fast = Entry(@"C:\Apps\fast.exe", "Fast");
        WorkspaceEntry slow = Entry(@"C:\Apps\slow.exe", "Slow");
        RestorePlan plan = Plan(
            Snapshot(fast, slow),
            resources:
            [
                new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    fast.ExecutablePath),
                new(1, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    slow.ExecutablePath)
            ]);
        var inventory = new FakeWindowInventory();
        var process = new RecordingRestoreProcessLauncher
        {
            OnLaunch = action =>
            {
                if (action.Target.Equals(fast.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    inventory.Live = Records((31, 3131, fast.ExecutablePath, "Fast"));
            }
        };
        var mutation = new RecordingWindowMutation();
        bool fastWasPlacedBeforeTimeout = false;
        var clock = new FakeRestoreClock
        {
            OnDelay = count =>
            {
                if (count == 2)
                    fastWasPlacedBeforeTimeout = mutation.Restores.Count == 1;
            }
        };
        var readiness = new FakeAppReadinessProbe(inventory);
        readiness.AdditionalProcessNames.Add("slow");
        var policy = new AppReadinessPolicy
        {
            PollInterval = TimeSpan.FromMilliseconds(250),
            Timeout = TimeSpan.FromMilliseconds(750),
            RequiredStableObservations = 2
        };

        RestoreExecutionResult result = await Executor(
            inventory,
            mutation,
            process,
            clock,
            readinessProbe: readiness,
            readinessPolicy: policy).ExecuteAsync(plan);

        Assert.True(fastWasPlacedBeforeTimeout);
        Assert.Equal(new IntPtr(31), Assert.Single(mutation.Restores).Hwnd);
        Assert.Equal(RestoreExecutionStatus.CompletedWithFailures, result.Status);
        Assert.Equal(RestoreExecutionEntryStatus.Restored, result.Entries[0].Status);
        Assert.Equal(RestoreExecutionEntryStatus.Failed, result.Entries[1].Status);
        Assert.Equal(AppReadinessState.TimedOut, result.Entries[1].ReadinessState);
        RestoreExecutionActionResult timeout = Assert.Single(
            result.Actions,
            action => action.EntryIndex == 1 &&
                      action.Kind == RestoreActionKind.AwaitWindowAppearance);
        Assert.Equal(RestoreExecutionActionStatus.Failed, timeout.Status);
        Assert.Equal(AppReadinessState.TimedOut, timeout.ReadinessState);
        Assert.Contains("750 ms", timeout.Explanation);
        Assert.Equal(4, clock.Delays.Count);
    }

    [Fact]
    public async Task Successful_unrelated_launch_does_not_start_a_passive_entries_timeout()
    {
        WorkspaceEntry active = Entry(@"C:\Apps\active.exe", "Active");
        WorkspaceEntry passive = Entry(@"C:\Apps\passive.exe", "Passive");
        RestorePlan generated = Plan(
            Snapshot(active, passive),
            resources:
            [
                new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    active.ExecutablePath),
                new(1, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    passive.ExecutablePath)
            ]);
        RestorePlanEntry passiveEntry = generated.Entries[1] with
        {
            Outcome = RestorePlanEntryOutcome.AwaitingRunningApplication,
            LaunchRequirement = RestoreLaunchRequirement.None(
                "Another related action would have to create this window."),
            Actions = generated.Entries[1].Actions
                .Where(action => action.Kind == RestoreActionKind.AwaitWindowAppearance)
                .ToArray()
        };
        RestorePlan plan = generated with
        {
            Entries = [generated.Entries[0], passiveEntry],
            Actions = generated.Actions
                .Where(action => action.EntryIndex != 1 ||
                    action.Kind == RestoreActionKind.AwaitWindowAppearance)
                .ToArray()
        };
        var inventory = new FakeWindowInventory();
        var process = new RecordingRestoreProcessLauncher
        {
            OnLaunch = action =>
                inventory.Live = Records((36, 3636, action.Target, "Active"))
        };
        var clock = new FakeRestoreClock();

        RestoreExecutionResult result = await Executor(
            inventory,
            new RecordingWindowMutation(),
            process,
            clock).ExecuteAsync(plan);

        RestoreExecutionActionResult passiveWait = Assert.Single(
            result.Actions,
            action => action.EntryIndex == 1 &&
                      action.Kind == RestoreActionKind.AwaitWindowAppearance);
        Assert.Equal(RestoreExecutionActionStatus.Skipped, passiveWait.Status);
        Assert.Null(passiveWait.ReadinessState);
        Assert.DoesNotContain(result.Entries, entry =>
            entry.EntryIndex == 1 && entry.ReadinessState == AppReadinessState.TimedOut);
        Assert.True(clock.Delays.Count < 5);
    }

    [Fact]
    public async Task Readiness_timeout_counts_probe_work_against_the_wall_clock_budget()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(
            Snapshot(entry),
            resources:
            [
                new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    entry.ExecutablePath)
            ]);
        var inventory = new FakeWindowInventory();
        var clock = new FakeRestoreClock();
        var readiness = new FakeAppReadinessProbe(inventory)
        {
            ObservationProvider = _ =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(600));
                return new AppReadinessObservation
                {
                    RunningProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "editor"
                    }
                };
            }
        };
        var progress = new RecordingProgress<RestoreProgressReport>();
        var policy = new AppReadinessPolicy
        {
            PollInterval = TimeSpan.FromMilliseconds(250),
            Timeout = TimeSpan.FromMilliseconds(500),
            RequiredStableObservations = 2
        };

        RestoreExecutionResult result = await Executor(
            inventory,
            new RecordingWindowMutation(),
            clock: clock,
            readinessProbe: readiness,
            readinessPolicy: policy).ExecuteAsync(plan, progress: progress);

        Assert.Equal(RestoreExecutionStatus.CompletedWithFailures, result.Status);
        Assert.Equal(AppReadinessState.TimedOut, Assert.Single(result.Entries).ReadinessState);
        Assert.Equal(1, readiness.ObservationCalls);
        Assert.Empty(clock.Delays);
        RestoreProgressReport waiting = Assert.Single(
            progress.Reports,
            report => report.Stage == RestoreProgressStage.WaitingForApplications);
        Assert.Equal(TimeSpan.FromMilliseconds(600), waiting.Elapsed);
        Assert.Equal(TimeSpan.FromMilliseconds(500), waiting.Timeout);
        Assert.Contains("editor", waiting.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_interrupts_readiness_polling_before_timeout()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(
            Snapshot(entry),
            resources:
            [
                new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    entry.ExecutablePath)
            ]);
        var inventory = new FakeWindowInventory();
        var readiness = new FakeAppReadinessProbe(inventory);
        readiness.AdditionalProcessNames.Add("editor");
        using var cancellation = new CancellationTokenSource();
        var clock = new FakeRestoreClock { OnDelay = _ => cancellation.Cancel() };

        RestoreExecutionResult result = await Executor(
            inventory,
            new RecordingWindowMutation(),
            clock: clock,
            readinessProbe: readiness).ExecuteAsync(plan, cancellation.Token);

        Assert.Equal(RestoreExecutionStatus.Cancelled, result.Status);
        Assert.True(result.WasCancelled);
        Assert.Single(clock.Delays);
        Assert.Equal(RestoreExecutionEntryStatus.Cancelled, Assert.Single(result.Entries).Status);
        Assert.Contains(result.Actions, action =>
            action.Kind == RestoreActionKind.AwaitWindowAppearance &&
            action.Status == RestoreExecutionActionStatus.Cancelled);
    }

    [Fact]
    public async Task Adapter_strategy_overrides_generic_stability_policy_after_safe_matching()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\special.exe", "Special");
        RestorePlan plan = Plan(
            Snapshot(entry),
            resources:
            [
                new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available,
                    entry.ExecutablePath)
            ]);
        var inventory = new FakeWindowInventory();
        var process = new RecordingRestoreProcessLauncher
        {
            OnLaunch = _ => inventory.Live = Records((32, 3232, entry.ExecutablePath, "Special"))
        };
        var readiness = new FakeAppReadinessProbe(inventory);
        readiness.UnresponsiveWindowHandles.Add(32);
        var clock = new FakeRestoreClock();
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(
            inventory,
            mutation,
            process,
            clock,
            readinessProbe: readiness,
            readinessStrategies: [new ImmediateSpecialAppReadinessStrategy()]).ExecuteAsync(plan);

        Assert.Equal([TimeSpan.FromMilliseconds(350)], clock.Delays);
        Assert.Equal(new IntPtr(32), Assert.Single(mutation.Restores).Hwnd);
        RestoreExecutionActionResult wait = Assert.Single(
            result.Actions,
            action => action.Kind == RestoreActionKind.AwaitWindowAppearance);
        Assert.Equal(AppReadinessState.Ready, wait.ReadinessState);
        Assert.Equal("special-adapter", wait.ReadinessStrategy);
    }

    [Fact]
    public void Generic_engine_reports_every_required_readiness_state_deterministically()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlanEntry planEntry = Assert.Single(Plan(Snapshot(entry)).Entries);
        LiveWindowIdentity live = Live(33, 3333, entry.ExecutablePath, "Notes");
        var engine = new AppReadinessEngine(new AppReadinessPolicy
        {
            PollInterval = TimeSpan.FromMilliseconds(100),
            Timeout = TimeSpan.FromMilliseconds(500),
            RequiredStableObservations = 2
        });
        var tracker = new AppReadinessTracker();
        IReadOnlySet<IntPtr> assigned = new HashSet<IntPtr>();

        AppReadinessEvaluation notStarted = engine.Evaluate(
            planEntry,
            new AppReadinessObservation(),
            assigned,
            tracker,
            TimeSpan.Zero);
        AppReadinessEvaluation processStarted = engine.Evaluate(
            planEntry,
            new AppReadinessObservation
            {
                RunningProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "editor"
                }
            },
            assigned,
            tracker,
            TimeSpan.FromMilliseconds(100));
        AppReadinessEvaluation windowFound = engine.Evaluate(
            planEntry,
            new AppReadinessObservation
            {
                Windows = [live],
                RunningProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "editor"
                }
            },
            assigned,
            tracker,
            TimeSpan.FromMilliseconds(200));
        AppReadinessEvaluation ready = engine.Evaluate(
            planEntry,
            new AppReadinessObservation
            {
                Windows = [live],
                RunningProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "editor"
                },
                ResponsiveWindowHandles = new HashSet<long> { 33 }
            },
            assigned,
            tracker,
            TimeSpan.FromMilliseconds(300));
        AppReadinessEvaluation timedOut = engine.Evaluate(
            planEntry,
            new AppReadinessObservation
            {
                RunningProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "editor"
                }
            },
            assigned,
            new AppReadinessTracker(),
            TimeSpan.FromMilliseconds(500));
        AppReadinessEvaluation failed = engine.Evaluate(
            planEntry,
            new AppReadinessObservation { Failure = "Injected observation failure." },
            assigned,
            new AppReadinessTracker(),
            TimeSpan.Zero);

        Assert.Equal(AppReadinessState.NotStarted, notStarted.State);
        Assert.Equal(AppReadinessState.ProcessStarted, processStarted.State);
        Assert.Equal(AppReadinessState.WindowFound, windowFound.State);
        Assert.Equal(AppReadinessState.Ready, ready.State);
        Assert.Equal(AppReadinessState.TimedOut, timedOut.State);
        Assert.Equal(AppReadinessState.Failed, failed.State);
    }

    [Fact]
    public async Task Browser_failure_executes_only_the_approved_fallback_then_reconciles()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\brave.exe", "Browser");
        entry.ProcessName = "brave";
        WorkspaceSnapshot snapshot = Snapshot(entry);
        snapshot.BrowserSessions.Add(new BrowserSession
        {
            Browser = "brave",
            ActiveTitle = "Browser",
            Tabs = [new BrowserTab { Url = "https://example.test/", Title = "Example" }]
        });
        RestorePlan plan = Plan(
            snapshot,
            browserAvailability: BrowserSessionRestoreAvailability.Available);
        Assert.Contains(plan.Actions, action => action.Kind == RestoreActionKind.RestoreBrowserSession);
        Assert.Contains(
            plan.Actions,
            action => action.Condition == RestoreActionCondition.BrowserSessionUnavailable);

        var browser = new FakeBrowserSessionConnector { RestoreResult = false };
        var inventory = new FakeWindowInventory();
        var process = new RecordingRestoreProcessLauncher
        {
            OnLaunch = _ => inventory.Live = Records((40, 4040, entry.ExecutablePath, "Browser"))
        };

        RestoreExecutionResult result = await Executor(
            inventory,
            new RecordingWindowMutation(),
            process,
            new FakeRestoreClock(),
            browser).ExecuteAsync(plan);

        Assert.Equal(1, browser.RestoreCalls);
        Assert.Single(browser.RestoredSessions);
        Assert.Single(process.Launches);
        Assert.Equal(RestoreActionCondition.BrowserSessionUnavailable, process.Launches[0].Condition);
        Assert.Equal(RestoreExecutionEntryStatus.Restored, Assert.Single(result.Entries).Status);
        Assert.Equal(RestoreExecutionStatus.StalePlan, result.Status);
    }

    [Fact]
    public async Task Successful_browser_action_skips_approved_fallback()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\brave.exe", "Browser");
        entry.ProcessName = "brave";
        WorkspaceSnapshot snapshot = Snapshot(entry);
        snapshot.BrowserSessions.Add(new BrowserSession { Browser = "brave" });
        RestorePlan plan = Plan(
            snapshot,
            browserAvailability: BrowserSessionRestoreAvailability.Available);
        var browser = new FakeBrowserSessionConnector { RestoreResult = true };
        var inventory = new FakeWindowInventory();
        var clock = new FakeRestoreClock
        {
            OnDelay = count =>
            {
                if (count == 1)
                    inventory.Live = Records((50, 5050, entry.ExecutablePath, "Browser"));
            }
        };
        var process = new RecordingRestoreProcessLauncher();

        RestoreExecutionResult result = await Executor(
            inventory,
            new RecordingWindowMutation(),
            process,
            clock,
            browser).ExecuteAsync(plan);

        Assert.Empty(process.Launches);
        Assert.Contains(
            result.Actions,
            action => action.Kind == RestoreActionKind.LaunchApplication &&
                      action.Status == RestoreExecutionActionStatus.Skipped);
        Assert.Equal(RestoreExecutionStatus.Completed, result.Status);
        Assert.Equal(RestoreExecutionEntryStatus.Restored, Assert.Single(result.Entries).Status);
    }

    [Fact]
    public async Task Align_and_minimize_uses_only_final_revalidated_assignments()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(
            Snapshot(entry),
            [Live(60, 6060, entry.ExecutablePath, "Notes")],
            mode: RestoreMode.AlignAndMinimize);
        var inventory = Inventory((60, 6060, entry.ExecutablePath, "Notes"));
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(inventory, mutation).ExecuteAsync(plan);

        Assert.Equal(new IntPtr(60), Assert.Single(Assert.Single(mutation.MinimizeCalls)));
        Assert.Equal([60L], result.AssignedWindowHandles);
        Assert.Equal(
            RestoreActionKind.MinimizeOtherWindows,
            Assert.Single(result.Actions, action => action.Kind == RestoreActionKind.MinimizeOtherWindows).Kind);
    }

    [Fact]
    public async Task Selective_plan_executes_included_entry_and_reports_excluded_entry()
    {
        WorkspaceEntry included = Entry(@"C:\Apps\editor.exe", "Primary notes");
        WorkspaceEntry excluded = Entry(@"C:\Apps\chat.exe", "Secondary chat");
        excluded.MonitorId = "secondary";
        excluded.Position.MonitorId = "secondary";
        WorkspaceSnapshot snapshot = Snapshot(included, excluded);
        RestorePlan plan = Plan(
            snapshot,
            [
                Live(61, 6161, included.ExecutablePath, "Primary notes"),
                Live(62, 6262, excluded.ExecutablePath, "Secondary chat")
            ],
            mode: RestoreMode.Selective(["primary"]));
        var inventory = Inventory(
            (61, 6161, included.ExecutablePath, "Primary notes"),
            (62, 6262, excluded.ExecutablePath, "Secondary chat"));
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(inventory, mutation).ExecuteAsync(plan);

        Assert.Equal(new IntPtr(61), Assert.Single(mutation.Restores).Hwnd);
        Assert.Equal(RestoreExecutionEntryStatus.Restored, result.Entries[0].Status);
        Assert.Equal(RestoreExecutionEntryStatus.Excluded, result.Entries[1].Status);
        Assert.DoesNotContain(plan.Actions, action => action.EntryIndex == 1);
    }

    [Fact]
    public async Task Disabled_entry_is_not_moved_or_minimized_by_align_plan()
    {
        WorkspaceEntry included = Entry(@"C:\Apps\editor.exe", "Notes");
        WorkspaceEntry disabled = Entry(@"C:\Apps\chat.exe", "Chat");
        RestorePlan preview = Plan(
            Snapshot(included, disabled),
            [
                Live(63, 6363, included.ExecutablePath, "Notes"),
                Live(64, 6464, disabled.ExecutablePath, "Chat")
            ],
            mode: RestoreMode.AlignAndMinimize);
        RestorePlan approved = RestorePlanner.DeriveApprovedPlan(preview, [1]);
        var inventory = Inventory(
            (63, 6363, included.ExecutablePath, "Notes"),
            (64, 6464, disabled.ExecutablePath, "Chat"));
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(inventory, mutation).ExecuteAsync(approved);

        Assert.Equal(new IntPtr(63), Assert.Single(mutation.Restores).Hwnd);
        HashSet<IntPtr> keep = Assert.Single(mutation.MinimizeCalls);
        Assert.Contains(new IntPtr(63), keep);
        Assert.Contains(new IntPtr(64), keep);
        Assert.Equal([63L], result.AssignedWindowHandles);
        Assert.Equal(RestoreExecutionEntryStatus.Excluded, result.Entries[1].Status);
    }

    [Fact]
    public async Task Two_user_resolved_ambiguous_entries_execute_with_session_wide_unique_handles()
    {
        WorkspaceEntry first = Entry(@"C:\Apps\editor.exe", "Alpha report");
        WorkspaceEntry second = Entry(@"C:\Apps\editor.exe", "Alpha report");
        RestorePlan preview = Plan(
            Snapshot(first, second),
            [
                Live(80, 8080, first.ExecutablePath, "Alpha report north"),
                Live(81, 8181, first.ExecutablePath, "Alpha report south")
            ]);
        RestorePlan firstResolved = RestorePlanner.ResolveAmbiguousMatch(preview, 0, 80);
        RestorePlan approved = RestorePlanner.ResolveAmbiguousMatch(firstResolved, 1, 81);
        var inventory = Inventory(
            (80, 8080, first.ExecutablePath, "Alpha report north"),
            (81, 8181, first.ExecutablePath, "Alpha report south"));
        var mutation = new RecordingWindowMutation();

        RestoreExecutionResult result = await Executor(inventory, mutation).ExecuteAsync(approved);

        Assert.Equal(RestoreExecutionStatus.Completed, result.Status);
        Assert.Equal([80L, 81L], result.AssignedWindowHandles.Order());
        Assert.Equal([new IntPtr(80), new IntPtr(81)],
            mutation.Restores.Select(item => item.Hwnd).Order());
    }

    [Fact]
    public async Task Pre_cancelled_execution_preserves_initial_reconciliation_and_skips_later_actions()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes");
        RestorePlan plan = Plan(
            Snapshot(entry),
            [Live(70, 7070, entry.ExecutablePath, "Notes")]);
        var inventory = Inventory((70, 7070, entry.ExecutablePath, "Notes"));
        var mutation = new RecordingWindowMutation();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        RestoreExecutionResult result = await Executor(inventory, mutation).ExecuteAsync(
            plan,
            cancellation.Token);

        Assert.True(result.WasCancelled);
        Assert.Equal(RestoreExecutionStatus.Cancelled, result.Status);
        Assert.Single(mutation.Restores);
        Assert.Equal(RestoreExecutionActionStatus.Succeeded, Assert.Single(result.Actions).Status);
    }

    private static RestoreExecutor Executor(
        FakeWindowInventory inventory,
        RecordingWindowMutation mutation,
        RecordingRestoreProcessLauncher? process = null,
        FakeRestoreClock? clock = null,
        FakeBrowserSessionConnector? browser = null,
        FakeRestoreResourceBoundary? resources = null,
        FakeAppReadinessProbe? readinessProbe = null,
        AppReadinessPolicy? readinessPolicy = null,
        IEnumerable<IAppReadinessStrategy>? readinessStrategies = null,
        IWindowPlacementProbe? placementProbe = null,
        WindowPlacementVerificationPolicy? placementPolicy = null,
        IEnumerable<IWindowPlacementVerificationStrategy>? placementStrategies = null) => new(
            inventory,
            mutation,
            process ?? new RecordingRestoreProcessLauncher(),
            clock ?? new FakeRestoreClock(),
            resources ?? new FakeRestoreResourceBoundary(),
            browser,
            readinessProbe ?? new FakeAppReadinessProbe(inventory),
            readinessPolicy,
            readinessStrategies,
            placementProbe,
            placementPolicy,
            placementStrategies);

    private static RestorePlan Plan(
        WorkspaceSnapshot snapshot,
        IReadOnlyList<LiveWindowIdentity>? live = null,
        IReadOnlyList<RestoreResourceObservation>? resources = null,
        BrowserSessionRestoreAvailability browserAvailability =
            BrowserSessionRestoreAvailability.NotAvailable,
        RestoreMode? mode = null) => RestorePlanner.Build(
            snapshot,
            new RestoreLiveInventory
            {
                Windows = live ?? Array.Empty<LiveWindowIdentity>(),
                Resources = resources ?? Array.Empty<RestoreResourceObservation>(),
                BrowserSessionRestore = browserAvailability
            },
            new RestoreMonitorTopology
            {
                Monitors = [new RestoreMonitor("primary", 0, 0, 0, 1920, 1080, 96, true)]
            },
            mode ?? RestoreMode.Standard);

    private static WorkspaceSnapshot Snapshot(params WorkspaceEntry[] entries) => new()
    {
        WorkspaceId = "executor-workspace",
        Name = "Executor fixture",
        Entries = entries.ToList()
    };

    private static WorkspaceEntry Entry(string executable, string title) => new()
    {
        ExecutablePath = executable,
        ProcessName = Path.GetFileNameWithoutExtension(executable),
        WindowClassName = "EditorWindow",
        MonitorId = "primary",
        Position = Record(executable, title)
    };

    private static LiveWindowIdentity Live(
        int hwnd,
        uint pid,
        string executable,
        string title) => WindowIdentityExtractor.FromLive(
            new IntPtr(hwnd),
            pid,
            Record(executable, title));

    private static FakeWindowInventory Inventory(
        params (int Hwnd, uint Pid, string Executable, string Title)[] windows) => new()
    {
        Live = Records(windows)
    };

    private static Dictionary<IntPtr, (uint Pid, WindowRecord Record)> Records(
        params (int Hwnd, uint Pid, string Executable, string Title)[] windows) =>
        windows.ToDictionary(
            window => new IntPtr(window.Hwnd),
            window => (window.Pid, Record(window.Executable, window.Title)));

    private static WindowRecord Record(string executable, string title) => new()
    {
        ExecutablePath = executable,
        ProcessName = Path.GetFileNameWithoutExtension(executable),
        ClassName = "EditorWindow",
        TitleSnippet = title,
        MonitorId = "primary",
        SavedDpi = 96,
        NormalRight = 800,
        NormalBottom = 600
    };

    private static WindowPlacementObservation Placement(
        RestoreTargetPlacement target,
        int offset = 0) => new(
            true,
            true,
            target.Left + offset,
            target.Top + offset,
            target.Right + offset,
            target.Bottom + offset,
            target.ShowCmd,
            target.TargetDpi);

    private static WindowPlacementVerificationPolicy VerificationPolicy(int maxRetries) => new()
    {
        InitialDelay = TimeSpan.Zero,
        RetryDelay = TimeSpan.Zero,
        MaxRetries = maxRetries
    };

    private sealed class ImmediateSpecialAppReadinessStrategy : IAppReadinessStrategy
    {
        public string Name => "special-adapter";

        public bool CanHandle(SavedWindowIdentity identity) =>
            identity.ProcessName.Equals("special", StringComparison.OrdinalIgnoreCase);

        public AppReadinessDecision Evaluate(AppReadinessContext context) =>
            context.MatchResolution.SelectedCandidate is not null
                ? new(AppReadinessState.Ready,
                    "The special adapter observed its application-specific readiness signal.")
                : new(AppReadinessState.ProcessStarted,
                    "The special adapter is waiting for a safely matched window.");
    }

    private sealed class WideToleranceSpecialPlacementStrategy :
        IWindowPlacementVerificationStrategy
    {
        public string Name => "special-placement";

        public bool CanHandle(SavedWindowIdentity identity) =>
            identity.ProcessName.Equals("special", StringComparison.OrdinalIgnoreCase);

        public WindowPlacementVerificationPolicy GetPolicy(RestorePlanEntry entry) => new()
        {
            InitialDelay = TimeSpan.Zero,
            RetryDelay = TimeSpan.Zero,
            MaxRetries = 0,
            BaseTolerancePixels = 20
        };
    }
}
