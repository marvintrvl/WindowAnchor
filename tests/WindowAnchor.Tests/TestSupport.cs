using System.Text.Json;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

internal sealed class TestDirectory : IDisposable
{
    internal TestDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "WindowAnchor.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    internal string CopyFixture(string fixtureName, string relativeDestination)
    {
        string destination = System.IO.Path.Combine(Path, relativeDestination);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        File.Copy(FixturePath(fixtureName), destination);
        return destination;
    }

    internal static WorkspaceSnapshot LoadFixture(string fixtureName)
    {
        string json = File.ReadAllText(FixturePath(fixtureName));
        return JsonSerializer.Deserialize<WorkspaceSnapshot>(json, JsonOptions)!;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }

    internal static string FixturePath(string fixtureName) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

internal sealed class FakeMonitorInventory : IMonitorInventory
{
    internal string Fingerprint { get; set; } = "fixturefp";
    internal List<MonitorInfo> Monitors { get; set; } = new();

    public string GetCurrentMonitorFingerprint() => Fingerprint;
    public List<MonitorInfo> GetCurrentMonitors() => Monitors;
}

internal class FakeWindowInventory : IWindowInventory
{
    internal List<WindowRecord> Snapshot { get; set; } = new();
    internal Dictionary<IntPtr, (uint Pid, WindowRecord Record)> Live { get; set; } = new();
    internal int LiveInventoryCalls { get; private set; }
    internal List<MonitorInfo>? SuppliedMonitors { get; private set; }
    internal List<WindowCandidatePolicy> SnapshotPolicies { get; } = new();
    internal List<WindowCandidatePolicy> LivePolicies { get; } = new();
    internal Func<int, Dictionary<IntPtr, (uint Pid, WindowRecord Record)>>? LiveProvider { get; set; }
    internal Func<IntPtr, bool>? IsAliveProvider { get; set; }
    internal Action? OnSnapshotWindows { get; set; }
    internal IReadOnlyList<RunningApplicationIdentity> RunningApplications { get; set; } = [];

    public virtual List<WindowRecord> SnapshotWindows(
        WindowCandidatePolicy policy,
        List<MonitorInfo>? monitors = null)
    {
        OnSnapshotWindows?.Invoke();
        SnapshotPolicies.Add(policy);
        SuppliedMonitors = monitors;
        return Snapshot;
    }

    public virtual Dictionary<IntPtr, (uint Pid, WindowRecord Record)> GetWindowsWithPids(
        WindowCandidatePolicy policy)
    {
        LivePolicies.Add(policy);
        LiveInventoryCalls++;
        return LiveProvider?.Invoke(LiveInventoryCalls) ?? Live;
    }

    public virtual bool IsWindowAlive(IntPtr hWnd) =>
        IsAliveProvider?.Invoke(hWnd) ?? Live.ContainsKey(hWnd);

    public virtual IReadOnlyList<RunningApplicationIdentity> GetRunningApplications() =>
        RunningApplications;
}

internal sealed class ThrowingWindowInventory : FakeWindowInventory
{
    public override Dictionary<IntPtr, (uint Pid, WindowRecord Record)> GetWindowsWithPids(
        WindowCandidatePolicy policy) =>
        throw new InvalidOperationException("Injected native inventory failure");
}

internal sealed class ThrowingSnapshotWindowInventory : FakeWindowInventory
{
    public override List<WindowRecord> SnapshotWindows(
        WindowCandidatePolicy policy,
        List<MonitorInfo>? monitors = null) =>
        throw new InvalidOperationException("Injected snapshot inventory failure");
}

internal class RecordingWindowMutation : IWindowMutation
{
    internal List<(IntPtr Hwnd, WindowRecord Record)> Restores { get; } = new();
    internal List<HashSet<IntPtr>> MinimizeCalls { get; } = new();
    internal List<WindowCandidatePolicy> MinimizePolicies { get; } = new();
    internal Action<IntPtr, WindowRecord>? OnRestore { get; set; }

    public virtual void RestoreSingleWindow(IntPtr hWnd, WindowRecord record)
    {
        Restores.Add((hWnd, record));
        OnRestore?.Invoke(hWnd, record);
    }

    public int MinimizeUserWindowsExcept(WindowCandidatePolicy policy, HashSet<IntPtr> keep)
    {
        MinimizePolicies.Add(policy);
        MinimizeCalls.Add(new HashSet<IntPtr>(keep));
        return 0;
    }
}

internal sealed class ThrowingWindowMutation : RecordingWindowMutation
{
    public override void RestoreSingleWindow(IntPtr hWnd, WindowRecord record) =>
        throw new InvalidOperationException("Injected mutation failure");
}

internal sealed class FakeBrowserSessionConnector : IBrowserSessionConnector
{
    internal BrowserCaptureResult CaptureResult { get; set; } =
        BrowserCaptureResult.Captured(new List<BrowserSession>());
    internal Exception? CaptureException { get; set; }
    internal int CaptureCalls { get; private set; }
    internal List<string> SelectedTitles { get; private set; } = new();
    internal bool RestoreResult { get; set; } = true;
    internal Exception? RestoreException { get; set; }
    internal int RestoreCalls { get; private set; }
    internal List<BrowserSession> RestoredSessions { get; private set; } = new();

    public Task<BrowserCaptureResult> CaptureAsync(
        string workspaceName,
        IEnumerable<string> selectedBrowserTitles,
        CancellationToken cancellationToken = default)
    {
        CaptureCalls++;
        SelectedTitles = selectedBrowserTitles.ToList();
        if (CaptureException != null)
            throw CaptureException;
        return Task.FromResult(CaptureResult);
    }

    public Task<bool> RestoreAsync(
        string workspaceName,
        List<BrowserSession> sessions,
        CancellationToken cancellationToken = default)
    {
        RestoreCalls++;
        RestoredSessions = sessions;
        if (RestoreException != null) throw RestoreException;
        return Task.FromResult(RestoreResult);
    }
}

internal sealed class RecordingRestoreProcessLauncher : IRestoreProcessLauncher
{
    internal List<RestoreAction> Launches { get; } = new();
    internal Action<RestoreAction>? OnLaunch { get; set; }
    internal Exception? Exception { get; set; }

    public void Launch(RestoreAction action)
    {
        Launches.Add(action);
        if (Exception != null) throw Exception;
        OnLaunch?.Invoke(action);
    }
}

internal sealed class FakeRestoreClock : IRestoreClock
{
    internal List<TimeSpan> Delays { get; } = new();
    internal Action<int>? OnDelay { get; set; }
    private long _elapsedTicks;

    internal TimeSpan Elapsed => TimeSpan.FromTicks(_elapsedTicks);

    internal void Advance(TimeSpan duration) => _elapsedTicks += duration.Ticks;

    public long GetTimestamp() => _elapsedTicks;

    public TimeSpan GetElapsedTime(long startingTimestamp) =>
        TimeSpan.FromTicks(_elapsedTicks - startingTimestamp);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        Advance(delay);
        OnDelay?.Invoke(Delays.Count);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingProgress<T> : IProgress<T>
{
    internal List<T> Reports { get; } = new();

    public void Report(T value) => Reports.Add(value);
}

internal sealed class FakeWindowPlacementProbe : IWindowPlacementProbe
{
    internal int ObservationCalls { get; private set; }
    internal Func<int, IntPtr, WindowPlacementObservation>? ObservationProvider { get; set; }
    internal WindowPlacementObservation DefaultObservation { get; set; } =
        new(true, true, 0, 0, 800, 600, 0, 96);

    public WindowPlacementObservation Observe(IntPtr hwnd)
    {
        ObservationCalls++;
        return ObservationProvider?.Invoke(ObservationCalls, hwnd) ?? DefaultObservation;
    }
}

internal sealed class FakePackagedAppResolver : IPackagedAppResolver
{
    internal PackagedAppResolution? Resolution { get; set; }
    internal List<(string ExecutablePath, string AppUserModelId)> Calls { get; } = new();

    public PackagedAppResolution? Resolve(string executablePath, string? appUserModelId = null)
    {
        Calls.Add((executablePath, appUserModelId ?? ""));
        return Resolution;
    }
}

internal sealed class FakeAppReadinessProbe : IAppReadinessProbe
{
    private readonly FakeWindowInventory _inventory;

    internal FakeAppReadinessProbe(FakeWindowInventory inventory) => _inventory = inventory;

    internal int ObservationCalls { get; private set; }
    internal HashSet<long> UnresponsiveWindowHandles { get; } = new();
    internal HashSet<string> AdditionalProcessNames { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    internal Func<int, AppReadinessObservation>? ObservationProvider { get; set; }

    public AppReadinessObservation Observe()
    {
        ObservationCalls++;
        if (ObservationProvider is not null)
            return ObservationProvider(ObservationCalls);

        Dictionary<IntPtr, (uint Pid, WindowRecord Record)> records =
            _inventory.GetWindowsWithPids(WindowCandidatePolicy.RestoreMatchCandidate);
        LiveWindowIdentity[] windows = records
            .Select(item => WindowIdentityExtractor.FromLive(
                item.Key,
                item.Value.Pid,
                item.Value.Record))
            .OrderBy(window => window.Hwnd.ToInt64())
            .ToArray();
        var processNames = new HashSet<string>(AdditionalProcessNames, StringComparer.OrdinalIgnoreCase);
        processNames.UnionWith(windows.Select(window => window.ProcessName));
        return new AppReadinessObservation
        {
            Windows = windows,
            RunningProcessNames = processNames,
            ResponsiveWindowHandles = windows
                .Select(window => window.Hwnd.ToInt64())
                .Where(handle => !UnresponsiveWindowHandles.Contains(handle))
                .ToHashSet()
        };
    }
}

internal sealed class FakeRestoreResourceBoundary : IRestoreResourceBoundary
{
    internal RestoreResourceAvailability DefaultAvailability { get; set; } =
        RestoreResourceAvailability.Available;
    internal Dictionary<string, RestoreResourceAvailability> AvailabilityByTarget { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    internal List<(int EntryIndex, RestoreResourceKind Kind, string Target)> Observations { get; } = new();
    internal List<RestoreAction> Revalidations { get; } = new();

    public RestoreResourceObservation Observe(
        int entryIndex,
        RestoreResourceKind kind,
        string target)
    {
        Observations.Add((entryIndex, kind, target));
        RestoreResourceAvailability availability = Availability(target);
        return new RestoreResourceObservation(
            entryIndex,
            kind,
            availability,
            availability == RestoreResourceAvailability.Available ? target : "");
    }

    public RestoreResourceValidation Revalidate(RestoreAction action)
    {
        Revalidations.Add(action);
        RestoreResourceAvailability availability = Availability(action.Target);
        return new RestoreResourceValidation(
            availability,
            availability == RestoreResourceAvailability.Available
                ? "Injected resource is available."
                : "Injected resource is stale or missing.");
    }

    private RestoreResourceAvailability Availability(string target) =>
        AvailabilityByTarget.TryGetValue(target, out RestoreResourceAvailability availability)
            ? availability
            : DefaultAvailability;
}

internal sealed class RecordingAtomicFileWriter : IAtomicFileWriter
{
    private readonly AtomicFileWriter _inner = new();

    internal List<string> Destinations { get; } = new();

    public void WriteAllText(string path, string contents)
    {
        Destinations.Add(path);
        _inner.WriteAllText(path, contents);
    }
}

internal sealed class ThrowingAtomicFileWriter : IAtomicFileWriter
{
    internal int Calls { get; private set; }

    public void WriteAllText(string path, string contents)
    {
        Calls++;
        throw new IOException("Injected atomic write failure");
    }
}

internal sealed class FakeCheckpointClock : ICheckpointClock
{
    internal DateTime UtcNow { get; set; } =
        new(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);

    DateTime ICheckpointClock.UtcNow => UtcNow;
}
