using System;
using System.Collections.Generic;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Provides monitor state without coupling consumers to the Win32 display APIs.</summary>
public interface IMonitorInventory
{
    /// <summary>Returns a stable fingerprint for the current set of monitors.</summary>
    string GetCurrentMonitorFingerprint();

    /// <summary>Returns the currently active monitors.</summary>
    List<MonitorInfo> GetCurrentMonitors();
}

/// <summary>Provides policy-selected views of the raw native window inventory.</summary>
public interface IWindowInventory
{
    /// <summary>Captures windows selected by an explicit policy, optionally assigning monitors.</summary>
    List<WindowRecord> SnapshotWindows(
        WindowCandidatePolicy policy,
        List<MonitorInfo>? monitors = null);

    /// <summary>Returns policy-selected live windows keyed by HWND with their process IDs.</summary>
    Dictionary<IntPtr, (uint Pid, WindowRecord Record)> GetWindowsWithPids(
        WindowCandidatePolicy policy);

    /// <summary>
    /// Returns whether an HWND still exists, including when it is absent from the filtered
    /// user-window inventory because it became temporarily hidden or otherwise ineligible.
    /// </summary>
    bool IsWindowAlive(IntPtr hWnd);
}

/// <summary>Applies restore mutations to live windows.</summary>
public interface IWindowMutation
{
    /// <summary>Restores one live window to a saved placement.</summary>
    void RestoreSingleWindow(IntPtr hWnd, WindowRecord record);

    /// <summary>Minimizes policy-selected windows except those in <paramref name="keep"/>.</summary>
    int MinimizeUserWindowsExcept(WindowCandidatePolicy policy, HashSet<IntPtr> keep);
}
