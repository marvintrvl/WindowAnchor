using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.UI;

/// <summary>Shared manual preview/approval workflow used by tray and settings commands.</summary>
internal static class RestorePlanPreviewWorkflow
{
    internal static Task<RestoreExecutionResult?> RunWorkspaceDefaultAsync(
        LayoutCoordinator coordinator,
        WorkspaceSnapshot snapshot,
        Window? owner = null,
        CancellationToken cancellationToken = default)
    {
        RestoreMode mode = RestoreMode.FromWorkspace(snapshot);
        return mode.Kind == RestoreModeKind.ExactSwitch
            ? RunSwitchAsync(coordinator, snapshot, owner, cancellationToken)
            : RunAsync(coordinator, snapshot, mode, owner, cancellationToken);
    }

    internal static async Task<RestoreExecutionResult?> RunUndoAsync(
        LayoutCoordinator coordinator,
        Window? owner = null,
        CancellationToken cancellationToken = default)
    {
        RestoreExecutionResult? result = await RunWithProgressAsync(
            "previous desktop",
            isSwitch: false,
            owner,
            cancellationToken,
            (token, progress) => coordinator.UndoLastRestoreAsync(token, progress));
        ShowLatestDiagnosticsWhenNeeded(result, "Undo Needs Attention", owner);
        return result;
    }

    internal static async Task<RestoreExecutionResult?> RunSwitchAsync(
        LayoutCoordinator coordinator,
        WorkspaceSnapshot snapshot,
        Window? owner = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            RestorePlan preview = coordinator.CreateRestorePlan(snapshot, RestoreMode.ExactSwitch);
            RestorePlan approvedPlan = preview;
            System.Collections.Generic.IReadOnlyList<WindowMatchHint> approvedHints =
                Array.Empty<WindowMatchHint>();
            if (RestorePreviewPolicy.ShouldShow(preview, coordinator.RestorePreviewEnabled))
            {
                Stopwatch preparationTimer = Stopwatch.StartNew();
                var dialog = new RestorePlanPreviewDialog(preview, isWorkspaceSwitch: true);
                preparationTimer.Stop();
                if (owner is not null)
                    dialog.Owner = owner;
                if (dialog.ShowDialog() != true || dialog.ApprovedPlan is null)
                    return null;
                approvedPlan = dialog.ApprovedPlan with
                {
                    PreviewPreparationDuration = preview.PreviewPreparationDuration +
                        preparationTimer.Elapsed
                };
                approvedHints = dialog.ApprovedMatchHints;
            }

            RestoreExecutionResult? result = await RunWithProgressAsync(
                snapshot.Name,
                isSwitch: true,
                owner,
                cancellationToken,
                (token, progress) => coordinator.SwitchWorkspaceAsync(
                    snapshot,
                    approvedPlan,
                    token,
                    progress));
            if (result?.Status == RestoreExecutionStatus.Completed)
                RememberApprovedMatches(coordinator, approvedHints);
            ShowDiagnosticsWhenNeeded(result, approvedPlan, "Switch Needs Attention", owner);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "switch.preview_failed",
                "The workspace switch preview workflow failed",
                ex,
                LogField.Identifier("workspaceId", snapshot.WorkspaceId),
                LogField.Public("errorCategory", "switch_preview"));
            ShowMessage(
                owner,
                "WindowAnchor could not prepare or execute the workspace switch.",
                "Switch Workspace",
                MessageBoxImage.Warning);
            return null;
        }
    }

    internal static async Task<RestoreExecutionResult?> RunAsync(
        LayoutCoordinator coordinator,
        WorkspaceSnapshot snapshot,
        RestoreMode mode,
        Window? owner = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(mode);

        try
        {
            RestorePlan preview = coordinator.CreateRestorePlan(snapshot, mode);
            RestorePlan approvedPlan = preview;
            System.Collections.Generic.IReadOnlyList<WindowMatchHint> approvedHints =
                Array.Empty<WindowMatchHint>();
            bool previewOnly = mode.Kind == RestoreModeKind.PreviewOnly;
            if (previewOnly || RestorePreviewPolicy.ShouldShow(preview, coordinator.RestorePreviewEnabled))
            {
                Stopwatch preparationTimer = Stopwatch.StartNew();
                var dialog = new RestorePlanPreviewDialog(preview);
                preparationTimer.Stop();
                if (owner is not null)
                    dialog.Owner = owner;
                bool approved = dialog.ShowDialog() == true && dialog.ApprovedPlan is not null;
                if (previewOnly)
                    return null;
                if (!approved)
                    return null;
                approvedPlan = dialog.ApprovedPlan! with
                {
                    PreviewPreparationDuration = preview.PreviewPreparationDuration +
                        preparationTimer.Elapsed
                };
                approvedHints = dialog.ApprovedMatchHints;
            }

            RestoreExecutionResult result = await RunWithProgressAsync(
                snapshot.Name,
                isSwitch: false,
                owner,
                cancellationToken,
                (token, progress) => coordinator.RestoreApprovedPlanAsync(
                    snapshot,
                    approvedPlan,
                    token,
                    progress));
            if (result.Status == RestoreExecutionStatus.Completed)
                RememberApprovedMatches(coordinator, approvedHints);
            ShowDiagnosticsWhenNeeded(result, approvedPlan, "Restore Needs Attention", owner);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "restore.preview_failed",
                "The restore preview workflow failed",
                ex,
                LogField.Identifier("workspaceId", snapshot.WorkspaceId),
                LogField.Public("errorCategory", "restore_preview"));
            ShowMessage(
                owner,
                "WindowAnchor could not prepare or execute the restore preview.",
                "Restore Preview",
                MessageBoxImage.Warning);
            return null;
        }
    }

    private static void RememberApprovedMatches(
        LayoutCoordinator coordinator,
        System.Collections.Generic.IReadOnlyList<WindowMatchHint> hints)
    {
        foreach (WindowMatchHint hint in hints)
        {
            coordinator.RememberWindowMatch(
                hint.WorkspaceId,
                hint.EntryId,
                hint.Identity);
        }
    }

    private static async Task<T> RunWithProgressAsync<T>(
        string workspaceName,
        bool isSwitch,
        Window? owner,
        CancellationToken cancellationToken,
        Func<CancellationToken, IProgress<RestoreProgressReport>, Task<T>> operation)
    {
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progressWindow = new RestoreProgressWindow(workspaceName, isSwitch, owner);
        progressWindow.CancelRequested += (_, _) => linkedCancellation.Cancel();
        var progress = new Progress<RestoreProgressReport>(progressWindow.ApplyReport);
        progressWindow.Show();
        try
        {
            return await operation(linkedCancellation.Token, progress);
        }
        finally
        {
            progressWindow.CompleteAndClose();
        }
    }

    private static void ShowDiagnosticsWhenNeeded(
        RestoreExecutionResult? result,
        RestorePlan plan,
        string title,
        Window? owner)
    {
        if (result is null || !RestoreDiagnosticsPresentationPolicy.ShouldShowFailureSummary(result))
            return;
        RestoreDiagnosticsReport report = RestoreDiagnosticsReportBuilder.Build(plan, result);
        RestoreDiagnosticsReportStore.Record(report);
        new RestoreDiagnosticsDialog(report, title, owner).ShowDialog();
    }

    private static void ShowLatestDiagnosticsWhenNeeded(
        RestoreExecutionResult? result,
        string title,
        Window? owner)
    {
        if (!RestoreDiagnosticsPresentationPolicy.ShouldShowFailureSummary(result) ||
            !RestoreDiagnosticsReportStore.TryGetLatest(out RestoreDiagnosticsReport? report) ||
            report is null)
        {
            return;
        }
        new RestoreDiagnosticsDialog(report, title, owner).ShowDialog();
    }

    private static void ShowMessage(
        Window? owner,
        string message,
        string title,
        MessageBoxImage image)
    {
        if (owner is null)
            System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, image);
        else
            System.Windows.MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
    }
}
