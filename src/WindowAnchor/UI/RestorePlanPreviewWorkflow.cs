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

            RestoreExecutionResult result = await coordinator.RestoreApprovedPlanAsync(
                snapshot,
                dialog.ApprovedPlan,
                cancellationToken);
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
                ShowMessage(
                    owner,
                    "The approved plan could not be completed. Reopen the restore preview to " +
                    "review blocking errors and current entry status.",
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
