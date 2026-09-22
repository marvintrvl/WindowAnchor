using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Executes an approved restore through fixed, sequential phases. Every HWND and launch resource
/// is revalidated immediately before mutation; no phase owns a second assignment cache.
/// </summary>
public sealed class RestoreExecutor
{
    private readonly RestorePreflightPhase _preflight;
    private readonly RestoreBrowserAndLaunchPhase _browserAndLaunch;
    private readonly RestoreReadinessPhase _readiness;
    private readonly RestorePlacementVerificationPhase _placementVerification;

    public RestoreExecutor(
        IWindowInventory windowInventory,
        IWindowMutation windowMutation,
        IRestoreProcessLauncher processLauncher,
        IRestoreClock clock,
        IRestoreResourceBoundary resources,
        IBrowserSessionConnector? browserConnector = null,
        IAppReadinessProbe? readinessProbe = null,
        AppReadinessPolicy? readinessPolicy = null,
        IEnumerable<IAppReadinessStrategy>? readinessStrategies = null,
        IWindowPlacementProbe? placementProbe = null,
        WindowPlacementVerificationPolicy? placementPolicy = null,
        IEnumerable<IWindowPlacementVerificationStrategy>? placementStrategies = null)
    {
        ArgumentNullException.ThrowIfNull(windowInventory);
        ArgumentNullException.ThrowIfNull(windowMutation);
        ArgumentNullException.ThrowIfNull(processLauncher);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(resources);

        var revalidator = new RestoreWindowRevalidator(windowInventory);
        var readinessEngine = new AppReadinessEngine(readinessPolicy, readinessStrategies);
        var resolvedReadinessProbe = readinessProbe ?? new SystemAppReadinessProbe(windowInventory);
        var resolvedPlacementProbe = placementProbe ?? new InventoryWindowPlacementProbe(windowInventory);
        var placementStrategyRegistry = new WindowPlacementVerificationStrategyRegistry(
            placementPolicy,
            placementStrategies);

        _preflight = new RestorePreflightPhase(
            windowInventory,
            resources,
            browserConnector,
            revalidator);
        _browserAndLaunch = new RestoreBrowserAndLaunchPhase(
            windowMutation,
            processLauncher,
            resources,
            browserConnector,
            revalidator);
        _readiness = new RestoreReadinessPhase(
            windowMutation,
            clock,
            resolvedReadinessProbe,
            readinessEngine,
            revalidator);
        _placementVerification = new RestorePlacementVerificationPhase(
            windowMutation,
            clock,
            resolvedPlacementProbe,
            placementStrategyRegistry,
            revalidator);
    }

    /// <summary>Executes an approved plan through injectable process, browser, window, and clock boundaries.</summary>
    public async Task<RestoreExecutionResult> ExecuteAsync(
        RestorePlan plan,
        CancellationToken cancellationToken = default,
        IProgress<RestoreProgressReport>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var context = new RestoreExecutionContext(plan);

        if (plan.WasCancelled)
        {
            RestoreExecutionSupport.MarkRemaining(
                context.IndexedActions,
                context.Results,
                RestoreExecutionActionStatus.Cancelled,
                "The approved plan was already cancelled.");
            return RestoreResultAggregator.Complete(
                context,
                RestoreExecutionStatus.Cancelled,
                wasCancelled: true);
        }

        if (plan.Mode == RestoreModeKind.PreviewOnly)
        {
            RestoreExecutionSupport.MarkRemaining(
                context.IndexedActions,
                context.Results,
                RestoreExecutionActionStatus.Skipped,
                "Preview-only plans are intentionally non-mutating.");
            return RestoreResultAggregator.Complete(
                context,
                RestoreExecutionStatus.Rejected,
                wasCancelled: false);
        }

        if (plan.BlockingErrors.Count > 0)
        {
            RestoreExecutionSupport.MarkRemaining(
                context.IndexedActions,
                context.Results,
                RestoreExecutionActionStatus.Skipped,
                "The approved plan contains blocking errors.");
            return RestoreResultAggregator.Complete(
                context,
                RestoreExecutionStatus.Rejected,
                wasCancelled: false);
        }

        RestoreExecutionResult? stalePreview;
        using (context.Timing.Measure(RestoreProgressStage.DetectingResources))
            stalePreview = _preflight.Execute(context);
        if (stalePreview is not null)
            return stalePreview;

        using (context.Timing.Measure(RestoreProgressStage.CapturingBrowserSession))
        {
            await _browserAndLaunch.RestoreBrowserSessionsAsync(
                context,
                cancellationToken,
                progress).ConfigureAwait(false);
        }

        // Preserve the established cancellation boundary: already-running windows are reconciled
        // before cancellation prevents launch and readiness work.
        using (context.Timing.Measure(RestoreProgressStage.LaunchingApplications))
            _browserAndLaunch.RestoreExistingWindows(context);

        if (cancellationToken.IsCancellationRequested)
        {
            RestoreExecutionSupport.MarkRemaining(
                context.IndexedActions,
                context.Results,
                RestoreExecutionActionStatus.Cancelled,
                "Cancellation was observed after initial window reconciliation.");
            return RestoreResultAggregator.Complete(
                context,
                RestoreExecutionStatus.Cancelled,
                wasCancelled: true);
        }

        using (context.Timing.Measure(RestoreProgressStage.LaunchingApplications))
            _browserAndLaunch.LaunchApplications(context, progress);

        bool readinessComplete;
        using (context.Timing.Measure(RestoreProgressStage.WaitingForApplications))
        {
            readinessComplete = await _readiness.ExecuteAsync(
                context,
                cancellationToken,
                progress).ConfigureAwait(false);
        }
        if (!readinessComplete)
        {
            RestoreExecutionSupport.MarkRemaining(
                context.IndexedActions,
                context.Results,
                RestoreExecutionActionStatus.Cancelled,
                "Cancellation interrupted application readiness polling.");
            return RestoreResultAggregator.Complete(
                context,
                RestoreExecutionStatus.Cancelled,
                wasCancelled: true);
        }

        bool placementComplete;
        using (context.Timing.Measure(RestoreProgressStage.VerifyingPlacements))
        {
            placementComplete = await _placementVerification.ExecuteAsync(
                context,
                cancellationToken,
                progress).ConfigureAwait(false);
        }
        if (!placementComplete)
        {
            RestoreExecutionSupport.MarkRemaining(
                context.IndexedActions,
                context.Results,
                RestoreExecutionActionStatus.Cancelled,
                "Cancellation interrupted post-restore placement verification.");
            return RestoreResultAggregator.Complete(
                context,
                RestoreExecutionStatus.Cancelled,
                wasCancelled: true);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            RestoreExecutionSupport.MarkRemaining(
                context.IndexedActions,
                context.Results,
                RestoreExecutionActionStatus.Cancelled,
                "Cancellation was observed before final minimization.");
            return RestoreResultAggregator.Complete(
                context,
                RestoreExecutionStatus.Cancelled,
                wasCancelled: true);
        }

        _browserAndLaunch.MinimizeOtherWindows(context);
        RestoreExecutionSupport.MarkRemaining(
            context.IndexedActions,
            context.Results,
            RestoreExecutionActionStatus.Skipped,
            "The action required no additional execution work.");
        return RestoreResultAggregator.Complete(
            context,
            RestoreResultAggregator.DetermineStatus(context),
            wasCancelled: false);
    }
}
