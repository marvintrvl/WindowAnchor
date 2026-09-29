using WindowAnchor.Native;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class WindowPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Zero_area_task_owner_uses_its_real_ui_only_for_capture_and_matching(bool inactive)
    {
        var root = Window() with
        {
            Hwnd = new IntPtr(10), Bounds = new WindowBounds(0, 0, 0, 0),
            TaskSwitcherRepresentativeHwnd = new IntPtr(11),
            IsCloaked = inactive, CloakState = inactive ? 2u : 0u
        };
        var ui = Window() with
        {
            OwnerHwnd = root.Hwnd, RootOwnerHwnd = root.Hwnd,
            TaskSwitcherRepresentativeHwnd = new IntPtr(11),
            IsCloaked = inactive, CloakState = inactive ? 2u : 0u
        };
        var windows = WindowInventory.ResolveTaskProxyWindows([root, ui]);
        Assert.Equal(root.Hwnd, windows[1].TaskProxyOwnerHwnd);
        foreach (var policy in new[] { WindowCandidatePolicy.CaptureCandidate, WindowCandidatePolicy.RestoreMatchCandidate })
        {
            Assert.False(inactive ? WindowPolicyEvaluator.IncludesInactiveVirtualDesktopCandidate(windows[0], policy) : Includes(windows[0], policy));
            Assert.True(inactive ? WindowPolicyEvaluator.IncludesInactiveVirtualDesktopCandidate(windows[1], policy) : Includes(windows[1], policy));
        }
        Assert.False(Includes(windows[1], WindowCandidatePolicy.SwitchCloseCandidate));
        Assert.False(Includes(windows[1], WindowCandidatePolicy.MinimizeCandidate));
        Assert.False(Includes(windows[1] with { IsCloaked = false, ExtendedStyle = NativeMethodsWindow.WS_EX_TOOLWINDOW }, WindowCandidatePolicy.CaptureCandidate));
        Assert.False(Includes(windows[1] with { IsCloaked = false, ExtendedStyle = NativeMethodsWindow.WS_EX_NOACTIVATE }, WindowCandidatePolicy.CaptureCandidate));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Real_root_or_cross_process_owner_does_not_promote_owned_dialog(bool realRoot, bool differentProcess)
    {
        var root = Window() with
        {
            Hwnd = new IntPtr(10),
            Bounds = realRoot ? new WindowBounds(0, 0, 800, 600) : new WindowBounds(0, 0, 0, 0),
            TaskSwitcherRepresentativeHwnd = new IntPtr(11)
        };
        var popup = Window() with
        {
            ProcessId = differentProcess ? 77u : root.ProcessId,
            OwnerHwnd = root.Hwnd, RootOwnerHwnd = root.Hwnd,
            TaskSwitcherRepresentativeHwnd = new IntPtr(11)
        };
        var windows = WindowInventory.ResolveTaskProxyWindows([root, popup]);
        Assert.Equal(IntPtr.Zero, windows[1].TaskProxyOwnerHwnd);
        Assert.False(Includes(windows[1], WindowCandidatePolicy.CaptureCandidate));
    }

    [Fact]
    public void Capture_candidate_preserves_all_legacy_layout_exclusions()
    {
        var normal = Window();

        Assert.True(Includes(normal, WindowCandidatePolicy.CaptureCandidate));
        Assert.False(Includes(
            normal with { IsVisible = false },
            WindowCandidatePolicy.CaptureCandidate));
        Assert.False(Includes(
            normal with { OwnerHwnd = new IntPtr(99) },
            WindowCandidatePolicy.CaptureCandidate));
        Assert.False(Includes(
            normal with { ClassName = "Shell_TrayWnd" },
            WindowCandidatePolicy.CaptureCandidate));
        Assert.False(Includes(
            normal with { Title = "   " },
            WindowCandidatePolicy.CaptureCandidate));
        Assert.False(Includes(
            normal with { Bounds = new WindowBounds(0, 0, 99, 400) },
            WindowCandidatePolicy.CaptureCandidate));
        Assert.False(Includes(
            normal with { Bounds = new WindowBounds(0, 0, 400, 99) },
            WindowCandidatePolicy.CaptureCandidate));
    }

    [Fact]
    public void Restore_match_candidate_uses_the_same_layout_shape_as_capture()
    {
        var candidates = new[]
        {
            Window(),
            Window() with { IsVisible = false },
            Window() with { OwnerHwnd = new IntPtr(90) },
            Window() with { ClassName = "WorkerW" },
            Window() with { Title = "" },
            Window() with { Bounds = new WindowBounds(0, 0, 50, 50) }
        };

        foreach (var candidate in candidates)
        {
            Assert.Equal(
                Includes(candidate, WindowCandidatePolicy.CaptureCandidate),
                Includes(candidate, WindowCandidatePolicy.RestoreMatchCandidate));
        }
    }

    [Theory]
    [InlineData(NativeMethodsWindow.WS_EX_TOOLWINDOW)]
    [InlineData(NativeMethodsWindow.WS_EX_NOACTIVATE)]
    public void Os_marked_non_task_surfaces_are_not_workspace_windows(long extendedStyle)
    {
        ObservedWindow surface = Window() with
        {
            ExtendedStyle = extendedStyle
        };

        Assert.False(Includes(surface, WindowCandidatePolicy.CaptureCandidate));
        Assert.False(Includes(surface, WindowCandidatePolicy.RestoreMatchCandidate));
        Assert.False(Includes(surface, WindowCandidatePolicy.SwitchCloseCandidate));
        Assert.False(Includes(surface, WindowCandidatePolicy.SwitchRiskCandidate));
        Assert.False(Includes(surface, WindowCandidatePolicy.MinimizeCandidate));
    }

    [Fact]
    public void Dwm_cloaked_surfaces_are_not_workspace_windows_or_switch_risks()
    {
        ObservedWindow cloaked = Window() with { IsCloaked = true };

        foreach (WindowCandidatePolicy policy in Enum.GetValues<WindowCandidatePolicy>())
            Assert.False(Includes(cloaked, policy));
    }

    [Fact]
    public void Shell_cloaked_inactive_desktop_task_can_be_admitted_only_for_capture_and_matching()
    {
        ObservedWindow inactiveDesktopWindow = Window() with
        {
            IsCloaked = true,
            CloakState = WindowPolicyEvaluator.DwmCloakedByShell
        };

        Assert.True(WindowPolicyEvaluator.IncludesInactiveVirtualDesktopCandidate(
            inactiveDesktopWindow,
            WindowCandidatePolicy.CaptureCandidate));
        Assert.True(WindowPolicyEvaluator.IncludesInactiveVirtualDesktopCandidate(
            inactiveDesktopWindow,
            WindowCandidatePolicy.RestoreMatchCandidate));
        Assert.False(WindowPolicyEvaluator.IncludesInactiveVirtualDesktopCandidate(
            inactiveDesktopWindow,
            WindowCandidatePolicy.SwitchCloseCandidate));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(6u)]
    public void Application_cloaking_is_never_treated_as_inactive_virtual_desktop_membership(uint cloakState)
    {
        ObservedWindow appHiddenWindow = Window() with
        {
            IsCloaked = true,
            CloakState = cloakState
        };

        Assert.False(WindowPolicyEvaluator.IncludesInactiveVirtualDesktopCandidate(
            appHiddenWindow,
            WindowCandidatePolicy.CaptureCandidate));
    }

    [Fact]
    public void Appwindow_style_explicitly_opts_an_owned_window_into_task_policies()
    {
        ObservedWindow appWindow = Window() with
        {
            OwnerHwnd = new IntPtr(99),
            ExtendedStyle = NativeMethodsWindow.WS_EX_APPWINDOW |
                NativeMethodsWindow.WS_EX_NOACTIVATE
        };

        Assert.True(Includes(appWindow, WindowCandidatePolicy.CaptureCandidate));
        Assert.True(Includes(appWindow, WindowCandidatePolicy.RestoreMatchCandidate));
        Assert.True(Includes(appWindow, WindowCandidatePolicy.SwitchCloseCandidate));
    }

    [Fact]
    public void Temporary_popup_does_not_remove_its_independent_root_from_capture()
    {
        ObservedWindow root = Window() with
        {
            TaskSwitcherRepresentativeHwnd = new IntPtr(12)
        };

        Assert.True(Includes(root, WindowCandidatePolicy.CaptureCandidate));
        Assert.True(Includes(root, WindowCandidatePolicy.RestoreMatchCandidate));
    }

    [Fact]
    public void Switch_risk_can_see_owned_and_transient_windows_without_capturing_them()
    {
        var ownedSaveDialog = Window() with
        {
            OwnerHwnd = new IntPtr(70),
            ClassName = "#32770",
            Title = "Save changes?",
            Bounds = new WindowBounds(20, 20, 80, 80)
        };
        var untitledTransient = Window() with
        {
            Title = "",
            Bounds = new WindowBounds(0, 0, 40, 40)
        };

        Assert.False(Includes(ownedSaveDialog, WindowCandidatePolicy.CaptureCandidate));
        Assert.True(Includes(ownedSaveDialog, WindowCandidatePolicy.SwitchRiskCandidate));
        Assert.False(Includes(untitledTransient, WindowCandidatePolicy.CaptureCandidate));
        Assert.True(Includes(untitledTransient, WindowCandidatePolicy.SwitchRiskCandidate));
    }

    [Fact]
    public void Close_and_minimize_preserve_layout_exclusions_and_never_select_own_windows()
    {
        const uint ownPid = 42;
        var normal = Window();
        var own = Window() with { ProcessId = ownPid };
        var ownedDialog = Window() with { OwnerHwnd = new IntPtr(123) };
        var shellChrome = Window() with { ClassName = "Progman" };

        Assert.True(Includes(normal, WindowCandidatePolicy.SwitchCloseCandidate, ownPid));
        Assert.True(Includes(normal, WindowCandidatePolicy.MinimizeCandidate, ownPid));

        foreach (var excluded in new[] { own, ownedDialog, shellChrome })
        {
            Assert.False(Includes(excluded, WindowCandidatePolicy.SwitchCloseCandidate, ownPid));
            Assert.False(Includes(excluded, WindowCandidatePolicy.MinimizeCandidate, ownPid));
        }
    }

    [Fact]
    public void Switch_risk_excludes_product_ui_and_shell_chrome()
    {
        const uint ownPid = 42;

        Assert.False(Includes(
            Window() with { ProcessId = ownPid, OwnerHwnd = new IntPtr(1) },
            WindowCandidatePolicy.SwitchRiskCandidate,
            ownPid));
        Assert.False(Includes(
            Window() with { ClassName = "Shell_TrayWnd" },
            WindowCandidatePolicy.SwitchRiskCandidate,
            ownPid));
    }

    [Fact]
    public void Safe_switch_preflight_returns_owned_dialog_observations_independently()
    {
        uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        var ownedDialog = Window() with
        {
            ProcessId = ownPid + 1,
            OwnerHwnd = new IntPtr(77),
            Title = "Save changes?",
            Bounds = new WindowBounds(0, 0, 60, 60)
        };
        var raw = new FakeRawWindowInventory
        {
            Windows =
            [
                ownedDialog,
                Window() with { ProcessId = ownPid }
            ]
        };
        var service = new WindowService(raw);

        var risks = service.InspectUserWindows(WindowCandidatePolicy.SwitchRiskCandidate);

        Assert.Equal(ownedDialog, Assert.Single(risks));
        Assert.Throws<ArgumentException>(() =>
            service.InspectUserWindows(WindowCandidatePolicy.CaptureCandidate));
        Assert.Throws<ArgumentException>(() =>
            service.SnapshotWindows(WindowCandidatePolicy.SwitchRiskCandidate));
    }

    [Fact]
    public void Safe_switch_close_returns_only_requested_non_preserved_handles()
    {
        uint otherPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id + 1;
        ObservedWindow close = Window() with { Hwnd = new IntPtr(11), ProcessId = otherPid };
        ObservedWindow preserve = Window() with { Hwnd = new IntPtr(12), ProcessId = otherPid };
        ObservedWindow ownedRisk = Window() with
        {
            Hwnd = new IntPtr(13),
            ProcessId = otherPid,
            OwnerHwnd = new IntPtr(11)
        };
        var service = new WindowService(new FakeRawWindowInventory
        {
            Windows = [close, preserve, ownedRisk]
        });

        IReadOnlySet<IntPtr> requested = service.RequestCloseUserWindowsExcept(
            WindowCandidatePolicy.SwitchCloseCandidate,
            new HashSet<IntPtr> { preserve.Hwnd });

        Assert.Equal([close.Hwnd], requested);
        Assert.DoesNotContain(ownedRisk.Hwnd, requested);
    }

    private static bool Includes(
        ObservedWindow window,
        WindowCandidatePolicy policy,
        uint ownPid = 0) => WindowPolicyEvaluator.Includes(window, policy, ownPid);

    private static ObservedWindow Window() => new(
        Hwnd: new IntPtr(11),
        ProcessId: 1001,
        OwnerHwnd: IntPtr.Zero,
        IsVisible: true,
        ClassName: "EditorWindow",
        Title: "Notes",
        Bounds: new WindowBounds(0, 0, 800, 600),
        ExecutablePath: @"C:\Apps\editor.exe",
        ProcessName: "editor",
        AppUserModelId: "");

    private sealed class FakeRawWindowInventory : IRawWindowInventory
    {
        internal IReadOnlyList<ObservedWindow> Windows { get; init; } = [];

        public IReadOnlyList<ObservedWindow> EnumerateWindows() => Windows;
        public bool IsWindowAlive(IntPtr hWnd) => Windows.Any(window => window.Hwnd == hWnd);
    }
}
