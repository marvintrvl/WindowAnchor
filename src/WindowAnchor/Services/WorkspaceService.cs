using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Stages emitted while a workspace snapshot is assembled.</summary>
public enum WorkspaceCaptureProgressStage
{
    Preparing,
    DetectingResources,
    SearchingCommonFolders,
    CapturingBrowserSession,
    Finalizing
}

/// <summary>Progress update emitted while a workspace snapshot is assembled.</summary>
/// <param name="Current">1-based index of the window currently being processed (0 = pre-loop setup).</param>
/// <param name="Total">Total number of windows to process.</param>
/// <param name="AppName">Process name of the window being processed (or a stage description).</param>
/// <param name="Detail">Window title snippet or a short stage description.</param>
public record struct SaveProgressReport(
    int Current,
    int Total,
    string AppName,
    string Detail,
    WorkspaceCaptureProgressStage Stage = WorkspaceCaptureProgressStage.DetectingResources);

/// <summary>
/// Orchestrates the save and restore pipeline for workspaces.
/// Coordinates <see cref="MonitorService"/>, <see cref="WindowService"/>,
/// <see cref="StorageService"/>, and <see cref="JumpListService"/>.
/// </summary>
/// <remarks>
/// This is the primary service called by both <see cref="LayoutCoordinator"/>
/// and UI code. Storage and tray operations are dispatched to the correct contexts internally.
/// </remarks>
public class WorkspaceService : IAsyncDisposable
{
    private readonly StorageService    _storageService;
    private readonly IWindowInventory  _windowInventory;
    private readonly IWindowMutation   _windowMutation;
    private readonly IMonitorInventory _monitorInventory;
    private readonly WebAppService     _webAppService;
    private readonly IPackagedAppResolver _packagedAppResolver;
    private readonly IBrowserSessionConnector? _browserSessionConnector;
    private readonly IRestoreResourceBoundary _restoreResources;
    private readonly RestoreExecutor _restoreExecutor;
    private readonly RestoreObservationBuilder _restoreObservationBuilder;
    private readonly WorkspaceCaptureBuilder _workspaceCaptureBuilder;
    private readonly WorkspaceSnapshotBuilder _workspaceSnapshotBuilder;
    private readonly SettingsService? _settingsService;
    private readonly RestoreTransactionCoordinator _restoreTransactionCoordinator = new();

    public ValueTask DisposeAsync() => _restoreTransactionCoordinator.DisposeAsync();

    /// <summary>Creates the production workspace service using the native window service.</summary>
    public WorkspaceService(
        StorageService  storageService,
        WindowService   windowService,
        MonitorService  monitorService,
        JumpListService jumpListService,
        WebAppService?   webAppService = null,
        IBrowserSessionConnector? browserSessionConnector = null,
        IRestoreProcessLauncher? restoreProcessLauncher = null,
        IRestoreClock? restoreClock = null,
        IRestoreResourceBoundary? restoreResources = null,
        SettingsService? settingsService = null,
        IAppReadinessProbe? appReadinessProbe = null,
        AppReadinessPolicy? appReadinessPolicy = null,
        IEnumerable<IAppReadinessStrategy>? appReadinessStrategies = null,
        IPackagedAppResolver? packagedAppResolver = null,
        IWindowPlacementProbe? placementProbe = null,
        WindowPlacementVerificationPolicy? placementPolicy = null,
        IEnumerable<IWindowPlacementVerificationStrategy>? placementStrategies = null)
        : this(
            storageService,
            windowService,
            windowService,
            monitorService,
            jumpListService,
            webAppService,
            browserSessionConnector,
            restoreProcessLauncher,
            restoreClock,
            restoreResources,
            settingsService,
            appReadinessProbe,
            appReadinessPolicy,
            appReadinessStrategies,
            packagedAppResolver,
            placementProbe,
            placementPolicy,
            placementStrategies)
    {
    }

    /// <summary>
    /// Creates a workspace service with explicit inventory and mutation boundaries.
    /// This overload supports deterministic service-layer tests without native HWNDs.
    /// </summary>
    public WorkspaceService(
        StorageService     storageService,
        IWindowInventory   windowInventory,
        IWindowMutation    windowMutation,
        IMonitorInventory  monitorInventory,
        JumpListService    jumpListService,
        WebAppService?     webAppService = null,
        IBrowserSessionConnector? browserSessionConnector = null,
        IRestoreProcessLauncher? restoreProcessLauncher = null,
        IRestoreClock? restoreClock = null,
        IRestoreResourceBoundary? restoreResources = null,
        SettingsService? settingsService = null,
        IAppReadinessProbe? appReadinessProbe = null,
        AppReadinessPolicy? appReadinessPolicy = null,
        IEnumerable<IAppReadinessStrategy>? appReadinessStrategies = null,
        IPackagedAppResolver? packagedAppResolver = null,
        IWindowPlacementProbe? placementProbe = null,
        WindowPlacementVerificationPolicy? placementPolicy = null,
        IEnumerable<IWindowPlacementVerificationStrategy>? placementStrategies = null)
    {
        _storageService   = storageService;
        _windowInventory  = windowInventory;
        _windowMutation   = windowMutation;
        _monitorInventory = monitorInventory;
        _webAppService    = webAppService ?? new WebAppService();
        _packagedAppResolver = packagedAppResolver ?? new PackagedAppResolver();
        _browserSessionConnector = browserSessionConnector;
        _settingsService = settingsService;
        _restoreResources = restoreResources ?? new FileSystemRestoreResourceBoundary();
        _restoreExecutor = new RestoreExecutor(
            _windowInventory,
            _windowMutation,
            restoreProcessLauncher ?? new SystemRestoreProcessLauncher(),
            restoreClock ?? new SystemRestoreClock(),
            _restoreResources,
            _browserSessionConnector,
            appReadinessProbe ?? new SystemAppReadinessProbe(_windowInventory),
            appReadinessPolicy,
            appReadinessStrategies,
            placementProbe ?? (_windowInventory is WindowService
                ? new SystemWindowPlacementProbe()
                : new InventoryWindowPlacementProbe(_windowInventory)),
            placementPolicy,
            placementStrategies);
        _restoreObservationBuilder = new RestoreObservationBuilder(
            _windowInventory,
            _monitorInventory,
            _restoreResources,
            _packagedAppResolver,
            _webAppService,
            _settingsService,
            _browserSessionConnector);
        var captureResourceResolver = new CaptureResourceResolver(jumpListService);
        var capturedWindowEntryFactory = new CapturedWindowEntryFactory(
            _webAppService,
            captureResourceResolver);
        _workspaceSnapshotBuilder = new WorkspaceSnapshotBuilder(
            _windowInventory,
            _monitorInventory,
            captureResourceResolver,
            capturedWindowEntryFactory);
        _workspaceCaptureBuilder = new WorkspaceCaptureBuilder(_browserSessionConnector);
    }

    // ── Storage proxies ────────────────────────────────────────────

    public void SetLastKnownFingerprint(string fp)        => _storageService.SetLastKnownFingerprint(fp);
    public List<WorkspaceSnapshot> GetAllWorkspaces()     => _storageService.LoadAllWorkspaces();

    /// <summary>
    /// Returns the most-recently saved workspace whose
    /// <see cref="WorkspaceSnapshot.MonitorFingerprint"/> matches <paramref name="fingerprint"/>,
    /// or <c>null</c> when no match exists.
    /// </summary>
    public WorkspaceSnapshot? FindWorkspaceByFingerprint(string fingerprint)
        => _storageService.LoadAllWorkspaces()
            .Where(w => w.MonitorFingerprint == fingerprint)
            .OrderByDescending(w => w.SavedAt)
            .FirstOrDefault();

    /// <summary>Returns the current monitor fingerprint (hash of the connected display configuration).</summary>
    public string GetCurrentMonitorFingerprint() => _monitorInventory.GetCurrentMonitorFingerprint();

    /// <summary>
    /// Returns current monitors paired with all visible windows on each monitor.
    /// Used by the Save Workspace dialog to show a per-window checkbox list.
    /// </summary>
    public List<(MonitorInfo Monitor, List<WindowRecord> Windows)> GetWindowPreviewForDialog()
    {
        var monitors = _monitorInventory.GetCurrentMonitors();
        var windows  = _windowInventory.SnapshotWindows(
                WindowCandidatePolicy.CaptureCandidate,
                monitors)
            .Where(w => !w.ProcessName.Equals("WindowAnchor", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Resolve a friendly display name so the dialog shows "Insilico Terminal" rather than
        // "brave" for installed web apps. Everything else keeps its process name.
        foreach (var w in windows)
            w.DisplayName = _webAppService.ResolveWebAppName(w.ProcessName, w.AppUserModelId)
                            ?? w.ProcessName;

        return monitors
            .Select(m => (m, windows.Where(w => w.MonitorId == m.MonitorId).ToList()))
            .ToList();
    }

    // ── Capture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Assembles window and optional browser data into one complete result without writing it.
    /// Call <see cref="PersistCapture"/> once after applying an explicit partial-capture policy.
    /// </summary>
    public async Task<WorkspaceCaptureResult> CaptureWorkspaceAsync(
        string name,
        bool saveFiles = true,
        HashSet<string>? monitorIds = null,
        IProgress<SaveProgressReport>? progress = null,
        List<WindowRecord>? selectedWindows = null,
        bool captureBrowserSessions = true,
        CancellationToken cancellationToken = default,
        bool searchCommonFolders = true,
        TimeSpan? commonFolderSearchBudget = null,
        bool buildFullJumpListCache = true)
        => await _workspaceCaptureBuilder.CaptureAsync(
            new WorkspaceCaptureRequest(
                name,
                saveFiles,
                monitorIds,
                progress,
                selectedWindows,
                captureBrowserSessions,
                searchCommonFolders,
                commonFolderSearchBudget,
                cancellationToken,
                buildFullJumpListCache),
            _workspaceSnapshotBuilder.Build).ConfigureAwait(false);

    /// <summary>Persists one already-complete capture into the requested typed repository.</summary>
    public void PersistCapture(
        WorkspaceCaptureResult capture,
        WorkspaceArtifactKind destination,
        IncompleteBrowserCapturePolicy incompleteBrowserPolicy)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (!capture.BrowserCapture.IsComplete &&
            incompleteBrowserPolicy == IncompleteBrowserCapturePolicy.RequireCompleteBrowserCapture)
        {
            throw new InvalidOperationException(
                $"Browser capture did not complete ({capture.BrowserCapture.Status}); persistence was not allowed by policy.");
        }

        switch (destination)
        {
            case WorkspaceArtifactKind.NamedWorkspace:
                _storageService.NamedWorkspaces.Save(capture.Snapshot);
                break;
            case WorkspaceArtifactKind.Checkpoint:
                _storageService.Checkpoints.Save(capture.Snapshot);
                break;
            case WorkspaceArtifactKind.TemporaryCapture:
                _storageService.TemporaryCaptures.Save(capture.Snapshot);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(destination), destination, null);
        }

        AppLogger.Info(
            "workspace.capture_persisted",
            "Persisted a workspace capture",
            LogField.Workspace("workspaceName", capture.Snapshot.Name),
            LogField.Public("artifactKind", destination),
            LogField.Public("browserCaptureStatus", capture.BrowserCapture.Status));
    }

    // ── Restore ──────────────────────────────────────────────────────────────

    /// <summary>Whether manual restore entry points should show the review dialog by default.</summary>
    public bool RestorePreviewEnabled => _settingsService?.Settings.ShowRestorePreview ?? true;

    /// <summary>Whether restore mutations should capture a new recovery checkpoint first.</summary>
    public bool RestoreCheckpointsEnabled =>
        _settingsService?.Settings.CreateRestoreCheckpoints ?? true;

    /// <summary>
    /// Observes the current environment and builds a reviewable plan. Discovery is read-only;
    /// process, browser, persistence, and native window mutation APIs are not called.
    /// </summary>
    public RestorePlan CreateRestorePlan(WorkspaceSnapshot snapshot, RestoreMode mode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(mode);
        Dictionary<IntPtr, (uint Pid, WindowRecord Record)> liveWindows =
            _windowInventory.GetWindowsWithPids(WindowCandidatePolicy.RestoreMatchCandidate);
        return CreateRestorePlan(snapshot, mode, liveWindows);
    }

    /// <summary>Builds and executes a plan while returning structured stale-plan outcomes.</summary>
    public async Task<RestoreExecutionResult> RestoreWorkspaceWithExecutionResultAsync(
        WorkspaceSnapshot snapshot,
        RestoreMode mode,
        CancellationToken ct = default,
        IProgress<RestoreProgressReport>? progress = null)
    {
        RestorePlan plan = CreateRestorePlan(snapshot, mode);
        return await ExecuteApprovedRestorePlanAsync(snapshot, plan, ct, progress).ConfigureAwait(false);
    }

    /// <summary>Runs an automatic restore with an explicit checkpoint trigger classification.</summary>
    internal async Task<RestoreExecutionResult> RestoreWorkspaceWithExecutionResultAsync(
        WorkspaceSnapshot snapshot,
        RestoreMode mode,
        WorkspaceCheckpointTrigger checkpointTrigger,
        CancellationToken ct)
    {
        RestorePlan plan = CreateRestorePlan(snapshot, mode);
        return await ExecuteApprovedRestorePlanTransactionalAsync(
            snapshot,
            plan,
            checkpointTrigger,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes an already-reviewed plan for its source snapshot without rebuilding or silently
    /// replacing the approved matches and actions.
    /// </summary>
    public async Task<RestoreExecutionResult> ExecuteApprovedRestorePlanAsync(
        WorkspaceSnapshot snapshot,
        RestorePlan approvedPlan,
        CancellationToken ct = default,
        IProgress<RestoreProgressReport>? progress = null) => await ExecuteApprovedRestorePlanTransactionalAsync(
            snapshot,
            approvedPlan,
            DetermineCheckpointTrigger(approvedPlan),
            ct,
            progress).ConfigureAwait(false);

    /// <summary>True when at least one healthy, non-expired checkpoint can be restored.</summary>
    public bool CanUndoLastRestore => _storageService.Checkpoints.GetLatest() is not null;

    /// <summary>Returns the newest healthy checkpoint for the UI-level undo reconciliation.</summary>
    internal WorkspaceSnapshot? GetLatestRestoreCheckpoint() =>
        _storageService.Checkpoints.GetLatest();

    /// <summary>
    /// Restores the newest healthy checkpoint through the normal planner. The transactional
    /// execution creates a new safety checkpoint first, making an undo itself undoable.
    /// </summary>
    public async Task<RestoreExecutionResult?> UndoLastRestoreAsync(
        CancellationToken ct = default,
        IProgress<RestoreProgressReport>? progress = null)
    {
        WorkspaceSnapshot? checkpoint = _storageService.Checkpoints.GetLatest();
        if (checkpoint is null)
            return null;

        RestorePlan plan = CreateRestorePlan(checkpoint, RestoreMode.Standard);
        return await ExecuteApprovedRestorePlanTransactionalAsync(
            checkpoint,
            plan,
            WorkspaceCheckpointTrigger.Undo,
            ct,
            progress).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs an operation behind the single restore transaction gate. When checkpoints are enabled,
    /// the callback is not invoked unless the pre-restore snapshot was committed atomically.
    /// </summary>
    internal async Task<CheckpointedOperationResult<T>> ExecuteCheckpointedOperationAsync<T>(
        WorkspaceSnapshot targetSnapshot,
        WorkspaceCheckpointTrigger trigger,
        Func<RestoreCheckpointOutcome, CancellationToken, Task<T>> operation,
        CancellationToken ct,
        IProgress<RestoreProgressReport>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(targetSnapshot);
        ArgumentNullException.ThrowIfNull(operation);
        return await _restoreTransactionCoordinator.ExecuteAsync(
            trigger,
            (token, operationProgress) => RestoreCheckpointsEnabled
                ? CreatePreRestoreCheckpointAsync(
                    targetSnapshot,
                    trigger,
                    token,
                    operationProgress)
                : SkipPreRestoreCheckpointAsync(trigger),
            operation,
            ct,
            progress).ConfigureAwait(false);
    }

    internal RestoreExecutionResult CreateCheckpointAbortedResult(
        RestorePlan plan,
        RestoreCheckpointOutcome checkpoint)
    {
        bool cancelled = checkpoint.Status == RestoreCheckpointStatus.Cancelled;
        RestoreExecutionEntryResult[] entries = plan.Entries.Select(entry =>
            new RestoreExecutionEntryResult(
                entry.EntryIndex,
                entry.EntryId,
                entry.Outcome == RestorePlanEntryOutcome.Excluded
                    ? RestoreExecutionEntryStatus.Excluded
                    : entry.Outcome == RestorePlanEntryOutcome.Blocked
                        ? RestoreExecutionEntryStatus.Blocked
                        : cancelled
                            ? RestoreExecutionEntryStatus.Cancelled
                            : RestoreExecutionEntryStatus.Failed,
                null,
                checkpoint.Explanation)).ToArray();
        RestoreExecutionActionResult[] actions = plan.Actions.Select((action, index) =>
            new RestoreExecutionActionResult(
                index,
                action.EntryIndex,
                action.Kind,
                cancelled
                    ? RestoreExecutionActionStatus.Cancelled
                    : RestoreExecutionActionStatus.Skipped,
                null,
                action.WindowHandle,
                checkpoint.Explanation)).ToArray();
        return new RestoreExecutionResult(
            plan.WorkspaceId,
            cancelled ? RestoreExecutionStatus.Cancelled : RestoreExecutionStatus.Rejected,
            cancelled,
            entries,
            actions,
            new HashSet<long>())
        {
            Checkpoint = checkpoint
        };
    }

    private async Task<RestoreExecutionResult> ExecuteApprovedRestorePlanTransactionalAsync(
        WorkspaceSnapshot snapshot,
        RestorePlan approvedPlan,
        WorkspaceCheckpointTrigger trigger,
        CancellationToken ct,
        IProgress<RestoreProgressReport>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(approvedPlan);
        if (!string.Equals(
                snapshot.WorkspaceId,
                approvedPlan.WorkspaceId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The approved restore plan belongs to a different workspace snapshot.",
                nameof(approvedPlan));
        }

        // A blocked, cancelled, or empty plan cannot mutate the desktop and therefore should not
        // consume checkpoint history. The executor still owns its structured rejection result.
        if (!approvedPlan.CanExecute || approvedPlan.Actions.Count == 0)
            return await ExecuteApprovedRestorePlanCoreAsync(snapshot, approvedPlan, ct, progress)
                .ConfigureAwait(false);

        CheckpointedOperationResult<RestoreExecutionResult> transaction =
            await ExecuteCheckpointedOperationAsync(
                snapshot,
                trigger,
                async (checkpoint, token) =>
                {
                    RestoreExecutionResult execution = await ExecuteApprovedRestorePlanCoreAsync(
                        snapshot,
                        approvedPlan,
                        token,
                        progress).ConfigureAwait(false);
                    return execution with { Checkpoint = checkpoint };
                },
                ct,
                progress).ConfigureAwait(false);

        return transaction.OperationStarted && transaction.Value is not null
            ? transaction.Value
            : CreateCheckpointAbortedResult(approvedPlan, transaction.Checkpoint);
    }

    internal async Task<RestoreExecutionResult> ExecuteApprovedRestorePlanAfterCheckpointAsync(
        WorkspaceSnapshot snapshot,
        RestorePlan approvedPlan,
        RestoreCheckpointOutcome checkpoint,
        CancellationToken ct,
        IProgress<RestoreProgressReport>? progress = null)
    {
        if (!checkpoint.AllowsOperation)
            return CreateCheckpointAbortedResult(approvedPlan, checkpoint);
        RestoreExecutionResult result = await ExecuteApprovedRestorePlanCoreAsync(
            snapshot,
            approvedPlan,
            ct,
            progress).ConfigureAwait(false);
        return result with { Checkpoint = checkpoint };
    }

    private async Task<RestoreExecutionResult> ExecuteApprovedRestorePlanCoreAsync(
        WorkspaceSnapshot snapshot,
        RestorePlan approvedPlan,
        CancellationToken ct,
        IProgress<RestoreProgressReport>? progress)
    {
        Stopwatch restoreTimer = Stopwatch.StartNew();
        AppLogger.Info(
            "restore.session_started",
            "Started an approved workspace restore session",
            LogField.Identifier("workspaceId", snapshot.WorkspaceId),
            LogField.Workspace("workspaceName", snapshot.Name),
            LogField.Public("entryCount", snapshot.Entries.Count),
            LogField.Public("mode", approvedPlan.Mode),
            LogField.Public("disabledEntryCount", approvedPlan.DisabledEntryIndexes.Count));

        RestoreExecutionResult result = await _restoreExecutor
            .ExecuteAsync(approvedPlan, ct, progress)
            .ConfigureAwait(false);
        ApplyExecutionState(snapshot, result);
        restoreTimer.Stop();

        AppLogger.Info(
            "restore.session_completed",
            "Completed an approved workspace restore session",
            LogField.Identifier("workspaceId", snapshot.WorkspaceId),
            LogField.Public("status", result.Status),
            LogField.Public("stalePlan", result.HasStalePlan),
            LogField.Public("assignedWindowCount", result.AssignedWindowHandles.Count),
            LogField.Public("durationMs", restoreTimer.Elapsed.TotalMilliseconds));
        return result;
    }

    private async Task<RestoreCheckpointOutcome> CreatePreRestoreCheckpointAsync(
        WorkspaceSnapshot targetSnapshot,
        WorkspaceCheckpointTrigger trigger,
        CancellationToken ct,
        IProgress<RestoreProgressReport>? progress)
    {
        Stopwatch checkpointTimer = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();
            string name = $"Recovery before {trigger}: {targetSnapshot.Name}";
            progress?.Report(new RestoreProgressReport(
                RestoreProgressStage.PreparingCheckpoint,
                "Creating recovery checkpoint",
                "Capturing the current desktop before making changes.",
                Elapsed: checkpointTimer.Elapsed));
            IProgress<SaveProgressReport>? captureProgress = progress is null
                ? null
                : new CallbackProgress<SaveProgressReport>(report =>
                    progress.Report(MapCheckpointProgress(report, checkpointTimer.Elapsed)));
            WorkspaceCaptureResult capture = await CaptureWorkspaceAsync(
                name,
                saveFiles: true,
                captureBrowserSessions: true,
                progress: captureProgress,
                cancellationToken: ct,
                searchCommonFolders: false,
                buildFullJumpListCache: false).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            string targetWorkspaceId = Guid.TryParse(targetSnapshot.WorkspaceId, out _)
                ? targetSnapshot.WorkspaceId
                : "";
            progress?.Report(new RestoreProgressReport(
                RestoreProgressStage.SavingCheckpoint,
                "Saving recovery checkpoint",
                "Writing the checkpoint atomically.",
                capture.Snapshot.Entries.Count,
                capture.Snapshot.Entries.Count,
                checkpointTimer.Elapsed));
            CheckpointSaveReceipt receipt = _storageService.Checkpoints.Save(
                capture.Snapshot,
                trigger,
                targetWorkspaceId);
            checkpointTimer.Stop();
            AppLogger.Info(
                "checkpoint.pre_restore_created",
                "Created a durable pre-restore checkpoint",
                LogField.Identifier("checkpointId", receipt.CheckpointId),
                LogField.Identifier("targetWorkspaceId", targetWorkspaceId),
                LogField.Public("trigger", trigger),
                LogField.Public("entryCount", capture.Snapshot.Entries.Count),
                LogField.Public("durationMs", checkpointTimer.Elapsed.TotalMilliseconds),
                LogField.Public("recursiveFileSearch", false),
                LogField.Public("jumpListSearch", false));
            return new RestoreCheckpointOutcome(
                RestoreCheckpointStatus.Created,
                trigger,
                receipt.CheckpointId,
                receipt.CreatedAtUtc,
                "A recovery checkpoint was created before desktop mutation.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            checkpointTimer.Stop();
            return RestoreTransactionCoordinator.CancelledCheckpoint(trigger);
        }
        catch (Exception ex)
        {
            checkpointTimer.Stop();
            AppLogger.Error(
                "checkpoint.pre_restore_failed",
                "Could not create the required pre-restore checkpoint; desktop mutation was blocked",
                ex,
                LogField.Identifier("targetWorkspaceId", targetSnapshot.WorkspaceId),
                LogField.Public("trigger", trigger),
                LogField.Public("durationMs", checkpointTimer.Elapsed.TotalMilliseconds),
                LogField.Public("errorCategory", "checkpoint_creation"));
            return new RestoreCheckpointOutcome(
                RestoreCheckpointStatus.Failed,
                trigger,
                null,
                null,
                "The recovery checkpoint could not be saved, so no restore action was applied.");
        }
    }

    private static Task<RestoreCheckpointOutcome> SkipPreRestoreCheckpointAsync(
        WorkspaceCheckpointTrigger trigger)
    {
        AppLogger.Info(
            "checkpoint.pre_restore_disabled",
            "Skipped the pre-restore checkpoint because it is disabled in settings",
            LogField.Public("trigger", trigger));
        return Task.FromResult(RestoreTransactionCoordinator.DisabledCheckpoint(trigger));
    }

    private static WorkspaceCheckpointTrigger DetermineCheckpointTrigger(RestorePlan plan)
    {
        if (plan.Mode == RestoreModeKind.AlignAndMinimize)
            return WorkspaceCheckpointTrigger.AlignAndMinimize;
        if (plan.Mode == RestoreModeKind.Selective)
            return WorkspaceCheckpointTrigger.SelectiveRestore;
        if (plan.Actions.Any(action => action.TargetPlacement is { Strategy: not RestorePlacementStrategy.ExactPixels }))
            return WorkspaceCheckpointTrigger.AdaptiveRestore;
        return WorkspaceCheckpointTrigger.Restore;
    }

    private static RestoreProgressReport MapCheckpointProgress(
        SaveProgressReport report,
        TimeSpan elapsed)
    {
        RestoreProgressStage stage = report.Stage switch
        {
            WorkspaceCaptureProgressStage.DetectingResources or
            WorkspaceCaptureProgressStage.SearchingCommonFolders =>
                RestoreProgressStage.DetectingResources,
            WorkspaceCaptureProgressStage.CapturingBrowserSession =>
                RestoreProgressStage.CapturingBrowserSession,
            WorkspaceCaptureProgressStage.Finalizing =>
                RestoreProgressStage.SavingCheckpoint,
            _ => RestoreProgressStage.PreparingCheckpoint
        };
        string message = report.Stage switch
        {
            WorkspaceCaptureProgressStage.DetectingResources =>
                $"Identifying recovery resources for {report.AppName}",
            WorkspaceCaptureProgressStage.SearchingCommonFolders => report.AppName,
            WorkspaceCaptureProgressStage.CapturingBrowserSession => report.AppName,
            WorkspaceCaptureProgressStage.Finalizing => "Finalizing recovery checkpoint",
            _ => "Preparing recovery checkpoint"
        };
        return new RestoreProgressReport(
            stage,
            message,
            report.Detail,
            report.Current,
            report.Total,
            elapsed);
    }

    private RestorePlan CreateRestorePlan(
        WorkspaceSnapshot snapshot,
        RestoreMode mode,
        IReadOnlyDictionary<IntPtr, (uint Pid, WindowRecord Record)> liveWindows)
    {
        RestoreObservation observation = _restoreObservationBuilder.Build(snapshot, liveWindows);
        return RestorePlanner.Build(
            snapshot,
            observation.Inventory,
            observation.Topology,
            mode);
    }

    /// <summary>Persists a user-confirmed composite identity for a stable workspace entry.</summary>
    public void RememberWindowMatch(
        string workspaceId,
        string entryId,
        WindowIdentityHint identity) =>
        (_settingsService ?? throw new InvalidOperationException(
            "Learned window matching is unavailable without application settings."))
        .RememberWindowMatch(workspaceId, entryId, identity);

    private static void ApplyExecutionState(
        WorkspaceSnapshot snapshot,
        RestoreExecutionResult result)
    {
        foreach (WorkspaceEntry entry in snapshot.Entries)
            entry.WasRestored = false;
        foreach (RestoreExecutionEntryResult entry in result.Entries)
        {
            if (entry.EntryIndex >= 0 && entry.EntryIndex < snapshot.Entries.Count)
                snapshot.Entries[entry.EntryIndex].WasRestored =
                    entry.Status == RestoreExecutionEntryStatus.Restored;
        }
    }


    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        private readonly Action<T> _callback = callback ?? throw new ArgumentNullException(nameof(callback));

        public void Report(T value) => _callback(value);
    }

}

