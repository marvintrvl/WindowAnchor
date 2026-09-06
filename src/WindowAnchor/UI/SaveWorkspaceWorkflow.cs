using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.UI;

internal enum SaveWorkspaceWorkflowStatus
{
    Cancelled,
    Saved,
    Failed
}

/// <summary>Result returned to a tray or Settings presenter after the shared save workflow.</summary>
internal sealed record SaveWorkspaceWorkflowResult(
    SaveWorkspaceWorkflowStatus Status,
    string WorkspaceName = "",
    int SelectedWindowCount = 0,
    WorkspaceCaptureResult? Capture = null);

/// <summary>
/// Owns the common Save Workspace UI and capture/persistence sequence. Callers remain responsible
/// only for context-specific success presentation, such as a tray balloon or Settings refresh.
/// </summary>
internal sealed class SaveWorkspaceWorkflow
{
    private readonly WorkspaceService _workspaceService;
    private readonly SettingsService? _settingsService;

    internal SaveWorkspaceWorkflow(
        WorkspaceService workspaceService,
        SettingsService? settingsService)
    {
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _settingsService = settingsService;
    }

    internal async Task<SaveWorkspaceWorkflowResult> RunAsync(Window? owner = null)
    {
        List<(MonitorInfo Monitor, List<WindowRecord> Windows)> windowPreview;
        try
        {
            windowPreview = await Task.Run(_workspaceService.GetWindowPreviewForDialog);
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "workspace.preview_enumeration_failed",
                "Could not enumerate windows for the save dialog",
                ex,
                LogField.Public("errorCategory", "window_enumeration"));
            windowPreview = [];
        }

        var dialog = new SaveWorkspaceDialog(windowPreview, _settingsService);
        if (owner != null)
            dialog.Owner = owner;
        if (dialog.ShowDialog() != true)
            return new SaveWorkspaceWorkflowResult(SaveWorkspaceWorkflowStatus.Cancelled);

        // WPF-bound dialog properties must be read before capture work moves to background threads.
        string name = dialog.WorkspaceName;
        bool saveFiles = dialog.SaveFiles;
        List<WindowRecord> selectedWindows = dialog.SelectedWindows;

        SaveProgressWindow? progressWindow = null;
        if (saveFiles)
        {
            progressWindow = new SaveProgressWindow(name);
            if (owner != null)
                progressWindow.Owner = owner;
            progressWindow.Show();
        }

        IProgress<SaveProgressReport>? progress = progressWindow == null
            ? null
            : new Progress<SaveProgressReport>(progressWindow.ApplyReport);

        bool? ownerWasEnabled = owner?.IsEnabled;
        if (owner != null)
            owner.IsEnabled = false;

        try
        {
            WorkspaceCaptureResult capture = await _workspaceService.CaptureWorkspaceAsync(
                name,
                saveFiles: saveFiles,
                selectedWindows: selectedWindows,
                progress: progress);
            _workspaceService.PersistCapture(
                capture,
                WorkspaceArtifactKind.NamedWorkspace,
                IncompleteBrowserCapturePolicy.SavePartialWorkspace);
            AppLogger.Info(
                "workspace.saved",
                "Saved a named workspace",
                LogField.Identifier("workspaceId", capture.Snapshot.WorkspaceId),
                LogField.Workspace("workspaceName", name),
                LogField.Public("saveFiles", saveFiles),
                LogField.Public("browserCaptureStatus", capture.BrowserCapture.Status));
            return new SaveWorkspaceWorkflowResult(
                SaveWorkspaceWorkflowStatus.Saved,
                name,
                selectedWindows.Count,
                capture);
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "workspace.save_failed",
                "Workspace capture or persistence failed",
                ex,
                LogField.Workspace("workspaceName", name),
                LogField.Public("errorCategory", "workspace_save"));
            MessageBox.Show(
                $"Failed to save workspace: {ex.Message}",
                "WindowAnchor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return new SaveWorkspaceWorkflowResult(
                SaveWorkspaceWorkflowStatus.Failed,
                name,
                selectedWindows.Count);
        }
        finally
        {
            if (owner != null && ownerWasEnabled.HasValue)
                owner.IsEnabled = ownerWasEnabled.Value;
            progressWindow?.Close();
        }
    }
}
