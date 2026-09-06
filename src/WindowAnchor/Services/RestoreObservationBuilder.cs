using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Immutable facts collected for one restore-plan construction pass.
/// </summary>
internal sealed record RestoreObservation(
    RestoreLiveInventory Inventory,
    RestoreMonitorTopology Topology);

/// <summary>
/// Builds the read-only environment boundary consumed by the pure restore planner.
/// Resource probing, process enumeration, and monitor adaptation stay here; planning remains
/// deterministic and side-effect free once this object has returned.
/// </summary>
internal sealed class RestoreObservationBuilder
{
    private readonly IWindowInventory _windowInventory;
    private readonly IMonitorInventory _monitorInventory;
    private readonly IRestoreResourceBoundary _restoreResources;
    private readonly IPackagedAppResolver _packagedAppResolver;
    private readonly WebAppService _webAppService;
    private readonly SettingsService? _settingsService;
    private readonly IBrowserSessionConnector? _browserSessionConnector;

    internal RestoreObservationBuilder(
        IWindowInventory windowInventory,
        IMonitorInventory monitorInventory,
        IRestoreResourceBoundary restoreResources,
        IPackagedAppResolver packagedAppResolver,
        WebAppService webAppService,
        SettingsService? settingsService,
        IBrowserSessionConnector? browserSessionConnector)
    {
        _windowInventory = windowInventory ?? throw new ArgumentNullException(nameof(windowInventory));
        _monitorInventory = monitorInventory ?? throw new ArgumentNullException(nameof(monitorInventory));
        _restoreResources = restoreResources ?? throw new ArgumentNullException(nameof(restoreResources));
        _packagedAppResolver = packagedAppResolver ?? throw new ArgumentNullException(nameof(packagedAppResolver));
        _webAppService = webAppService ?? throw new ArgumentNullException(nameof(webAppService));
        _settingsService = settingsService;
        _browserSessionConnector = browserSessionConnector;
    }

    internal RestoreObservation Build(
        WorkspaceSnapshot snapshot,
        IReadOnlyDictionary<IntPtr, (uint Pid, WindowRecord Record)> liveWindows)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(liveWindows);
        Stopwatch stopwatch = Stopwatch.StartNew();

        LiveWindowIdentity[] identities = liveWindows
            .Select(window => WindowIdentityExtractor.FromLive(
                window.Key,
                window.Value.Pid,
                window.Value.Record))
            .ToArray();

        var inventory = new RestoreLiveInventory
        {
            Windows = identities,
            Resources = ObserveRestoreResources(snapshot),
            RunningApplications = _windowInventory.GetRunningApplications(),
            MatchHints = _settingsService?.GetWindowMatchHints(snapshot.WorkspaceId) ??
                Array.Empty<WindowMatchHint>(),
            BrowserSessionRestore = snapshot.BrowserSessions.Count == 0
                ? BrowserSessionRestoreAvailability.NotAvailable
                : _browserSessionConnector is null
                    ? BrowserSessionRestoreAvailability.Unavailable
                    : BrowserSessionRestoreAvailability.Available
        };

        RestoreObservation observation = new RestoreObservation(
            inventory,
            BuildRestoreTopology(snapshot, liveWindows));
        AppLogger.Debug(
            "restore.observation_completed",
            "Completed one restore observation pass",
            LogField.Public("entryCount", snapshot.Entries.Count),
            LogField.Public("liveWindowCount", liveWindows.Count),
            LogField.Public("resourceObservationCount", inventory.Resources.Count),
            LogField.Public("runningApplicationCount", inventory.RunningApplications.Count),
            LogField.Public("monitorCount", observation.Topology.Monitors.Count),
            LogField.Public("durationMs", stopwatch.Elapsed.TotalMilliseconds));
        return observation;
    }

    private IReadOnlyList<RestoreResourceObservation> ObserveRestoreResources(
        WorkspaceSnapshot snapshot)
    {
        var observations = new List<RestoreResourceObservation>();
        for (int entryIndex = 0; entryIndex < snapshot.Entries.Count; entryIndex++)
        {
            WorkspaceEntry entry = snapshot.Entries[entryIndex];
            if (!string.IsNullOrWhiteSpace(entry.LaunchArg))
            {
                observations.Add(_restoreResources.Observe(
                    entryIndex,
                    RestoreResourceKind.LaunchTarget,
                    entry.LaunchArg));
            }

            string executableTarget = entry.IsWebApp
                ? entry.WebAppLaunchTarget ?? entry.ExecutablePath
                : entry.ExecutablePath;
            if (!string.IsNullOrWhiteSpace(executableTarget))
            {
                observations.Add(_restoreResources.Observe(
                    entryIndex,
                    RestoreResourceKind.Executable,
                    executableTarget));
            }

            PackagedAppResolution? packaged = _packagedAppResolver.Resolve(
                entry.ExecutablePath,
                entry.AppUserModelId);
            if (packaged is not null)
            {
                observations.Add(new RestoreResourceObservation(
                    entryIndex,
                    RestoreResourceKind.PackagedApplication,
                    RestoreResourceAvailability.Available,
                    packaged.AppUserModelId));
            }

            if (!entry.IsWebApp)
                continue;

            RestoreResourceObservation shortcut = _restoreResources.Observe(
                entryIndex,
                RestoreResourceKind.WebAppShortcut,
                entry.WebAppShortcutPath ?? "");
            if (shortcut.Availability != RestoreResourceAvailability.Available &&
                !string.IsNullOrWhiteSpace(entry.AppUserModelId))
            {
                WebAppInfo? resolved = _webAppService.FindByAumid(entry.AppUserModelId);
                if (resolved is not null)
                {
                    shortcut = _restoreResources.Observe(
                        entryIndex,
                        RestoreResourceKind.WebAppShortcut,
                        resolved.ShortcutPath);
                }
            }
            observations.Add(shortcut);
        }
        return observations;
    }

    private RestoreMonitorTopology BuildRestoreTopology(
        WorkspaceSnapshot snapshot,
        IReadOnlyDictionary<IntPtr, (uint Pid, WindowRecord Record)> liveWindows)
    {
        List<MonitorInfo> currentMonitors = _monitorInventory.GetCurrentMonitors();
        var result = new List<RestoreMonitor>(currentMonitors.Count);
        int nextLeft = 0;
        foreach (MonitorInfo monitor in currentMonitors
                     .OrderBy(item => item.Index)
                     .ThenBy(item => item.MonitorId, StringComparer.OrdinalIgnoreCase))
        {
            uint dpi = monitor.Dpi > 0 ? monitor.Dpi : liveWindows.Values
                .Select(item => item.Record)
                .Where(record => string.Equals(
                    record.MonitorId,
                    monitor.MonitorId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(record => record.SavedDpi)
                .FirstOrDefault(value => value > 0);
            if (dpi == 0)
            {
                dpi = snapshot.Entries
                    .Where(entry => string.Equals(
                        entry.MonitorId,
                        monitor.MonitorId,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.Position?.SavedDpi ?? 0)
                    .FirstOrDefault(value => value > 0);
            }
            if (dpi == 0)
                dpi = 96;

            int width = monitor.WidthPixels > 0 ? monitor.WidthPixels : 1920;
            int height = monitor.HeightPixels > 0 ? monitor.HeightPixels : 1080;
            int left = monitor.HasValidBounds ? monitor.BoundsLeft : nextLeft;
            int top = monitor.HasValidBounds ? monitor.BoundsTop : 0;
            int right = monitor.HasValidBounds ? monitor.BoundsRight : left + width;
            int bottom = monitor.HasValidBounds ? monitor.BoundsBottom : top + height;
            int workLeft = monitor.HasValidWorkArea ? monitor.WorkAreaLeft : left;
            int workTop = monitor.HasValidWorkArea ? monitor.WorkAreaTop : top;
            int workRight = monitor.HasValidWorkArea ? monitor.WorkAreaRight : right;
            int workBottom = monitor.HasValidWorkArea ? monitor.WorkAreaBottom : bottom;
            result.Add(new RestoreMonitor(
                monitor.MonitorId,
                monitor.Index,
                left,
                top,
                right,
                bottom,
                dpi,
                monitor.IsPrimary,
                workLeft,
                workTop,
                workRight,
                workBottom));
            nextLeft = Math.Max(nextLeft, right);
        }

        return new RestoreMonitorTopology
        {
            Monitors = result,
            IsExactMatch = MonitorTopologiesMatchExactly(snapshot.Monitors, currentMonitors)
        };
    }

    internal static bool MonitorTopologiesMatchExactly(
        IReadOnlyList<MonitorInfo> saved,
        IReadOnlyList<MonitorInfo> current)
    {
        if (saved.Count == 0 || saved.Count != current.Count)
            return false;

        MonitorInfo[] savedOrdered = saved.OrderBy(monitor => monitor.Index).ToArray();
        MonitorInfo[] currentOrdered = current.OrderBy(monitor => monitor.Index).ToArray();
        for (int index = 0; index < savedOrdered.Length; index++)
        {
            MonitorInfo source = savedOrdered[index];
            MonitorInfo target = currentOrdered[index];
            if (!source.HasValidBounds || !source.HasValidWorkArea ||
                !target.HasValidBounds || !target.HasValidWorkArea ||
                !string.Equals(source.MonitorId, target.MonitorId, StringComparison.OrdinalIgnoreCase) ||
                source.BoundsLeft != target.BoundsLeft ||
                source.BoundsTop != target.BoundsTop ||
                source.BoundsRight != target.BoundsRight ||
                source.BoundsBottom != target.BoundsBottom ||
                source.WorkAreaLeft != target.WorkAreaLeft ||
                source.WorkAreaTop != target.WorkAreaTop ||
                source.WorkAreaRight != target.WorkAreaRight ||
                source.WorkAreaBottom != target.WorkAreaBottom ||
                (source.Dpi > 0 ? source.Dpi : 96) != (target.Dpi > 0 ? target.Dpi : 96))
            {
                return false;
            }
        }
        return true;
    }
}
