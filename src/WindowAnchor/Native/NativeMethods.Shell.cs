using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowAnchor.Native;

/// <summary>
/// P/Invoke and COM declarations for the Windows Shell property system.
/// <para>
/// Used to read the <c>AppUserModelID</c> (AUMID) of a window and of a
/// <c>.lnk</c> shortcut.  This is the only reliable way to tell a Chrome/Brave/Edge
/// <em>web-app window</em> (an installed PWA such as Insilico Terminal or aggr.trade)
/// apart from an ordinary browser window: both live in the same
/// <c>chrome.exe</c>/<c>brave.exe</c> process and use the same window class
/// (<c>Chrome_WidgetWin_1</c>), but Chromium assigns every installed web app its own
/// per-window AUMID so it gets its own taskbar group.
/// </para>
/// </summary>
public static class NativeMethodsShell
{
    // ── Property system ───────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId   = formatId;
            PropertyId = propertyId;
        }
    }

    /// <summary>PKEY_AppUserModel_ID — {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5.</summary>
    public static readonly PropertyKey PKEY_AppUserModel_ID =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    /// <summary>
    /// Minimal PROPVARIANT wrapper. Only VT_LPWSTR / VT_BSTR values are read,
    /// which is all PKEY_AppUserModel_ID ever contains.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public sealed class PropVariant : IDisposable
    {
        private ushort _vt;
        private ushort _reserved1;
        private ushort _reserved2;
        private ushort _reserved3;
        private IntPtr _pointer;
        private IntPtr _pointer2;

        private const ushort VT_EMPTY  = 0;
        private const ushort VT_BSTR   = 8;
        private const ushort VT_LPWSTR = 31;

        /// <summary>Returns the string value, or <c>null</c> when the variant holds something else.</summary>
        public string? AsString()
        {
            if (_vt is VT_LPWSTR or VT_BSTR)
                return Marshal.PtrToStringUni(_pointer);
            return null;
        }

        public bool IsEmpty => _vt == VT_EMPTY;

        public void Dispose()
        {
            try { PropVariantClear(this); } catch { /* best effort */ }
            _vt = VT_EMPTY;
            GC.SuppressFinalize(this);
        }

        ~PropVariant() => Dispose();
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear([In, Out] PropVariant pvar);

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, [In, Out] PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, [In] PropVariant pv);
        [PreserveSig] int Commit();
    }

    /// <summary>Returns the property store of a window (contains its AppUserModelID, if set).</summary>
    [DllImport("shell32.dll", SetLastError = true)]
    public static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    public static readonly Guid IID_IPropertyStore =
        new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    // ── Shell links (.lnk) ────────────────────────────────────────────────────

    /// <summary>CLSID_ShellLink coclass — <c>new ShellLinkCoClass()</c> maps to CoCreateInstance.</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    public class ShellLinkCoClass { }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
                     int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
                             int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName,
                  [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    /// <summary>SLGP_RAWPATH — return the unexpanded target path.</summary>
    public const uint SLGP_RAWPATH = 0x4;

    /// <summary>STGM_READ — open the .lnk read-only.</summary>
    public const uint STGM_READ = 0x0;

    // ── Packaged-process AppUserModelID ────────────────────────────────────────
    // Store/MSIX apps (TradingView, Notepad, …) usually do NOT set an explicit AUMID on their
    // window, so SHGetPropertyStoreForWindow returns nothing. The reliable source is the
    // *process*: GetApplicationUserModelId returns "PackageFamilyName!AppId" for any packaged
    // process. That AUMID is what shell:AppsFolder needs to relaunch the app with full package
    // identity (and therefore its saved settings, e.g. dark theme).

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    /// <summary>Minimal access right needed to query a process's package identity.</summary>
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Returned by GetApplicationUserModelId when the process is not packaged.</summary>
    public const int APPMODEL_ERROR_NO_APPLICATION = 15703;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetApplicationUserModelId(
        IntPtr hProcess, ref uint applicationUserModelIdLength, [Out] char[]? applicationUserModelId);

    /// <summary>Converts a package full name into its version-independent family name.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int PackageFamilyNameFromFullName(
        string packageFullName,
        ref uint packageFamilyNameLength,
        StringBuilder? packageFamilyName);

    /// <summary>Gets registered package full names for one family in the current user context.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetPackagesByPackageFamily(
        string packageFamilyName,
        ref uint count,
        IntPtr packageFullNames,
        ref uint bufferLength,
        IntPtr buffer);

    /// <summary>Gets the installation path for a staged package full name.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetStagedPackagePathByFullName(
        string packageFullName,
        ref uint pathLength,
        StringBuilder? path);

    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
}
