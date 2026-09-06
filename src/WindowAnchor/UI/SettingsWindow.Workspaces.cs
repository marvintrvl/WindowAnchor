using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.UI;

public partial class SettingsWindow
{
    // ── Refresh ──────────────────────────────────────────────────────────────

    private void Refresh()
    {
        var all = _workspaceService.GetAllWorkspaces();
        var ordered = GetOrderedWorkspaces(all);
        string? defaultId = _settingsService.Settings.DefaultWorkspaceId;

        var wsRows = new List<WorkspaceRow>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var ws = ordered[i];
            wsRows.Add(new WorkspaceRow
            {
                Source    = ws,
                Position  = i + 1,
                IsDefault = !string.IsNullOrEmpty(defaultId)
                    && ws.WorkspaceId.Equals(defaultId, StringComparison.OrdinalIgnoreCase),
            });
        }

        WorkspacesList.ItemsSource = wsRows;
        WorkspaceCountText.Text    = $"{wsRows.Count} workspace{(wsRows.Count == 1 ? "" : "s")} saved";
        WorkspacesEmpty.Visibility = wsRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Rows are rebuilt from scratch, so any previous selection no longer refers to them.
        SelectAllWorkspaces.IsChecked = false;
        UpdateBulkActionBar();
    }

    // ── Bulk selection ───────────────────────────────────────────────────────

    /// <summary>Rows currently ticked for a bulk action.</summary>
    private List<WorkspaceRow> SelectedWorkspaceRows =>
        (WorkspacesList.ItemsSource as IEnumerable<WorkspaceRow>)?
            .Where(r => r.IsSelected).ToList() ?? new List<WorkspaceRow>();

    /// <summary>
    /// Shows or hides the bulk action bar and updates its counter. Called whenever a row
    /// checkbox changes and after the list is rebuilt.
    /// </summary>
    private void UpdateBulkActionBar()
    {
        int count = SelectedWorkspaceRows.Count;
        BulkActionBar.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        BulkSelectionText.Text   = $"{count} workspace{(count == 1 ? "" : "s")} selected";
    }

    private void OnWorkspaceSelectionChanged(object sender, RoutedEventArgs e) => UpdateBulkActionBar();

    /// <summary>Header checkbox: ticks or unticks every row at once.</summary>
    private void OnSelectAllWorkspacesChanged(object sender, RoutedEventArgs e)
    {
        if (WorkspacesList.ItemsSource is not IEnumerable<WorkspaceRow> rows) return;

        bool select = SelectAllWorkspaces.IsChecked == true;
        foreach (var row in rows) row.IsSelected = select;

        UpdateBulkActionBar();
    }

    private void OnClearWorkspaceSelection(object sender, RoutedEventArgs e)
    {
        SelectAllWorkspaces.IsChecked = false;   // also clears every row via the handler above
        if (WorkspacesList.ItemsSource is IEnumerable<WorkspaceRow> rows)
            foreach (var row in rows) row.IsSelected = false;

        UpdateBulkActionBar();
    }

    /// <summary>
    /// Deletes every ticked workspace after a single confirmation listing the names.
    /// </summary>
    private void OnDeleteSelectedWorkspaces(object sender, RoutedEventArgs e)
    {
        var selected = SelectedWorkspaceRows;
        if (selected.Count == 0) return;

        // Show at most five names so the dialog stays readable with a large selection.
        var shown = selected.Take(5).Select(r => $"\u2022 {r.Name}");
        string list = string.Join(Environment.NewLine, shown);
        if (selected.Count > 5)
            list += $"{Environment.NewLine}\u2022 \u2026and {selected.Count - 5} more";

        var confirm = System.Windows.MessageBox.Show(
            $"Delete {selected.Count} workspace{(selected.Count == 1 ? "" : "s")}?" +
            $"{Environment.NewLine}{Environment.NewLine}{list}" +
            $"{Environment.NewLine}{Environment.NewLine}This cannot be undone.",
            "Delete Workspaces",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.OK) return;

        foreach (var row in selected)
        {
            try
            {
                _storageService.DeleteWorkspace(row.Source);
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "workspace.delete_failed",
                    "Could not delete a workspace",
                    ex,
                    LogField.Identifier("workspaceId", row.Source.WorkspaceId),
                    LogField.Workspace("workspaceName", row.Name),
                    LogField.Public("errorCategory", "workspace_delete"));
            }
        }

        AppLogger.Info(
            "workspace.bulk_delete_completed",
            "Completed a bulk workspace deletion",
            LogField.Public("workspaceCount", selected.Count));
        Refresh();
    }

    /// <summary>
    /// Returns workspaces in the user's preferred order.
    /// IDs in <see cref="AppSettings.WorkspaceOrderIds"/> come first (in order),
    /// followed by any remaining workspaces sorted by save date.
    /// </summary>
    private List<WorkspaceSnapshot> GetOrderedWorkspaces(List<WorkspaceSnapshot> all)
        => WorkspaceOrderPolicy.Order(all, _settingsService.Settings.WorkspaceOrderIds);

    /// <summary>Persists the current display order into settings.</summary>
    private void PersistWorkspaceOrder()
    {
        if (WorkspacesList.ItemsSource is not List<WorkspaceRow> rows) return;
        _settingsService.Settings.WorkspaceOrderIds = rows.Select(r => r.Source.WorkspaceId).ToList();
        _settingsService.Save();
    }

    // ── Autostart toggle ──────────────────────────────────────────────────────

    private void OnAutostartToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        if (AutostartToggle.IsChecked == true)
            AutostartService.Enable();
        else
            AutostartService.Disable();
    }

    // ── Save new workspace — inline name card ────────────────────────────────

    private async void OnSaveNewWorkspace(object sender, RoutedEventArgs e)
    {
        var workflow = new SaveWorkspaceWorkflow(_workspaceService, _settingsService);
        SaveWorkspaceWorkflowResult result = await workflow.RunAsync(this);
        if (result.Status == SaveWorkspaceWorkflowStatus.Saved)
            Refresh();
    }

    // ── Workspace row — ⋯ popup ───────────────────────────────────────────────

    private void OnWorkspaceMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not WorkspaceRow row) return;

        var menu = new ContextMenu();

        var restore = new System.Windows.Controls.MenuItem { Header = "Restore Workspace" };
        restore.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowCounterclockwise24 };
        restore.Click += (_, _) => _ = RestorePlanPreviewWorkflow.RunWorkspaceDefaultAsync(
            _coordinator,
            row.Source,
            this);
        menu.Items.Add(restore);

        var restoreAs = new System.Windows.Controls.MenuItem { Header = "Restore As" };
        AddRestoreModeItem(restoreAs, row.Source, RestoreMode.Repair, "Repair", this);
        AddRestoreModeItem(restoreAs, row.Source, RestoreMode.MoveExisting, "Move Existing", this);
        AddRestoreModeItem(restoreAs, row.Source, RestoreMode.Resume, "Resume", this);
        AddRestoreModeItem(restoreAs, row.Source, RestoreMode.LaunchFresh, "Launch Fresh", this);
        AddRestoreModeItem(restoreAs, row.Source, RestoreMode.ExactSwitch, "Exact Switch", this);
        AddRestoreModeItem(restoreAs, row.Source, RestoreMode.PreviewOnly, "Preview Only", this);
        menu.Items.Add(restoreAs);

        var switchWs = new System.Windows.Controls.MenuItem { Header = "Switch to Workspace" };
        switchWs.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowSwap24 };
        switchWs.Click += (_, _) => DoSwitchWorkspace(row);
        menu.Items.Add(switchWs);

        // Only offer Selective Restore when the workspace has more than one monitor
        if (row.Source.Monitors.Count > 1)
        {
            var restoreSelective = new System.Windows.Controls.MenuItem { Header = "Restore Selected Monitors…" };
            restoreSelective.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.DesktopCheckmark24 };
            restoreSelective.Click += (_, _) => DoSelectiveRestore(row);
            menu.Items.Add(restoreSelective);
        }

        var viewWindows = new System.Windows.Controls.MenuItem { Header = "View & Edit Windows…" };
        viewWindows.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.AppsList24 };
        viewWindows.Click += (_, _) => DoViewWindows(row);
        menu.Items.Add(viewWindows);

        menu.Items.Add(new Separator());

        // ── Reorder: Move Up / Move Down ─────────────────────────────────
        if (row.Position > 1)
        {
            var moveUp = new System.Windows.Controls.MenuItem { Header = "Move Up" };
            moveUp.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowUp24 };
            moveUp.Click += (_, _) => MoveWorkspace(row, -1);
            menu.Items.Add(moveUp);
        }

        if (WorkspacesList.ItemsSource is List<WorkspaceRow> rows && row.Position < rows.Count)
        {
            var moveDown = new System.Windows.Controls.MenuItem { Header = "Move Down" };
            moveDown.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowDown24 };
            moveDown.Click += (_, _) => MoveWorkspace(row, +1);
            menu.Items.Add(moveDown);
        }

        menu.Items.Add(new Separator());

        // ── Set / remove default workspace ───────────────────────────────
        if (row.IsDefault)
        {
            var clearDefault = new System.Windows.Controls.MenuItem { Header = "Remove as Default" };
            clearDefault.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.StarOff24 };
            clearDefault.Click += (_, _) =>
            {
                _settingsService.Settings.DefaultWorkspaceId = null;
                _settingsService.Save();
                Refresh();
            };
            menu.Items.Add(clearDefault);
        }
        else
        {
            var setDefault = new System.Windows.Controls.MenuItem { Header = "Set as Default" };
            setDefault.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Star24 };
            setDefault.Click += (_, _) =>
            {
                _settingsService.Settings.DefaultWorkspaceId = row.Source.WorkspaceId;
                _settingsService.Save();
                Refresh();
            };
            menu.Items.Add(setDefault);
        }

        menu.Items.Add(new Separator());

        var rename = new System.Windows.Controls.MenuItem { Header = "Rename…" };
        rename.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Edit24 };
        rename.Click += (_, _) => DoRenameWorkspace(row);
        menu.Items.Add(rename);

        var delete = new System.Windows.Controls.MenuItem { Header = "Delete" };
        delete.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24 };
        delete.Click += (_, _) => DoDeleteWorkspace(row);
        menu.Items.Add(delete);

        menu.PlacementTarget = btn;
        menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen          = true;
    }

    private void MoveWorkspace(WorkspaceRow row, int direction)
    {
        if (WorkspacesList.ItemsSource is not List<WorkspaceRow> rows) return;
        int oldIdx = rows.IndexOf(row);
        int newIdx = oldIdx + direction;
        if (newIdx < 0 || newIdx >= rows.Count) return;

        // Swap in the list
        (rows[oldIdx], rows[newIdx]) = (rows[newIdx], rows[oldIdx]);

        // Update positions
        for (int i = 0; i < rows.Count; i++)
            rows[i].Position = i + 1;

        // Rebind to force UI update
        WorkspacesList.ItemsSource = null;
        WorkspacesList.ItemsSource = rows;

        PersistWorkspaceOrder();
    }

    private void DoSelectiveRestore(WorkspaceRow row)
    {
        string currentFp = _workspaceService.GetCurrentMonitorFingerprint();
        bool mismatch = currentFp != row.Source.MonitorFingerprint;

        var dlg = new SelectiveRestoreDialog(row.Source, mismatch, _settingsService) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.SelectedMonitorIds is { Count: > 0 } ids)
            _ = RestorePlanPreviewWorkflow.RunAsync(
                _coordinator,
                row.Source,
                RestoreMode.Selective(ids.ToArray()),
                this);
    }

    private void AddRestoreModeItem(
        System.Windows.Controls.MenuItem parent,
        WorkspaceSnapshot snapshot,
        RestoreMode mode,
        string label,
        Window owner)
    {
        var item = new System.Windows.Controls.MenuItem
        {
            Header = label,
            IsChecked = snapshot.DefaultRestoreMode == mode.Kind
        };
        item.Click += (_, _) =>
        {
            if (mode.Kind == RestoreModeKind.ExactSwitch)
                _ = RestorePlanPreviewWorkflow.RunSwitchAsync(_coordinator, snapshot, owner);
            else
                _ = RestorePlanPreviewWorkflow.RunAsync(_coordinator, snapshot, mode, owner);
        };
        parent.Items.Add(item);
    }

    private void DoSwitchWorkspace(WorkspaceRow row)
    {
        var result = System.Windows.MessageBox.Show(
            $"Switch to \u201c{row.Name}\u201d?\n\n" +
            "You will review destination matches and monitor adaptation first. " +
            "Only unrelated windows will then be asked to close; apps with unsaved work " +
            "can prompt you to save.\n\nThe approved workspace plan runs after those windows close.",
            "Switch Workspace",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.OK) return;

        _ = RestorePlanPreviewWorkflow.RunSwitchAsync(_coordinator, row.Source, this);
    }

    private void DoViewWindows(WorkspaceRow row)
    {
        var dlg = new WorkspaceWindowsDialog(row.Source, _storageService, _settingsService) { Owner = this };
        dlg.ShowDialog();
        Refresh();
    }

    private void DoRenameWorkspace(WorkspaceRow row)
    {
        // Cancel any other editing rows first
        if (WorkspacesList.ItemsSource is System.Collections.Generic.List<WorkspaceRow> rows)
            foreach (var r in rows) r.IsEditing = false;

        row.EditName  = row.Name;
        row.IsEditing = true;
    }

    // ── Workspace inline edit events ─────────────────────────────────────────

    private void OnWsEditKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;
        if (tb.Tag is not WorkspaceRow row) return;
        if (e.Key == Key.Enter)  CommitWsRename(row);
        if (e.Key == Key.Escape) row.IsEditing = false;
    }

    private void OnWsEditSave(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is WorkspaceRow row)
            CommitWsRename(row);
    }

    private void OnWsEditCancel(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is WorkspaceRow row)
            row.IsEditing = false;
    }

    private void CommitWsRename(WorkspaceRow row)
    {
        string newName = row.EditName.Trim();
        if (!string.IsNullOrEmpty(newName) && newName != row.Name)
            _storageService.RenameWorkspace(row.Source, newName);
        row.IsEditing = false;
        Refresh();
    }

    private void DoDeleteWorkspace(WorkspaceRow row)
    {
        _storageService.DeleteWorkspace(row.Source);
        Refresh();
    }
}
