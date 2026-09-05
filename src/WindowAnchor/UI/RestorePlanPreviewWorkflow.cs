using System;
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
    internal static async Task RunDirectAsync(
        LayoutCoordinator coordinator,
        WorkspaceSnapshot snapshot,
        RestoreMode mode,
        Window? owner = null,
        CancellationToken cancellationToken = default)
    {
        await RunWithProgressAsync(
            snapshot.Name,
            isSwitch: false,
            owner,
            cancellationToken,
            async (token, progress) =>
            {
                if (mode.Kind == RestoreModeKind.AlignAndMinimize)
                    await coordinator.AlignAndMinimizeOthersAsync(snapshot, token, progress);
                else
                    await coordinator.RestoreWorkspaceAsync(snapshot, token, progress);
                return true;
            });
    }

    internal static Task<RestoreExecutionResult?> RunUndoAsync(
        LayoutCoordinator coordinator,
        Window? owner = null,
        CancellationToken cancellationToken = default) =>
        RunWithProgressAsync(
            "previous desktop",
            isSwitch: false,
            owner,
            cancellationToken,
            (token, progress) => coordinator.UndoLastRestoreAsync(token, progress));

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
            RestorePlan preview = coordinator.CreateRestorePlan(snapshot, RestoreMode.Standard);
            var dialog = new RestorePlanPreviewDialog(preview, isWorkspaceSwitch: true);
            if (owner is not null)
                dialog.Owner = owner;
            if (dialog.ShowDialog() != true || dialog.ApprovedPlan is null)
                return null;

            RestoreExecutionResult? result = await RunWithProgressAsync(
                snapshot.Name,
                isSwitch: true,
                owner,
                cancellationToken,
                (token, progress) => coordinator.SwitchWorkspaceAsync(
                    snapshot,
                    dialog.ApprovedPlan,
                    token,
                    progress));
            if (result?.Status == RestoreExecutionStatus.Completed)
                RememberApprovedMatches(coordinator, dialog.ApprovedMatchHints);
            if (result?.HasStalePlan == true)
            {
                ShowMessage(
                    owner,
                    "The switch preview became stale while unrelated windows were closing. " +
                    "Nothing stale was applied; reopen the preview and try again.",
                    "Switch Preview Is Stale",
                    MessageBoxImage.Warning);
            }
            else if (result?.Status is RestoreExecutionStatus.Rejected or
                     RestoreExecutionStatus.CompletedWithFailures)
            {
                string placementDetails = FormatPlacementFailures(result);
                ShowMessage(
                    owner,
                    "The approved switch plan could not be completed." + placementDetails +
                    " Reopen the preview to review current status.",
                    "Switch Needs Attention",
                    MessageBoxImage.Warning);
            }
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
            var dialog = new RestorePlanPreviewDialog(preview);
            if (owner is not null)
                dialog.Owner = owner;
            if (dialog.ShowDialog() != true || dialog.ApprovedPlan is null)
                return null;

            RestoreExecutionResult result = await RunWithProgressAsync(
                snapshot.Name,
                isSwitch: false,
                owner,
                cancellationToken,
                (token, progress) => coordinator.RestoreApprovedPlanAsync(
                    snapshot,
                    dialog.ApprovedPlan,
                    token,
                    progress));
            if (result.Status == RestoreExecutionStatus.Completed)
                RememberApprovedMatches(coordinator, dialog.ApprovedMatchHints);
            if (result.HasStalePlan)
            {
                string reasons = string.Join(
                    Environment.NewLine,
                    result.Actions
                        .Where(action => action.Status == RestoreExecutionActionStatus.Stale)
                        .Select(action => $"• {action.Explanation}")
                        .Distinct(StringComparer.Ordinal)
                        .Take(5));
                ShowMessage(
                    owner,
                    "The restore preview is stale because the desktop or a required resource " +
                    "changed after it was shown. Stale actions were not applied. Review a fresh " +
                    $"preview before retrying.{Environment.NewLine}{Environment.NewLine}{reasons}",
                    "Restore Preview Is Stale",
                    MessageBoxImage.Warning);
            }
            else if (result.Status is RestoreExecutionStatus.Rejected or
                     RestoreExecutionStatus.CompletedWithFailures)
            {
                string placementDetails = FormatPlacementFailures(result);
                ShowMessage(
                    owner,
                    "The approved plan could not be completed." + placementDetails +
                    " Reopen the restore preview to review blocking errors and current entry status.",
                    "Restore Needs Attention",
                    MessageBoxImage.Warning);
            }
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

    private static string FormatPlacementFailures(RestoreExecutionResult result)
    {
        if (result.PlacementFailures.Count == 0)
            return "";

        string details = string.Join(
            Environment.NewLine,
            result.PlacementFailures
                .Take(5)
                .Select(entry =>
                    $"• Entry {entry.EntryIndex + 1}: {entry.PlacementVerification} after " +
                    $"{entry.PlacementRetryCount} " +
                    $"retr{(entry.PlacementRetryCount == 1 ? "y" : "ies")}"));
        return Environment.NewLine + Environment.NewLine +
               "Post-restore placement verification:" + Environment.NewLine + details +
               Environment.NewLine;
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
