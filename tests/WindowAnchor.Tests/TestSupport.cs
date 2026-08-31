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

    public virtual List<WindowRecord> SnapshotWindows(
        WindowCandidatePolicy policy,
        List<MonitorInfo>? monitors = null)
    {
        SnapshotPolicies.Add(policy);
        SuppliedMonitors = monitors;
        return Snapshot;
    }

    public virtual Dictionary<IntPtr, (uint Pid, WindowRecord Record)> GetWindowsWithPids(
        WindowCandidatePolicy policy)
    {
        LivePolicies.Add(policy);
        LiveInventoryCalls++;
        return Live;
    }

    public virtual bool IsWindowAlive(IntPtr hWnd) => Live.ContainsKey(hWnd);
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

    public virtual void RestoreSingleWindow(IntPtr hWnd, WindowRecord record) =>
        Restores.Add((hWnd, record));

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
        CancellationToken cancellationToken = default) => Task.FromResult(true);
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
