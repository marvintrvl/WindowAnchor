using System;
using System.Runtime.InteropServices;

namespace WindowAnchor.Services;

/// <summary>Outcome from the documented virtual-desktop manager boundary.</summary>
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
public sealed record VirtualDesktopMoveResult(VirtualDesktopAssociationStatus Status);

/// <summary>
/// Boundary for the documented Windows virtual-desktop API. Implementations must never create,
/// enumerate, name, or switch virtual desktops; a failed move is an adaptation, not an error.
/// </summary>
public interface IVirtualDesktopAssociation
{
    VirtualDesktopCaptureResult TryGetWindowDesktopId(IntPtr hWnd);
    VirtualDesktopMoveResult TryMoveWindowToDesktop(IntPtr hWnd, Guid desktopId);
}

/// <summary>
/// Uses only the public <c>IVirtualDesktopManager</c> Shell COM interface available on Windows
/// 10 and later. This interface exposes neither desktop enumeration nor desktop switching.
/// </summary>
public sealed class VirtualDesktopAssociationService : IVirtualDesktopAssociation
{
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

        try
        {
            int hr = CreateManager().MoveWindowToDesktop(hWnd, ref desktopId);
            return new VirtualDesktopMoveResult(hr >= 0
                ? VirtualDesktopAssociationStatus.Available
                : VirtualDesktopAssociationStatus.Rejected);
        }
        catch (COMException) { return new VirtualDesktopMoveResult(VirtualDesktopAssociationStatus.Rejected); }
        catch (PlatformNotSupportedException) { return new VirtualDesktopMoveResult(VirtualDesktopAssociationStatus.Unsupported); }
        catch (InvalidCastException) { return new VirtualDesktopMoveResult(VirtualDesktopAssociationStatus.Unavailable); }
    }

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
}
