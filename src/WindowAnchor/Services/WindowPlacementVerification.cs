using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using WindowAnchor.Models;
using WindowAnchor.Native;

namespace WindowAnchor.Services;

/// <summary>Observed outcome of applying a planned placement to one assigned HWND.</summary>
public enum WindowPlacementVerificationState
{
    Applied,
    Settling,
    Rejected,
    MovedByApp,
    WindowGone
}

/// <summary>One read-only placement observation made after a restore mutation.</summary>
public sealed record WindowPlacementObservation(
    bool WindowExists,
    bool PlacementReadable,
    int Left,
    int Top,
    int Right,
    int Bottom,
    int ShowCmd,
    uint Dpi)
{
    public static WindowPlacementObservation Gone { get; } =
        new(false, false, 0, 0, 0, 0, 0, 96);

    public static WindowPlacementObservation Unreadable { get; } =
        new(true, false, 0, 0, 0, 0, 0, 96);
}

/// <summary>Bounded, DPI-aware verification policy for one application family.</summary>
public sealed record WindowPlacementVerificationPolicy
{
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(350);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(350);
    public int MaxRetries { get; init; } = 2;
    public int BaseTolerancePixels { get; init; } = 8;

    public static WindowPlacementVerificationPolicy Default { get; } = new();

    internal void Validate()
    {
        if (InitialDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(InitialDelay));
        if (RetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RetryDelay));
        if (MaxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRetries));
        if (BaseTolerancePixels < 0)
            throw new ArgumentOutOfRangeException(nameof(BaseTolerancePixels));
    }
}

/// <summary>Application-specific override for placement tolerance and bounded retry timing.</summary>
public interface IWindowPlacementVerificationStrategy
{
    string Name { get; }
    bool CanHandle(SavedWindowIdentity identity);
    WindowPlacementVerificationPolicy GetPolicy(RestorePlanEntry entry);
}

/// <summary>Read-only boundary used to inspect a specific assigned HWND.</summary>
public interface IWindowPlacementProbe
{
    WindowPlacementObservation Observe(IntPtr hwnd);
}

/// <summary>Final or intermediate comparison result produced without side effects.</summary>
public sealed record WindowPlacementEvaluation(
    WindowPlacementVerificationState State,
    int TolerancePixels,
    string Explanation);

/// <summary>Pure DPI-aware comparison between planned and observed normal placement.</summary>
public static class WindowPlacementVerifier
{
    public static WindowPlacementEvaluation Evaluate(
        RestoreTargetPlacement target,
        WindowPlacementObservation observation,
        WindowPlacementVerificationPolicy policy,
        bool finalObservation,
        bool wasPreviouslyApplied)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        int tolerance = (int)Math.Ceiling(
            policy.BaseTolerancePixels * Math.Max(target.TargetDpi, 96u) / 96d);
        if (!observation.WindowExists)
        {
            return new WindowPlacementEvaluation(
                WindowPlacementVerificationState.WindowGone,
                tolerance,
                "The assigned window closed before placement verification completed.");
        }

        bool applied = observation.PlacementReadable &&
            Math.Abs(observation.Left - target.Left) <= tolerance &&
            Math.Abs(observation.Top - target.Top) <= tolerance &&
            Math.Abs(observation.Right - target.Right) <= tolerance &&
            Math.Abs(observation.Bottom - target.Bottom) <= tolerance &&
            EquivalentShowState(observation.ShowCmd, target.ShowCmd);
        if (applied)
        {
            return new WindowPlacementEvaluation(
                WindowPlacementVerificationState.Applied,
                tolerance,
                "The assigned window matches the planned normal bounds and show state.");
        }

        if (!finalObservation)
        {
            return new WindowPlacementEvaluation(
                WindowPlacementVerificationState.Settling,
                tolerance,
                observation.PlacementReadable
                    ? "The assigned window has not settled at the planned placement yet."
                    : "The assigned window placement is temporarily unreadable while it settles.");
        }

        return wasPreviouslyApplied
            ? new WindowPlacementEvaluation(
                WindowPlacementVerificationState.MovedByApp,
                tolerance,
                "The app moved its window after the planned placement was observed.")
            : new WindowPlacementEvaluation(
                WindowPlacementVerificationState.Rejected,
                tolerance,
                "The window did not accept the planned placement within the bounded retry policy.");
    }

    private static bool EquivalentShowState(int actual, int expected)
    {
        static int Category(int showCmd) => showCmd switch
        {
            3 => 3,
            2 or 6 or 7 or 11 => 2,
            _ => 1
        };
        return Category(actual) == Category(expected);
    }
}

/// <summary>Native placement probe used by the production restore service.</summary>
public sealed class SystemWindowPlacementProbe : IWindowPlacementProbe
{
    public WindowPlacementObservation Observe(IntPtr hwnd)
    {
        if (!NativeMethodsWindow.IsWindow(hwnd))
            return WindowPlacementObservation.Gone;

        var placement = new NativeMethodsWindow.WindowPlacement
        {
            Length = Marshal.SizeOf<NativeMethodsWindow.WindowPlacement>()
        };
        if (!NativeMethodsWindow.GetWindowPlacement(hwnd, ref placement))
            return WindowPlacementObservation.Unreadable;

        return new WindowPlacementObservation(
            true,
            true,
            placement.RcNormalPosition.Left,
            placement.RcNormalPosition.Top,
            placement.RcNormalPosition.Right,
            placement.RcNormalPosition.Bottom,
            placement.ShowCmd,
            NativeMethodsWindow.GetDpiForWindow(hwnd));
    }
}

/// <summary>Inventory-backed fallback that keeps service tests independent of native HWNDs.</summary>
public sealed class InventoryWindowPlacementProbe : IWindowPlacementProbe
{
    private readonly IWindowInventory _inventory;

    public InventoryWindowPlacementProbe(IWindowInventory inventory) =>
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));

    public WindowPlacementObservation Observe(IntPtr hwnd)
    {
        Dictionary<IntPtr, (uint Pid, WindowRecord Record)> windows =
            _inventory.GetWindowsWithPids(WindowCandidatePolicy.RestoreMatchCandidate);
        if (windows.TryGetValue(hwnd, out (uint Pid, WindowRecord Record) observed))
        {
            WindowRecord record = observed.Record;
            return new WindowPlacementObservation(
                true,
                true,
                record.NormalLeft,
                record.NormalTop,
                record.NormalRight,
                record.NormalBottom,
                record.ShowCmd,
                record.SavedDpi > 0 ? record.SavedDpi : 96);
        }
        return _inventory.IsWindowAlive(hwnd)
            ? WindowPlacementObservation.Unreadable
            : WindowPlacementObservation.Gone;
    }
}

/// <summary>Resolves per-app verification policy, falling back to the generic bounded policy.</summary>
public sealed class WindowPlacementVerificationStrategyRegistry
{
    private readonly IReadOnlyList<IWindowPlacementVerificationStrategy> _strategies;
    private readonly WindowPlacementVerificationPolicy _fallback;

    public WindowPlacementVerificationStrategyRegistry(
        WindowPlacementVerificationPolicy? fallback = null,
        IEnumerable<IWindowPlacementVerificationStrategy>? strategies = null)
    {
        _fallback = fallback ?? WindowPlacementVerificationPolicy.Default;
        _fallback.Validate();
        _strategies = (strategies ?? Array.Empty<IWindowPlacementVerificationStrategy>()).ToArray();
    }

    public (string Name, WindowPlacementVerificationPolicy Policy) Resolve(RestorePlanEntry entry)
    {
        IWindowPlacementVerificationStrategy? strategy = _strategies.FirstOrDefault(candidate =>
            candidate.CanHandle(entry.SavedIdentity));
        WindowPlacementVerificationPolicy policy = strategy?.GetPolicy(entry) ?? _fallback;
        policy.Validate();
        return (strategy?.Name ?? "generic", policy);
    }
}
