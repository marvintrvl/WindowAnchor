using System;
using System.Runtime.InteropServices;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Outcome from the virtual-desktop interoperability boundary.</summary>
public enum VirtualDesktopAssociationStatus
{
    Available,
    Unsupported,
    Unavailable,
    Rejected
}

/// <summary>Safe result of observing a top-level window's virtual desktop.</summary>
public sealed record VirtualDesktopCaptureResult(
    VirtualDesktopAssociationStatus Status,
    Guid? DesktopId = null,
    bool? IsOnCurrentDesktop = null);

/// <summary>Safe result of requesting a move to a saved virtual desktop.</summary>
public sealed record VirtualDesktopMoveResult(
    VirtualDesktopAssociationStatus Status,
    string Message = "");

public sealed record VirtualDesktopTopologyResult(
    VirtualDesktopAssociationStatus Status,
    IReadOnlyDictionary<Guid, Guid> DesktopMap,
    int CreatedDesktopCount = 0,
    string Message = "");

/// <summary>
/// Boundary for Windows virtual-desktop capture, topology reconstruction, and window placement.
/// A failed topology or move operation is an adaptation, not a restore-fatal error.
/// </summary>
public interface IVirtualDesktopAssociation
{
    VirtualDesktopCaptureResult TryGetWindowDesktopId(IntPtr hWnd);
    VirtualDesktopMoveResult TryMoveWindowToDesktop(IntPtr hWnd, Guid desktopId);
    VirtualDesktopTopologyResult EnsureTopology(IReadOnlyList<SavedVirtualDesktop> savedDesktops);
}

/// <summary>
/// Uses the public <c>IVirtualDesktopManager</c> interface for observation and a tightly
/// build-gated Windows 11 Shell interface for topology reconstruction and cross-process moves.
/// </summary>
public sealed class VirtualDesktopAssociationService : IVirtualDesktopAssociation
{
    internal bool TryMoveWindowRoundTripForSmokeTest(
        IntPtr hWnd,
        out Guid temporaryDesktopId,
        out string diagnostic)
    {
        temporaryDesktopId = Guid.Empty;
        diagnostic = "";
        VirtualDesktopCaptureResult original = new(VirtualDesktopAssociationStatus.Unavailable);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            original = TryGetWindowDesktopId(hWnd);
            if (original.Status == VirtualDesktopAssociationStatus.Available) break;
            Thread.Sleep(50);
        }
        if (!SupportsPrivateWindows11Api() ||
            original.Status != VirtualDesktopAssociationStatus.Available ||
            original.DesktopId is not Guid originalDesktopId)
        {
            diagnostic = $"Original desktop lookup failed: {original.Status}.";
            return false;
        }

        object? shellObject = null;
        object? managerObject = null;
        IDesktopManagerFacade? manager = null;
        IVirtualDesktop? created = null;
        IVirtualDesktop? fallback = null;
        bool removed = false;
        try
        {
            Type shellType = Type.GetTypeFromCLSID(ImmersiveShellClsid)!;
            shellObject = Activator.CreateInstance(shellType)!;
            var provider = (IServiceProvider)shellObject;
            managerObject = provider.QueryService(
                ref VirtualDesktopManagerInternalClsid,
                ref VirtualDesktopManagerInternalIid);
            manager = CreatePrivateManager(managerObject);
            fallback = manager.GetCurrentDesktop();
            created = manager.CreateDesktop();
            temporaryDesktopId = created.GetId();
            if (temporaryDesktopId == Guid.Empty)
            {
                diagnostic = "Windows returned an empty temporary desktop ID.";
                return false;
            }
            VirtualDesktopMoveResult outward = TryMoveWindowWithApplicationView(hWnd, temporaryDesktopId);
            if (outward.Status != VirtualDesktopAssociationStatus.Available)
            {
                diagnostic = $"Move to temporary desktop failed: {outward.Status}; {outward.Message}";
                return false;
            }
            if (!WaitForDesktop(hWnd, temporaryDesktopId))
            {
                diagnostic = "Move call succeeded but the public API did not observe the temporary desktop.";
                return false;
            }

            VirtualDesktopMoveResult home = TryMoveWindowWithApplicationView(hWnd, originalDesktopId);
            if (home.Status != VirtualDesktopAssociationStatus.Available)
            {
                diagnostic = $"Move back failed: {home.Status}; {home.Message}";
                return false;
            }
            if (!WaitForDesktop(hWnd, originalDesktopId))
            {
                diagnostic = "Move-back call succeeded but the public API did not observe the original desktop.";
                return false;
            }

            manager.RemoveDesktop(created, fallback);
            removed = true;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or PlatformNotSupportedException)
        {
            diagnostic = $"Smoke setup failed: 0x{ex.HResult:X8}; {ex.Message}";
            return false;
        }
        finally
        {
            if (!removed && created is not null && fallback is not null && manager is not null)
            {
                try { manager.RemoveDesktop(created, fallback); } catch { }
            }
            if (created is not null && Marshal.IsComObject(created)) Marshal.ReleaseComObject(created);
            if (fallback is not null && Marshal.IsComObject(fallback)) Marshal.ReleaseComObject(fallback);
            if (managerObject is not null && Marshal.IsComObject(managerObject)) Marshal.ReleaseComObject(managerObject);
            if (shellObject is not null && Marshal.IsComObject(shellObject)) Marshal.ReleaseComObject(shellObject);
        }
    }

    private bool WaitForDesktop(IntPtr hWnd, Guid expectedDesktopId)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (TryGetWindowDesktopId(hWnd).DesktopId == expectedDesktopId) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    internal bool TryCreateAndRemoveDesktopForSmokeTest(out Guid createdDesktopId)
    {
        createdDesktopId = Guid.Empty;
        if (!SupportsPrivateWindows11Api()) return false;
        object? shellObject = null;
        object? managerObject = null;
        IDesktopManagerFacade? manager = null;
        IVirtualDesktop? created = null;
        IVirtualDesktop? fallback = null;
        bool removed = false;
        try
        {
            Type shellType = Type.GetTypeFromCLSID(ImmersiveShellClsid)!;
            shellObject = Activator.CreateInstance(shellType)!;
            var provider = (IServiceProvider)shellObject;
            managerObject = provider.QueryService(
                ref VirtualDesktopManagerInternalClsid,
                ref VirtualDesktopManagerInternalIid);
            manager = CreatePrivateManager(managerObject);
            int before = manager.GetCount();
            fallback = manager.GetCurrentDesktop();
            created = manager.CreateDesktop();
            createdDesktopId = created.GetId();
            if (createdDesktopId == Guid.Empty || manager.GetCount() != before + 1) return false;
            manager.RemoveDesktop(created, fallback);
            removed = true;
            return manager.GetCount() == before;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            if (!removed && created is not null && fallback is not null && manager is not null)
            {
                try { manager.RemoveDesktop(created, fallback); } catch { }
            }
            if (created is not null && Marshal.IsComObject(created)) Marshal.ReleaseComObject(created);
            if (fallback is not null && Marshal.IsComObject(fallback)) Marshal.ReleaseComObject(fallback);
            if (managerObject is not null && Marshal.IsComObject(managerObject)) Marshal.ReleaseComObject(managerObject);
            if (shellObject is not null && Marshal.IsComObject(shellObject)) Marshal.ReleaseComObject(shellObject);
        }
    }

    public VirtualDesktopTopologyResult EnsureTopology(IReadOnlyList<SavedVirtualDesktop> savedDesktops)
    {
        SavedVirtualDesktop[] saved = (savedDesktops ?? [])
            .Where(desktop => Guid.TryParse(desktop.DesktopId, out _))
            .OrderBy(desktop => desktop.Index)
            .ToArray();
        if (saved.Length == 0)
            return new(VirtualDesktopAssociationStatus.Available, new Dictionary<Guid, Guid>());

        // This private Shell interface is intentionally build-selected because Windows 11
        // 24H2 inserted a vtable member before CreateDesktop.
        if (!SupportsPrivateWindows11Api())
            return new(VirtualDesktopAssociationStatus.Unsupported, new Dictionary<Guid, Guid>(),
                Message: "Private virtual-desktop topology APIs are unsupported on this Windows build.");

        object? shellObject = null;
        object? managerObject = null;
        var desktopObjects = new List<object>();
        try
        {
            Type shellType = Type.GetTypeFromCLSID(ImmersiveShellClsid)
                ?? throw new PlatformNotSupportedException("Immersive Shell is unavailable.");
            shellObject = Activator.CreateInstance(shellType)
                ?? throw new PlatformNotSupportedException("Immersive Shell is unavailable.");
            var provider = (IServiceProvider)shellObject;
            managerObject = provider.QueryService(
                ref VirtualDesktopManagerInternalClsid,
                ref VirtualDesktopManagerInternalIid);
            IDesktopManagerFacade manager = CreatePrivateManager(managerObject);
            List<Guid> actual = ReadDesktopIds(manager, desktopObjects);
            int created = 0;
            while (actual.Count < saved.Length)
            {
                IVirtualDesktop desktop = manager.CreateDesktop();
                desktopObjects.Add(desktop);
                Guid id = desktop.GetId();
                if (id == Guid.Empty || actual.Contains(id))
                    throw new COMException("Windows returned an invalid virtual desktop.");
                actual.Add(id);
                created++;
            }

            IReadOnlyDictionary<Guid, Guid> map = MapSavedDesktops(saved, actual);
            return new(VirtualDesktopAssociationStatus.Available, map, created,
                created == 0 ? "Existing virtual desktops were mapped by saved order."
                    : $"Created {created} missing virtual desktop(s) and mapped the saved topology.");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or
            PlatformNotSupportedException or InvalidOperationException)
        {
            return new(VirtualDesktopAssociationStatus.Unavailable, new Dictionary<Guid, Guid>(),
                Message: ex.Message);
        }
        finally
        {
            foreach (object desktop in desktopObjects.Distinct())
                if (Marshal.IsComObject(desktop)) Marshal.ReleaseComObject(desktop);
            if (managerObject is not null && Marshal.IsComObject(managerObject))
                Marshal.ReleaseComObject(managerObject);
            if (shellObject is not null && Marshal.IsComObject(shellObject))
                Marshal.ReleaseComObject(shellObject);
        }
    }

    internal static IReadOnlyDictionary<Guid, Guid> MapSavedDesktops(
        IReadOnlyList<SavedVirtualDesktop> savedDesktops,
        IReadOnlyList<Guid> actualDesktops)
    {
        SavedVirtualDesktop[] saved = savedDesktops.OrderBy(desktop => desktop.Index).ToArray();
        if (actualDesktops.Count < saved.Length)
            throw new ArgumentException("The current topology must contain at least the saved desktop count.");

        var map = new Dictionary<Guid, Guid>();
        var used = new HashSet<Guid>();
        foreach (SavedVirtualDesktop item in saved)
        {
            Guid savedId = Guid.Parse(item.DesktopId);
            if (actualDesktops.Contains(savedId) && used.Add(savedId))
                map[savedId] = savedId;
        }
        foreach (SavedVirtualDesktop item in saved)
        {
            Guid savedId = Guid.Parse(item.DesktopId);
            if (map.ContainsKey(savedId)) continue;
            Guid candidate = item.Index < actualDesktops.Count && !used.Contains(actualDesktops[item.Index])
                ? actualDesktops[item.Index]
                : actualDesktops.First(id => !used.Contains(id));
            used.Add(candidate);
            map[savedId] = candidate;
        }
        return map;
    }

    private static List<Guid> ReadDesktopIds(
        IDesktopManagerFacade manager,
        ICollection<object> desktopObjects)
    {
        manager.GetDesktops(out IObjectArray desktops);
        try
        {
            desktops.GetCount(out int count);
            var result = new List<Guid>(count);
            Guid iid = typeof(IVirtualDesktop).GUID;
            for (int index = 0; index < count; index++)
            {
                desktops.GetAt(index, ref iid, out object desktopObject);
                desktopObjects.Add(desktopObject);
                Guid id = ((IVirtualDesktop)desktopObject).GetId();
                if (id != Guid.Empty) result.Add(id);
            }
            return result;
        }
        finally
        {
            if (Marshal.IsComObject(desktops)) Marshal.ReleaseComObject(desktops);
        }
    }
    public VirtualDesktopCaptureResult TryGetWindowDesktopId(IntPtr hWnd)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            return new VirtualDesktopCaptureResult(VirtualDesktopAssociationStatus.Unsupported);
        if (hWnd == IntPtr.Zero)
            return new VirtualDesktopCaptureResult(VirtualDesktopAssociationStatus.Rejected);

        try
        {
            IVirtualDesktopManager manager = CreateManager();
            int idResult = manager.GetWindowDesktopId(hWnd, out Guid desktopId);
            int currentResult = manager.IsWindowOnCurrentVirtualDesktop(
                hWnd,
                out bool isOnCurrentDesktop);
            return idResult >= 0 && desktopId != Guid.Empty
                ? new VirtualDesktopCaptureResult(
                    VirtualDesktopAssociationStatus.Available,
                    desktopId,
                    currentResult >= 0 ? isOnCurrentDesktop : null)
                : new VirtualDesktopCaptureResult(VirtualDesktopAssociationStatus.Unavailable);
        }
        catch (COMException) { return new VirtualDesktopCaptureResult(VirtualDesktopAssociationStatus.Unavailable); }
        catch (PlatformNotSupportedException) { return new VirtualDesktopCaptureResult(VirtualDesktopAssociationStatus.Unsupported); }
        catch (InvalidCastException) { return new VirtualDesktopCaptureResult(VirtualDesktopAssociationStatus.Unavailable); }
    }

    public VirtualDesktopMoveResult TryMoveWindowToDesktop(IntPtr hWnd, Guid desktopId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            return new VirtualDesktopMoveResult(VirtualDesktopAssociationStatus.Unsupported);
        if (hWnd == IntPtr.Zero || desktopId == Guid.Empty)
            return new VirtualDesktopMoveResult(VirtualDesktopAssociationStatus.Rejected);

        VirtualDesktopMoveResult? privateFailure = null;
        if (SupportsPrivateWindows11Api())
        {
            VirtualDesktopMoveResult privateResult = TryMoveWindowWithApplicationView(hWnd, desktopId);
            if (privateResult.Status == VirtualDesktopAssociationStatus.Available)
                return privateResult;
            privateFailure = privateResult;
        }

        try
        {
            int hr = CreateManager().MoveWindowToDesktop(hWnd, ref desktopId);
            return hr >= 0
                ? new VirtualDesktopMoveResult(VirtualDesktopAssociationStatus.Available)
                : privateFailure ?? new VirtualDesktopMoveResult(
                    VirtualDesktopAssociationStatus.Rejected,
                    $"The public Shell move failed with HRESULT 0x{hr:X8}.");
        }
        catch (COMException ex)
        {
            return privateFailure ?? new VirtualDesktopMoveResult(
                VirtualDesktopAssociationStatus.Rejected,
                $"The public Shell move failed (0x{ex.HResult:X8}): {ex.Message}");
        }
        catch (PlatformNotSupportedException ex)
        {
            return privateFailure ?? new VirtualDesktopMoveResult(
                VirtualDesktopAssociationStatus.Unsupported,
                ex.Message);
        }
        catch (InvalidCastException ex)
        {
            return privateFailure ?? new VirtualDesktopMoveResult(
                VirtualDesktopAssociationStatus.Unavailable,
                ex.Message);
        }
    }

    private static VirtualDesktopMoveResult TryMoveWindowWithApplicationView(
        IntPtr hWnd,
        Guid desktopId)
    {
        object? shellObject = null;
        object? managerObject = null;
        object? collectionObject = null;
        IVirtualDesktop? targetDesktop = null;
        IObjectArray? desktops = null;
        try
        {
            Type shellType = Type.GetTypeFromCLSID(ImmersiveShellClsid)
                ?? throw new PlatformNotSupportedException("Immersive Shell is unavailable.");
            shellObject = Activator.CreateInstance(shellType)
                ?? throw new PlatformNotSupportedException("Immersive Shell is unavailable.");
            var provider = (IServiceProvider)shellObject;
            managerObject = provider.QueryService(
                ref VirtualDesktopManagerInternalClsid,
                ref VirtualDesktopManagerInternalIid);
            collectionObject = provider.QueryService(
                ref ApplicationViewCollectionIid,
                ref ApplicationViewCollectionIid);
            IDesktopManagerFacade manager = CreatePrivateManager(managerObject);
            var collection = (IApplicationViewCollection)collectionObject;

            manager.GetDesktops(out desktops);
            desktops.GetCount(out int desktopCount);
            Guid desktopInterfaceId = typeof(IVirtualDesktop).GUID;
            for (int index = 0; index < desktopCount; index++)
            {
                desktops.GetAt(index, ref desktopInterfaceId, out object desktopObject);
                var candidate = (IVirtualDesktop)desktopObject;
                if (candidate.GetId() == desktopId)
                {
                    targetDesktop = candidate;
                    break;
                }
                if (Marshal.IsComObject(candidate)) Marshal.ReleaseComObject(candidate);
            }
            if (targetDesktop is null)
                return new VirtualDesktopMoveResult(
                    VirtualDesktopAssociationStatus.Unavailable,
                    "The requested desktop was not present in the Shell desktop collection.");

            // Owned application windows such as IObit's visible main UI can have distinct Shell
            // views for the hidden root owner and visible popup. Moving only the popup gives it
            // the target GUID while the root owner keeps the application visible on the source
            // desktop. Move every recognized view in the root/main pair, root first.
            IntPtr rootOwner = Native.NativeMethodsWindow.GetAncestor(
                hWnd,
                Native.NativeMethodsWindow.GA_ROOTOWNER);
            bool movedAny = false;
            COMException? lastMoveError = null;
            foreach (IntPtr candidate in new[] { rootOwner, hWnd }
                         .Where(value => value != IntPtr.Zero).Distinct())
            {
                IApplicationView? view = null;
                try
                {
                    int hr = collection.GetViewForHwnd(candidate, out view);
                    if (hr < 0 || view is null) continue;
                    manager.MoveViewToDesktop(view, targetDesktop);
                    movedAny = true;
                }
                catch (COMException ex)
                {
                    lastMoveError = ex;
                }
                finally
                {
                    if (view is not null && Marshal.IsComObject(view))
                        Marshal.ReleaseComObject(view);
                }
            }
            if (!movedAny)
                return new VirtualDesktopMoveResult(
                    VirtualDesktopAssociationStatus.Unavailable,
                    lastMoveError is null
                        ? "The Shell application-view collection did not recognize the window ownership tree."
                        : $"Shell rejected every view in the window ownership tree (0x{lastMoveError.HResult:X8}): {lastMoveError.Message}");

            return new VirtualDesktopMoveResult(VirtualDesktopAssociationStatus.Available);
        }
        catch (COMException ex)
        {
            return new VirtualDesktopMoveResult(
                VirtualDesktopAssociationStatus.Rejected,
                $"Shell rejected the move (0x{ex.HResult:X8}): {ex.Message}");
        }
        catch (PlatformNotSupportedException ex)
        {
            return new VirtualDesktopMoveResult(VirtualDesktopAssociationStatus.Unsupported, ex.Message);
        }
        catch (InvalidCastException ex)
        {
            return new VirtualDesktopMoveResult(VirtualDesktopAssociationStatus.Unavailable, ex.Message);
        }
        finally
        {
            if (targetDesktop is not null && Marshal.IsComObject(targetDesktop))
                Marshal.ReleaseComObject(targetDesktop);
            if (desktops is not null && Marshal.IsComObject(desktops)) Marshal.ReleaseComObject(desktops);
            if (collectionObject is not null && Marshal.IsComObject(collectionObject))
                Marshal.ReleaseComObject(collectionObject);
            if (managerObject is not null && Marshal.IsComObject(managerObject))
                Marshal.ReleaseComObject(managerObject);
            if (shellObject is not null && Marshal.IsComObject(shellObject))
                Marshal.ReleaseComObject(shellObject);
        }
    }

    private static bool SupportsPrivateWindows11Api() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    private static IDesktopManagerFacade CreatePrivateManager(object managerObject) =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)
            ? new DesktopManager24H2((IVirtualDesktopManagerInternal24H2)managerObject)
            : new DesktopManager23H2((IVirtualDesktopManagerInternal23H2)managerObject);

    private static IVirtualDesktopManager CreateManager()
    {
        Type managerType = Type.GetTypeFromCLSID(VirtualDesktopManagerClsid)
            ?? throw new PlatformNotSupportedException("VirtualDesktopManager is unavailable.");
        object manager = Activator.CreateInstance(managerType)
            ?? throw new PlatformNotSupportedException("VirtualDesktopManager is unavailable.");
        return (IVirtualDesktopManager)manager;
    }

    private static readonly Guid VirtualDesktopManagerClsid =
        new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    private static readonly Guid ImmersiveShellClsid =
        new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static Guid VirtualDesktopManagerInternalClsid =
        new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    private static Guid VirtualDesktopManagerInternalIid =
        new("53F5CA0B-158F-4124-900C-057158060B27");
    private static Guid ApplicationViewCollectionIid =
        new("1841C6D7-4F9D-42C0-AF41-8747538F10E5");

    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow,
            [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);
        [PreserveSig]
        int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig]
        int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
    private interface IVirtualDesktop
    {
        void IsViewVisible(IApplicationView view);
        Guid GetId();
        IntPtr GetName();
        IntPtr GetWallpaperPath();
        bool IsRemote();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    private interface IObjectArray
    {
        void GetCount(out int count);
        void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("53F5CA0B-158F-4124-900C-057158060B27")]
    private interface IVirtualDesktopManagerInternal23H2
    {
        int GetCount();
        void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
        bool CanViewMoveDesktops(IApplicationView view);
        IVirtualDesktop GetCurrentDesktop();
        void GetDesktops(out IObjectArray desktops);
        [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
        void SwitchDesktop(IVirtualDesktop desktop);
        IVirtualDesktop CreateDesktop();
        void MoveDesktop(IVirtualDesktop desktop, int index);
        void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("53F5CA0B-158F-4124-900C-057158060B27")]
    private interface IVirtualDesktopManagerInternal24H2
    {
        int GetCount();
        void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
        bool CanViewMoveDesktops(IApplicationView view);
        IVirtualDesktop GetCurrentDesktop();
        void GetDesktops(out IObjectArray desktops);
        [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
        void SwitchDesktop(IVirtualDesktop desktop);
        void SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);
        IVirtualDesktop CreateDesktop();
        void MoveDesktop(IVirtualDesktop desktop, int index);
        void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
    }

    private interface IDesktopManagerFacade
    {
        int GetCount();
        void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
        IVirtualDesktop GetCurrentDesktop();
        void GetDesktops(out IObjectArray desktops);
        IVirtualDesktop CreateDesktop();
        void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
    }

    private sealed class DesktopManager23H2(IVirtualDesktopManagerInternal23H2 manager)
        : IDesktopManagerFacade
    {
        public int GetCount() => manager.GetCount();
        public void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop) =>
            manager.MoveViewToDesktop(view, desktop);
        public IVirtualDesktop GetCurrentDesktop() => manager.GetCurrentDesktop();
        public void GetDesktops(out IObjectArray desktops) => manager.GetDesktops(out desktops);
        public IVirtualDesktop CreateDesktop() => manager.CreateDesktop();
        public void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback) =>
            manager.RemoveDesktop(desktop, fallback);
    }

    private sealed class DesktopManager24H2(IVirtualDesktopManagerInternal24H2 manager)
        : IDesktopManagerFacade
    {
        public int GetCount() => manager.GetCount();
        public void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop) =>
            manager.MoveViewToDesktop(view, desktop);
        public IVirtualDesktop GetCurrentDesktop() => manager.GetCurrentDesktop();
        public void GetDesktops(out IObjectArray desktops) => manager.GetDesktops(out desktops);
        public IVirtualDesktop CreateDesktop() => manager.CreateDesktop();
        public void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback) =>
            manager.RemoveDesktop(desktop, fallback);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
    private interface IApplicationView
    {
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
    private interface IApplicationViewCollection
    {
        [PreserveSig] int GetViews(out IObjectArray array);
        [PreserveSig] int GetViewsByZOrder(out IObjectArray array);
        [PreserveSig] int GetViewsByAppUserModelId(
            [MarshalAs(UnmanagedType.LPWStr)] string id,
            out IObjectArray array);
        [PreserveSig] int GetViewForHwnd(IntPtr hWnd, out IApplicationView view);
    }
}
