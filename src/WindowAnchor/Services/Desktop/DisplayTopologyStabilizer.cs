using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Waits until the active display topology has remained unchanged long enough for restoration.</summary>
public sealed class DisplayTopologyStabilizer
{
    private static readonly TimeSpan DefaultSettleInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly IMonitorInventory _monitorInventory;
    private readonly IRestoreClock _clock;
    private readonly TimeSpan _settleInterval;
    private readonly TimeSpan _sampleInterval;
    private readonly TimeSpan _timeout;

    public DisplayTopologyStabilizer(
        IMonitorInventory monitorInventory,
        IRestoreClock? clock = null,
        TimeSpan? settleInterval = null,
        TimeSpan? sampleInterval = null,
        TimeSpan? timeout = null)
    {
        _monitorInventory = monitorInventory ?? throw new ArgumentNullException(nameof(monitorInventory));
        _clock = clock ?? new SystemRestoreClock();
        _settleInterval = settleInterval ?? DefaultSettleInterval;
        _sampleInterval = sampleInterval ?? DefaultSampleInterval;
        _timeout = timeout ?? DefaultTimeout;

        if (_settleInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(settleInterval));
        if (_sampleInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sampleInterval));
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<DisplayTopologyStabilizationResult> WaitForStableTopologyAsync(
        CancellationToken cancellationToken = default)
    {
        long startedAt = _clock.GetTimestamp();
        DisplayTopologySnapshot latest = Capture();
        long unchangedSince = startedAt;

        while (true)
        {
            TimeSpan elapsed = _clock.GetElapsedTime(startedAt);
            TimeSpan unchangedFor = _clock.GetElapsedTime(unchangedSince);
            if (unchangedFor >= _settleInterval)
                return DisplayTopologyStabilizationResult.Stable(latest, elapsed);
            if (elapsed >= _timeout)
                return DisplayTopologyStabilizationResult.Timeout(latest, elapsed);

            TimeSpan remaining = Min(
                _sampleInterval,
                _settleInterval - unchangedFor,
                _timeout - elapsed);
            await _clock.DelayAsync(remaining, cancellationToken).ConfigureAwait(false);

            DisplayTopologySnapshot next = Capture();
            if (!string.Equals(next.Signature, latest.Signature, StringComparison.Ordinal))
            {
                latest = next;
                unchangedSince = _clock.GetTimestamp();
            }
        }
    }

    private DisplayTopologySnapshot Capture()
    {
        List<MonitorInfo> monitors = _monitorInventory.GetCurrentMonitors();
        return new DisplayTopologySnapshot(
            _monitorInventory.GetCurrentMonitorFingerprint(),
            DisplayTopologySignature.Create(monitors));
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second, TimeSpan third) =>
        first <= second && first <= third ? first : second <= third ? second : third;
}

public sealed record DisplayTopologySnapshot(string Fingerprint, string Signature);

public sealed record DisplayTopologyStabilizationResult(
    DisplayTopologySnapshot Snapshot,
    bool IsStable,
    bool TimedOut,
    TimeSpan Elapsed)
{
    public static DisplayTopologyStabilizationResult Stable(DisplayTopologySnapshot snapshot, TimeSpan elapsed) =>
        new(snapshot, true, false, elapsed);

    public static DisplayTopologyStabilizationResult Timeout(DisplayTopologySnapshot snapshot, TimeSpan elapsed) =>
        new(snapshot, false, true, elapsed);
}

/// <summary>Builds an order-independent signature of the current, restore-relevant monitor topology.</summary>
public static class DisplayTopologySignature
{
    public static string Create(IEnumerable<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        return string.Join("|", monitors
            .Select(Describe)
            .OrderBy(value => value, StringComparer.Ordinal));
    }

    private static string Describe(MonitorInfo monitor) => string.Join(",", [
        Escape(monitor.MonitorId),
        Escape(monitor.DeviceName),
        monitor.BoundsLeft.ToString(CultureInfo.InvariantCulture),
        monitor.BoundsTop.ToString(CultureInfo.InvariantCulture),
        monitor.BoundsRight.ToString(CultureInfo.InvariantCulture),
        monitor.BoundsBottom.ToString(CultureInfo.InvariantCulture),
        monitor.WorkAreaLeft.ToString(CultureInfo.InvariantCulture),
        monitor.WorkAreaTop.ToString(CultureInfo.InvariantCulture),
        monitor.WorkAreaRight.ToString(CultureInfo.InvariantCulture),
        monitor.WorkAreaBottom.ToString(CultureInfo.InvariantCulture),
        monitor.Dpi.ToString(CultureInfo.InvariantCulture),
        monitor.IsPrimary ? "primary" : "secondary",
        monitor.Orientation.ToString(CultureInfo.InvariantCulture)
    ]);

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal);
}
