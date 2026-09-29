using WindowAnchor.Services;
using WindowAnchor.UI;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Xunit.Abstractions;

namespace WindowAnchor.Tests;

public class VirtualDesktopLiveTests(ITestOutputHelper output)
{
    [Fact]
    public void Live_inventory_includes_inactive_desktop_apps_when_requested()
    {
        if (Environment.GetEnvironmentVariable("WINDOWANCHOR_LIVE_DESKTOP_SMOKE") != "1")
            return;

        var manager = new VirtualDesktopAssociationService();
        var raw = new WindowInventory().EnumerateWindows();
        foreach (var window in raw.Where(w => w.IsVisible &&
                     w.ProcessName.Contains("iobit", StringComparison.OrdinalIgnoreCase)))
        {
            var desktop = manager.TryGetWindowDesktopId(window.Hwnd);
            output.WriteLine($"IObit diagnostic: hwnd={window.Hwnd}, pid={window.ProcessId}, process={window.ProcessName}, class={window.ClassName}, visible={window.IsVisible}, cloak={window.CloakState}, style={window.ExtendedStyle:X}, owner={window.OwnerHwnd}, root={window.RootOwnerHwnd}, representative={window.TaskSwitcherRepresentativeHwnd}, taskProxy={window.TaskProxyOwnerHwnd}, bounds={window.Bounds}, pathAvailable={!string.IsNullOrEmpty(window.ExecutablePath)}, desktop={desktop.Status}, desktopId={desktop.DesktopId}, current={desktop.IsOnCurrentDesktop}, candidate={WindowPolicyEvaluator.IncludesInactiveVirtualDesktopCandidate(window, WindowCandidatePolicy.CaptureCandidate)}");
        }
        var inactive = raw.Where(window =>
            WindowPolicyEvaluator.IncludesInactiveVirtualDesktopCandidate(
                window, WindowCandidatePolicy.CaptureCandidate))
            .Select(window => (Window: window, Desktop: manager.TryGetWindowDesktopId(
                window.TaskProxyOwnerHwnd != IntPtr.Zero ? window.TaskProxyOwnerHwnd : window.Hwnd)))
            .Where(item => item.Desktop.IsOnCurrentDesktop == false).ToArray();
        foreach (var item in inactive)
            output.WriteLine($"Inactive app={item.Window.ProcessName}, visible={item.Window.IsVisible}, cloak={item.Window.CloakState}, status={item.Desktop.Status}");
        Assert.NotEmpty(inactive);

        // This is the real default construction used by the app, without opting into movement.
        var service = new WindowService();
        var monitors = new MonitorService().GetCurrentMonitors();
        var captured = service.SnapshotWindows(WindowCandidatePolicy.CaptureCandidate, monitors);
        var matched = service.GetWindowsWithPids(WindowCandidatePolicy.RestoreMatchCandidate);
        if (Environment.GetEnvironmentVariable("WINDOWANCHOR_LIVE_IOBIT_SMOKE") == "1")
        {
            var iobit = Assert.Single(captured, w =>
                w.ProcessName.Equals("IObitUninstaler", StringComparison.OrdinalIgnoreCase));
            Assert.False(iobit.IsOnCurrentVirtualDesktop);
            Assert.Equal("TfrmIObitUninstall", iobit.ClassName);
            Assert.True(File.Exists(iobit.ExecutablePath));
            output.WriteLine("IObit visible application captured exactly once, on inactive desktop, with valid executable path.");
        }
        output.WriteLine($"Raw inactive tasks={inactive.Length}; captured inactive={captured.Count(w => w.IsOnCurrentVirtualDesktop == false)}; matchable inactive={matched.Values.Count(w => w.Record.IsOnCurrentVirtualDesktop == false)}");
        Assert.All(inactive, item => Assert.Contains(item.Window.Hwnd, matched.Keys));
        Assert.All(inactive, item => Assert.Contains(captured, record =>
            record.ProcessName == item.Window.ProcessName &&
            record.VirtualDesktopId == item.Desktop.DesktopId?.ToString("D")));
        var desktops = VirtualDesktopCatalog.Read();
        Assert.True(desktops.Count >= 2);
        var groups = SaveWorkspaceDialog.BuildGroups(monitors.Select(m =>
            (m, captured.Where(w => w.MonitorId == m.MonitorId).ToList())).ToList(), desktops: desktops);
        Assert.Equal(captured.Count, groups.Sum(group => group.Windows.Count));
        Assert.All(inactive, item => Assert.Contains(groups, group =>
            group.GroupHeader.StartsWith(desktops.Single(d => d.Id == item.Desktop.DesktopId).Name) &&
            group.Windows.Any(w => w.Record.VirtualDesktopId == item.Desktop.DesktopId?.ToString("D"))));
    }

    [Fact]
    public void Private_topology_api_can_create_and_remove_an_empty_desktop()
    {
        if (Environment.GetEnvironmentVariable("WINDOWANCHOR_LIVE_TOPOLOGY_SMOKE") != "1") return;
        var service = new VirtualDesktopAssociationService();
        Assert.True(service.TryCreateAndRemoveDesktopForSmokeTest(out Guid created));
        Assert.NotEqual(Guid.Empty, created);
        output.WriteLine($"Created and removed temporary virtual desktop {created:D}.");
    }

    [Fact]
    public void Private_move_api_can_round_trip_an_external_style_window()
    {
        if (Environment.GetEnvironmentVariable("WINDOWANCHOR_LIVE_DESKTOP_MOVE_SMOKE") != "1") return;

        using var ready = new ManualResetEventSlim();
        Window? smokeWindow = null;
        Dispatcher? dispatcher = null;
        IntPtr hWnd = IntPtr.Zero;
        Exception? threadError = null;
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                smokeWindow = new Window
                {
                    Title = $"WindowAnchor virtual desktop smoke {Guid.NewGuid():N}",
                    Width = 320,
                    Height = 180,
                    ShowInTaskbar = true,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                smokeWindow.SourceInitialized += (_, _) =>
                {
                    hWnd = new WindowInteropHelper(smokeWindow).Handle;
                };
                smokeWindow.Loaded += (_, _) =>
                {
                    smokeWindow.Activate();
                    ready.Set();
                };
                smokeWindow.Show();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                threadError = ex;
                ready.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));

        try
        {
            Assert.Null(threadError);
            Assert.NotEqual(IntPtr.Zero, hWnd);
            var service = new VirtualDesktopAssociationService();
            bool moved = service.TryMoveWindowRoundTripForSmokeTest(
                hWnd,
                out Guid temporary,
                out string diagnostic);
            output.WriteLine(diagnostic);
            Assert.True(moved, diagnostic);
            Assert.NotEqual(Guid.Empty, temporary);
            output.WriteLine($"Moved a controlled window to temporary desktop {temporary:D}, moved it back, and removed the temporary desktop.");
        }
        finally
        {
            dispatcher?.BeginInvoke(() =>
            {
                smokeWindow?.Close();
                dispatcher.InvokeShutdown();
            });
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public void Private_move_api_moves_iobit_root_and_main_views_together()
    {
        if (Environment.GetEnvironmentVariable("WINDOWANCHOR_LIVE_IOBIT_MOVE_SMOKE") != "1") return;

        var service = new VirtualDesktopAssociationService();
        ObservedWindow main = Assert.Single(new WindowInventory().EnumerateWindows(), window =>
            window.ProcessName.Equals("IObitUninstaler", StringComparison.OrdinalIgnoreCase) &&
            window.ClassName == "TfrmIObitUninstall" &&
            window.TaskProxyOwnerHwnd != IntPtr.Zero);
        Guid original = Assert.IsType<Guid>(
            service.TryGetWindowDesktopId(main.TaskProxyOwnerHwnd).DesktopId);
        Guid target = Assert.Single(VirtualDesktopCatalog.Read(), desktop => desktop.Id != original).Id;

        VirtualDesktopMoveResult outward = service.TryMoveWindowToDesktop(main.Hwnd, target);
        Assert.Equal(VirtualDesktopAssociationStatus.Available, outward.Status);
        Assert.True(WaitForMembership(service, main.Hwnd, main.TaskProxyOwnerHwnd, target));

        VirtualDesktopMoveResult home = service.TryMoveWindowToDesktop(main.Hwnd, original);
        Assert.Equal(VirtualDesktopAssociationStatus.Available, home.Status);
        Assert.True(WaitForMembership(service, main.Hwnd, main.TaskProxyOwnerHwnd, original));
        output.WriteLine("Moved IObit's hidden TApplication root and visible main view together, then returned both to their original desktop.");
    }

    private static bool WaitForMembership(
        VirtualDesktopAssociationService service,
        IntPtr main,
        IntPtr root,
        Guid expected)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (service.TryGetWindowDesktopId(main).DesktopId == expected &&
                service.TryGetWindowDesktopId(root).DesktopId == expected)
                return true;
            Thread.Sleep(50);
        }
        return false;
    }
}
