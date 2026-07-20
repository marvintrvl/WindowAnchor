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
}
