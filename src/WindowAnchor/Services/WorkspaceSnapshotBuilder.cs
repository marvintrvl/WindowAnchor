using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Selects monitors/windows and assembles a snapshot without persistence or browser calls.
/// Per-window entry and resource policy is delegated to focused collaborators.
/// </summary>
internal sealed class WorkspaceSnapshotBuilder
{
    private static readonly TimeSpan DefaultCommonFolderSearchBudget = TimeSpan.FromSeconds(5);
    private readonly IWindowInventory _windowInventory;
    private readonly IMonitorInventory _monitorInventory;
    private readonly CaptureResourceResolver _resourceResolver;
    private readonly CapturedWindowEntryFactory _entryFactory;

    internal WorkspaceSnapshotBuilder(
        IWindowInventory windowInventory,
        IMonitorInventory monitorInventory,
        CaptureResourceResolver resourceResolver,
        CapturedWindowEntryFactory entryFactory)
    {
        _windowInventory = windowInventory ?? throw new ArgumentNullException(nameof(windowInventory));
        _monitorInventory = monitorInventory ?? throw new ArgumentNullException(nameof(monitorInventory));
        _resourceResolver = resourceResolver ?? throw new ArgumentNullException(nameof(resourceResolver));
        _entryFactory = entryFactory ?? throw new ArgumentNullException(nameof(entryFactory));
    }

    internal WorkspaceSnapshot Build(WorkspaceCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Stopwatch snapshotTimer = Stopwatch.StartNew();
        CaptureResourceSearchBudget folderSearchBudget = _resourceResolver.CreateSearchBudget(
            request.SaveFiles && request.SearchCommonFolders,
            request.CommonFolderSearchBudget ?? DefaultCommonFolderSearchBudget,
            request.CancellationToken);
        string fingerprint = _monitorInventory.GetCurrentMonitorFingerprint();
        List<MonitorInfo> allMonitors = _monitorInventory.GetCurrentMonitors();

        bool isSelective = request.SelectedWindows != null;
        List<WindowRecord> windows;
        List<MonitorInfo> monitorsToSave;
        if (isSelective)
        {
            windows = request.SelectedWindows!;
            var usedMonitorIds = new HashSet<string>(windows.Select(window => window.MonitorId));
            monitorsToSave = allMonitors
                .Where(monitor => usedMonitorIds.Contains(monitor.MonitorId))
                .ToList();
        }
        else
        {
            monitorsToSave = request.MonitorIds == null
                ? allMonitors
                : allMonitors.Where(monitor => request.MonitorIds.Contains(monitor.MonitorId)).ToList();
            windows = _windowInventory.SnapshotWindows(
                WindowCandidatePolicy.CaptureCandidate,
                allMonitors);
            if (request.MonitorIds != null)
            {
                var selectedMonitorIds = new HashSet<string>(
                    monitorsToSave.Select(monitor => monitor.MonitorId));
                windows = windows
                    .Where(window => selectedMonitorIds.Contains(window.MonitorId))
                    .ToList();
            }
        }

        var entries = new List<WorkspaceEntry>();
        if (request.SaveFiles && request.BuildFullJumpListCache)
        {
            request.Progress?.Report(new SaveProgressReport(
                0,
                windows.Count,
                "Building file detection cache…",
                "",
                WorkspaceCaptureProgressStage.Preparing));
            _resourceResolver.BuildSnapshotCache();
        }

        int progressIndex = 0;
        try
        {
            foreach (WindowRecord window in windows)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                request.Progress?.Report(new SaveProgressReport(
                    ++progressIndex,
                    windows.Count,
                    window.ProcessName,
                    window.TitleSnippet));

                if (!isSelective &&
                    window.ProcessName.Equals("WindowAnchor", StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Info(
                        "workspace.capture_own_window_skipped",
                        "Skipped WindowAnchor's own window during capture");
                    continue;
                }

                entries.Add(_entryFactory.Create(
                    window,
                    request.SaveFiles,
                    folderSearchBudget,
                    request.Progress,
                    isSelective ? 0 : progressIndex,
                    isSelective ? 0 : windows.Count,
                    request.BuildFullJumpListCache));
            }
        }
        finally
        {
            if (request.SaveFiles && request.BuildFullJumpListCache)
                _resourceResolver.ClearSnapshotCache();
        }

        request.Progress?.Report(new SaveProgressReport(
            windows.Count,
            windows.Count,
            "Assembling workspace snapshot…",
            "",
            WorkspaceCaptureProgressStage.Finalizing));

        var snapshot = new WorkspaceSnapshot
        {
            Name = request.Name,
            MonitorFingerprint = fingerprint,
            SavedAt = DateTime.UtcNow,
            SavedWithFiles = request.SaveFiles,
            Monitors = monitorsToSave,
            Entries = entries
        };

        if (isSelective)
        {
            AppLogger.Info(
                "workspace.snapshot_built",
                "Built a selective workspace snapshot",
                LogField.Workspace("workspaceName", request.Name),
                LogField.Public("entryCount", entries.Count),
                LogField.Public("saveFiles", request.SaveFiles),
                LogField.Public("captureMode", "selective"),
                LogField.Public("durationMs", snapshotTimer.Elapsed.TotalMilliseconds),
                LogField.Public("recursiveFileSearch", request.SearchCommonFolders),
                LogField.Public("fullJumpListIndex", request.BuildFullJumpListCache));
        }
        else
        {
            AppLogger.Info(
                "workspace.snapshot_built",
                "Built a workspace snapshot",
                LogField.Workspace("workspaceName", request.Name),
                LogField.Public("entryCount", entries.Count),
                LogField.Public("monitorCount", monitorsToSave.Count),
                LogField.Public("saveFiles", request.SaveFiles),
                LogField.Public("captureMode", "all_windows"),
                LogField.Public("durationMs", snapshotTimer.Elapsed.TotalMilliseconds),
                LogField.Public("recursiveFileSearch", request.SearchCommonFolders),
                LogField.Public("fullJumpListIndex", request.BuildFullJumpListCache));
        }

        return snapshot;
    }
}
