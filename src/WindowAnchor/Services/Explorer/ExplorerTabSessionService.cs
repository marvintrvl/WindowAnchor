using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Native;

namespace WindowAnchor.Services;

internal sealed record ExplorerWindowSession(
    IReadOnlyList<string> TabPaths,
    int ActiveTabIndex);

internal sealed record ExplorerTabObservation(
    IntPtr WindowHandle,
    IntPtr TabHandle,
    string Location,
    bool IsActive);

internal interface IExplorerTabSessionCapture
{
    IReadOnlyDictionary<IntPtr, ExplorerWindowSession> CaptureOpenWindows();
}

public interface IExplorerTabSessionRestorer
{
    Task<ExplorerTabRestoreResult> RestoreAsync(
        IntPtr windowHandle,
        RestoreExplorerSession session,
        CancellationToken cancellationToken);
}

public sealed record ExplorerTabRestoreResult(
    int RequestedTabCount,
    int ExistingTabCount,
    int OpenedTabCount,
    int FailedTabCount,
    bool ActiveTabSelected)
{
    public bool Succeeded => FailedTabCount == 0 && ActiveTabSelected;
}

internal static class ExplorerTabSessionSnapshotBuilder
{
    internal static IReadOnlyDictionary<IntPtr, ExplorerWindowSession> Build(
        IEnumerable<ExplorerTabObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var result = new Dictionary<IntPtr, ExplorerWindowSession>();
        foreach (IGrouping<IntPtr, ExplorerTabObservation> group in observations
                     .Where(observation => observation.WindowHandle != IntPtr.Zero &&
                         !string.IsNullOrWhiteSpace(observation.Location))
                     .GroupBy(observation => observation.WindowHandle))
        {
            ExplorerTabObservation[] tabs = group.ToArray();
            int activeIndex = Array.FindIndex(tabs, tab => tab.IsActive);
            result[group.Key] = new ExplorerWindowSession(
                tabs.Select(tab => tab.Location).ToArray(),
                activeIndex >= 0 ? activeIndex : 0);
        }
        return result;
    }
}

/// <summary>
/// Captures Windows 11 File Explorer tabs through Shell automation and restores missing tabs by
/// asking the target Explorer window to create a tab, then navigating that tab through its Shell
/// browser COM object. No global keyboard input or third-party helper is required.
/// </summary>
internal sealed class ExplorerTabSessionService : IExplorerTabSessionCapture, IExplorerTabSessionRestorer
{
    private static readonly ExplorerStaDispatcher StaDispatcher = new();
    private static readonly Guid ShellBrowserInterface = typeof(IComShellBrowser).GUID;
    private static readonly TimeSpan TabCreationTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(5);

    public IReadOnlyDictionary<IntPtr, ExplorerWindowSession> CaptureOpenWindows() =>
        ExplorerTabSessionSnapshotBuilder.Build(CaptureOpenTabs());

    internal IReadOnlyList<ExplorerTabObservation> CaptureOpenTabs() =>
        StaDispatcher.Invoke(CaptureOpenTabsCore);

    private static IReadOnlyList<ExplorerTabObservation> CaptureOpenTabsCore()
    {
        var observations = new List<ExplorerTabObservation>();
        object? shell = null;
        object? windows = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
                return observations;

            shell = Activator.CreateInstance(shellType);
            if (shell == null)
                return observations;
            dynamic dynamicShell = shell;
            windows = dynamicShell.Windows();
            dynamic dynamicWindows = windows;
            int count = Convert.ToInt32(dynamicWindows.Count);
            for (int index = 0; index < count; index++)
            {
                object? browser = null;
                try
                {
                    browser = dynamicWindows.Item(index);
                    if (browser == null || !IsFileExplorer(browser))
                        continue;

                    dynamic dynamicBrowser = browser;
                    IntPtr windowHandle = new(Convert.ToInt64(dynamicBrowser.HWND));
                    string location = GetLocation(browser);
                    if (windowHandle == IntPtr.Zero || location.Length == 0)
                        continue;

                    IntPtr tabHandle = GetTabHandle(browser);
                    IntPtr activeTab = NativeMethodsExplorer.FindWindowEx(
                        windowHandle,
                        IntPtr.Zero,
                        NativeMethodsExplorer.ExplorerTabWindowClass,
                        null);
                    observations.Add(new ExplorerTabObservation(
                        windowHandle,
                        tabHandle,
                        location,
                        tabHandle != IntPtr.Zero && tabHandle == activeTab));
                }
                catch
                {
                    // A tab can disappear while ShellWindows is being enumerated.
                }
                finally
                {
                    ReleaseComObject(browser);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "explorer.tabs_capture_failed",
                "File Explorer tab capture was unavailable",
                LogField.Public("errorType", ex.GetType().Name));
        }
        finally
        {
            ReleaseComObject(windows);
            ReleaseComObject(shell);
        }

        return observations;
    }

    public Task<ExplorerTabRestoreResult> RestoreAsync(
        IntPtr windowHandle,
        RestoreExplorerSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return StaDispatcher.InvokeAsync(() =>
        {
            try
            {
                return RestoreCore(windowHandle, session, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "explorer.tabs_restore_failed",
                    "File Explorer tab restoration failed",
                    LogField.Public("errorType", ex.GetType().Name));
                return new ExplorerTabRestoreResult(
                    session.TabPaths.Count,
                    0,
                    0,
                    session.TabPaths.Count,
                    ActiveTabSelected: false);
            }
        });
    }

    private static ExplorerTabRestoreResult RestoreCore(
        IntPtr windowHandle,
        RestoreExplorerSession session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsExplorerWindow(windowHandle) || session.TabPaths.Count == 0)
        {
            return new ExplorerTabRestoreResult(
                session.TabPaths.Count,
                0,
                0,
                session.TabPaths.Count,
                ActiveTabSelected: false);
        }

        object? shell = null;
        object? windows = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            shell = shellType == null ? null : Activator.CreateInstance(shellType);
            if (shell == null)
            {
                return new ExplorerTabRestoreResult(
                    session.TabPaths.Count,
                    0,
                    0,
                    session.TabPaths.Count,
                    ActiveTabSelected: false);
            }

            dynamic dynamicShell = shell;
            windows = dynamicShell.Windows();
            List<ExplorerComTab> existingTabs = EnumerateWindowTabs(windows, windowHandle);
            int existingCount = existingTabs.Count;
            int opened = 0;
            int failed = 0;
            try
            {
                var available = new List<ExplorerComTab>(existingTabs);
                foreach (string desiredPath in session.TabPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int existingIndex = available.FindIndex(tab =>
                        LocationsEqual(tab.Location, desiredPath));
                    if (existingIndex >= 0)
                    {
                        available.RemoveAt(existingIndex);
                        continue;
                    }

                    if (!CanRestoreLocation(desiredPath) ||
                        !TryOpenTab(windows, windowHandle, desiredPath, cancellationToken))
                    {
                        failed++;
                        continue;
                    }
                    opened++;
                }
            }
            finally
            {
                foreach (ExplorerComTab tab in existingTabs)
                    ReleaseComObject(tab.Browser);
            }

            List<ExplorerComTab> finalTabs = EnumerateWindowTabs(windows, windowHandle);
            bool selected;
            try
            {
                selected = SelectSavedActiveTab(windowHandle, finalTabs, session);
            }
            finally
            {
                foreach (ExplorerComTab tab in finalTabs)
                    ReleaseComObject(tab.Browser);
            }

            return new ExplorerTabRestoreResult(
                session.TabPaths.Count,
                existingCount,
                opened,
                failed,
                selected);
        }
        finally
        {
            ReleaseComObject(windows);
            ReleaseComObject(shell);
        }
    }

    private static bool TryOpenTab(
        object shellWindows,
        IntPtr windowHandle,
        string location,
        CancellationToken cancellationToken)
    {
        IntPtr[] currentTabs = EnumerateTabHandles(windowHandle).ToArray();
        IntPtr activeTab = currentTabs.FirstOrDefault();
        if (activeTab == IntPtr.Zero || !NativeMethodsExplorer.PostMessage(
                activeTab,
                NativeMethodsExplorer.WM_COMMAND,
                new IntPtr(NativeMethodsExplorer.ExplorerNewTabCommand),
                IntPtr.Zero))
        {
            return false;
        }

        IntPtr newTab = WaitFor(
            () => EnumerateTabHandles(windowHandle).Except(currentTabs).FirstOrDefault(),
            value => value != IntPtr.Zero,
            TabCreationTimeout,
            cancellationToken);
        if (newTab == IntPtr.Zero)
            return false;

        object? browser = WaitFor(
            () => FindBrowserForTab(shellWindows, windowHandle, newTab),
            value => value != null,
            TabCreationTimeout,
            cancellationToken);
        if (browser == null)
            return false;

        try
        {
            dynamic dynamicBrowser = browser;
            dynamicBrowser.Navigate2(location);
            return WaitFor(
                () => GetLocation(browser),
                observed => LocationsEqual(observed, location),
                NavigationTimeout,
                cancellationToken).Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComObject(browser);
        }
    }

    private static bool SelectSavedActiveTab(
        IntPtr windowHandle,
        IReadOnlyList<ExplorerComTab> tabs,
        RestoreExplorerSession session)
    {
        int activeIndex = Math.Clamp(session.ActiveTabIndex, 0, session.TabPaths.Count - 1);
        string activeLocation = session.TabPaths[activeIndex];
        int desiredOccurrence = session.TabPaths.Take(activeIndex + 1)
            .Count(path => LocationsEqual(path, activeLocation));
        ExplorerComTab? target = tabs
            .Where(tab => LocationsEqual(tab.Location, activeLocation))
            .Skip(desiredOccurrence - 1)
            .FirstOrDefault();
        if (target == null || target.TabHandle == IntPtr.Zero)
            return false;

        IntPtr active = NativeMethodsExplorer.FindWindowEx(
            windowHandle,
            IntPtr.Zero,
            NativeMethodsExplorer.ExplorerTabWindowClass,
            null);
        if (active == target.TabHandle)
            return true;

        int tabCount = EnumerateTabHandles(windowHandle).Count();
        for (int index = 0; index < tabCount; index++)
        {
            NativeMethodsExplorer.SendMessage(
                windowHandle,
                NativeMethodsExplorer.WM_COMMAND,
                new IntPtr(NativeMethodsExplorer.ExplorerSelectTabCommand),
                new IntPtr(index + 1));
            active = WaitFor(
                () => NativeMethodsExplorer.FindWindowEx(
                    windowHandle,
                    IntPtr.Zero,
                    NativeMethodsExplorer.ExplorerTabWindowClass,
                    null),
                handle => handle == target.TabHandle,
                TimeSpan.FromMilliseconds(300),
                CancellationToken.None);
            if (active == target.TabHandle)
                return true;
        }
        return false;
    }

    private static List<ExplorerComTab> EnumerateWindowTabs(object shellWindows, IntPtr windowHandle)
    {
        var tabs = new List<ExplorerComTab>();
        dynamic dynamicWindows = shellWindows;
        int count = Convert.ToInt32(dynamicWindows.Count);
        for (int index = 0; index < count; index++)
        {
            object? browser = null;
            try
            {
                browser = dynamicWindows.Item(index);
                if (browser == null || !IsFileExplorer(browser))
                    continue;
                dynamic dynamicBrowser = browser;
                if (new IntPtr(Convert.ToInt64(dynamicBrowser.HWND)) != windowHandle)
                    continue;

                string location = GetLocation(browser);
                IntPtr tabHandle = GetTabHandle(browser);
                if (location.Length == 0 || tabHandle == IntPtr.Zero)
                    continue;

                tabs.Add(new ExplorerComTab(browser, tabHandle, location));
                browser = null;
            }
            catch
            {
                // The tab may close while it is being observed.
            }
            finally
            {
                ReleaseComObject(browser);
            }
        }
        return tabs;
    }

    private static object? FindBrowserForTab(
        object shellWindows,
        IntPtr windowHandle,
        IntPtr tabHandle)
    {
        object? match = null;
        foreach (ExplorerComTab tab in EnumerateWindowTabs(shellWindows, windowHandle))
        {
            if (tab.TabHandle == tabHandle)
                match = tab.Browser;
            else
                ReleaseComObject(tab.Browser);
        }
        return match;
    }

    private static IEnumerable<IntPtr> EnumerateTabHandles(IntPtr windowHandle)
    {
        IntPtr tab = IntPtr.Zero;
        while (true)
        {
            tab = NativeMethodsExplorer.FindWindowEx(
                windowHandle,
                tab,
                NativeMethodsExplorer.ExplorerTabWindowClass,
                null);
            if (tab == IntPtr.Zero)
                yield break;
            yield return tab;
        }
    }

    private static bool IsFileExplorer(object browser)
    {
        try
        {
            dynamic dynamicBrowser = browser;
            string fullName = Convert.ToString(dynamicBrowser.FullName) ?? "";
            if (Path.GetFileName(fullName).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                return true;
            string name = Convert.ToString(dynamicBrowser.Name) ?? "";
            return name.Equals("File Explorer", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Windows Explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetLocation(object browser)
    {
        object? document = null;
        object? folder = null;
        object? self = null;
        try
        {
            dynamic dynamicBrowser = browser;
            string locationUrl = Convert.ToString(dynamicBrowser.LocationURL) ?? "";
            if (Uri.TryCreate(locationUrl, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                return Uri.UnescapeDataString(uri.LocalPath);
            if (locationUrl.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                return locationUrl;

            document = dynamicBrowser.Document;
            dynamic dynamicDocument = document;
            folder = dynamicDocument.Folder;
            dynamic dynamicFolder = folder;
            self = dynamicFolder.Self;
            dynamic dynamicSelf = self;
            string shellPath = Convert.ToString(dynamicSelf.Path) ?? "";
            if (shellPath.StartsWith("::", StringComparison.Ordinal))
                shellPath = $"shell:{shellPath}";
            return shellPath;
        }
        catch
        {
            return "";
        }
        finally
        {
            ReleaseComObject(self);
            ReleaseComObject(folder);
            ReleaseComObject(document);
        }
    }

    private static IntPtr GetTabHandle(object browser)
    {
        IComShellBrowser? shellBrowser = null;
        try
        {
            if (browser is not IComServiceProvider serviceProvider)
                return IntPtr.Zero;
            Guid service = ShellBrowserInterface;
            Guid iid = ShellBrowserInterface;
            int result = serviceProvider.QueryService(ref service, ref iid, out shellBrowser);
            if (result < 0 || shellBrowser == null)
                return IntPtr.Zero;
            return shellBrowser.GetWindow(out IntPtr handle) >= 0
                ? handle
                : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
        finally
        {
            ReleaseComObject(shellBrowser);
        }
    }

    private static bool IsExplorerWindow(IntPtr windowHandle)
    {
        var className = new char[64];
        int length = NativeMethodsExplorer.GetClassName(windowHandle, className, className.Length);
        return length > 0 && new string(className, 0, length).Equals(
            NativeMethodsExplorer.ExplorerWindowClass,
            StringComparison.Ordinal);
    }

    private static bool CanRestoreLocation(string location) =>
        location.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
        Directory.Exists(location) ||
        File.Exists(location);

    private static bool LocationsEqual(string? left, string? right) =>
        NormalizeLocation(left).Equals(NormalizeLocation(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return "";
        string normalized = location.Trim().Replace('/', '\\');
        if (location.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(location, UriKind.Absolute, out Uri? uri) && uri.IsFile)
        {
            normalized = Uri.UnescapeDataString(uri.LocalPath);
        }
        string? root = null;
        try { root = Path.GetPathRoot(normalized); }
        catch { }
        return normalized.Length > (root?.Length ?? 0)
            ? normalized.TrimEnd('\\')
            : normalized;
    }

    private static T WaitFor<T>(
        Func<T> observe,
        Predicate<T> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        T result = observe();
        while (!predicate(result) && timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(50);
            result = observe();
        }
        return result;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); }
            catch { }
        }
    }

    private sealed record ExplorerComTab(object Browser, IntPtr TabHandle, string Location);

    private sealed class ExplorerStaDispatcher
    {
        private readonly BlockingCollection<Action> _operations = new();

        internal ExplorerStaDispatcher()
        {
            var thread = new Thread(() =>
            {
                foreach (Action operation in _operations.GetConsumingEnumerable())
                    operation();
            })
            {
                IsBackground = true,
                Name = "WindowAnchor Explorer tab automation"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        internal T Invoke<T>(Func<T> operation) =>
            InvokeAsync(operation).GetAwaiter().GetResult();

        internal Task<T> InvokeAsync<T>(Func<T> operation)
        {
            var completion = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _operations.Add(() =>
            {
                try
                {
                    completion.TrySetResult(operation());
                }
                catch (OperationCanceledException ex)
                {
                    completion.TrySetCanceled(ex.CancellationToken);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            return completion.Task;
        }
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(
            ref Guid service,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IComShellBrowser? shellBrowser);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComShellBrowser
    {
        [PreserveSig]
        int GetWindow(out IntPtr windowHandle);
    }
}
