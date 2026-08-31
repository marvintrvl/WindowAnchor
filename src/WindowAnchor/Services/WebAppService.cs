using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using WindowAnchor.Native;

namespace WindowAnchor.Services;

/// <summary>
/// Describes an installed browser web app (PWA / "Create shortcut… → Open as window"),
/// e.g. Insilico Terminal or aggr.trade installed from Chrome or Brave.
/// </summary>
/// <param name="AppUserModelId">Per-window AUMID Chromium assigns to the app window.</param>
/// <param name="ShortcutPath">Full path of the Start-Menu/Desktop <c>.lnk</c> that launches it.</param>
/// <param name="TargetPath">Shortcut target (usually <c>chrome_proxy.exe</c> or <c>chrome.exe</c>).</param>
/// <param name="Arguments">Shortcut arguments, e.g. <c>--profile-directory=Default --app-id=abc…</c>.</param>
/// <param name="DisplayName">Shortcut file name without extension, used for logging/UI.</param>
public record WebAppInfo(
    string AppUserModelId,
    string ShortcutPath,
    string TargetPath,
    string Arguments,
    string DisplayName);

/// <summary>
/// Identifies browser web-app windows and resolves how to relaunch them.
///
/// <para><b>The problem this solves.</b> An installed web app (Insilico Terminal,
/// aggr.trade, …) opens in the ordinary <c>chrome.exe</c> / <c>brave.exe</c> process and
/// uses the same window class as a normal browser window. Identifying a window only by
/// executable path + class name therefore makes every PWA window look like "a Chrome
/// window", so restoring a layout launched a plain browser instead of the app.</para>
///
/// <para><b>The fix.</b> Chromium sets a distinct <c>AppUserModelID</c> on every web-app
/// window (that is how the app gets its own taskbar icon). We read that AUMID per window,
/// store it in the snapshot, and match it against the AUMID recorded on the Start-Menu
/// shortcut Chromium created when the app was installed. Restoring then launches that
/// shortcut, and window matching requires an exact AUMID match so a PWA entry can never
/// consume a plain browser window (or vice versa).</para>
/// </summary>
public class WebAppService
{
    /// <summary>Chromium-based browsers whose windows may actually be web-app windows.</summary>
    private static readonly HashSet<string> ChromiumProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "opera", "opera_gx", "vivaldi", "chromium", "thorium", "arc",
    };

    // AUMID (lower-case) → web app. Null until the first lookup builds it.
    private Dictionary<string, WebAppInfo>? _shortcutIndex;

    // When the index was last built. A lookup miss is the normal case for every plain browser
    // window, so the index must NOT be rebuilt on every miss — that would rescan the whole
    // Start Menu once per browser window. Rebuild at most once per this interval.
    private DateTime _indexBuiltAt = DateTime.MinValue;
    private static readonly TimeSpan RebuildCooldown = TimeSpan.FromMinutes(2);

    /// <summary>Returns <c>true</c> when the process is a Chromium-based browser.</summary>
    public static bool IsChromiumBrowser(string processName) =>
        ChromiumProcessNames.Contains(processName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase));

    // ── Friendly display name ─────────────────────────────────────────────────

    /// <summary>
    /// Returns a human-friendly name for a window when it is an installed browser web app
    /// (e.g. "Insilico Terminal" instead of "brave"), or <c>null</c> for anything else.
    /// Purely cosmetic — used by the Save Workspace dialog.
    /// </summary>
    public string? ResolveWebAppName(string processName, string appUserModelId)
    {
        if (!IsChromiumBrowser(processName))     return null;
        if (string.IsNullOrEmpty(appUserModelId)) return null;

        var info = FindByAumid(appUserModelId);
        if (info != null) return info.DisplayName;

        // Shortcut missing but the AUMID still looks like a web app — fall back to the app id.
        return LooksLikeWebAppAumid(appUserModelId) ? "Web App" : null;
    }

    // ── Per-window AUMID ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads the explicit <c>AppUserModelID</c> of a window via
    /// <c>SHGetPropertyStoreForWindow</c>. Returns an empty string when the window has no
    /// explicit AUMID (which is the normal case for most classic desktop apps).
    /// </summary>
    public static string GetWindowAppUserModelId(IntPtr hWnd)
    {
        NativeMethodsShell.IPropertyStore? store = null;
        try
        {
            var iid = NativeMethodsShell.IID_IPropertyStore;
            int hr = NativeMethodsShell.SHGetPropertyStoreForWindow(hWnd, ref iid, out store);
            if (hr != 0 || store == null) return "";

            using var pv = new NativeMethodsShell.PropVariant();
            var key = NativeMethodsShell.PKEY_AppUserModel_ID;
            if (store.GetValue(ref key, pv) != 0) return "";

            return pv.AsString() ?? "";
        }
        catch (Exception ex)
        {
            AppLogger.Debug(
                "web_app.window_aumid_query_failed",
                "Could not query a window AppUserModelID",
                ex,
                LogField.Public("hwnd", hWnd),
                LogField.Public("errorCategory", "window_aumid_query"));
            return "";
        }
        finally
        {
            if (store != null) Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>
    /// Returns the <c>AppUserModelID</c> of a running process from its package identity, or an
    /// empty string when the process is not a packaged (Store/MSIX) app or cannot be queried.
    /// <para>
    /// This is the reliable AUMID source for Store apps, whose windows typically carry no
    /// explicit AUMID property. The result (<c>PackageFamilyName!AppId</c>) is what
    /// <c>shell:AppsFolder</c> needs to relaunch the app with full package identity.
    /// </para>
    /// </summary>
    public static string GetProcessAppUserModelId(uint processId)
    {
        IntPtr hProcess = NativeMethodsShell.OpenProcess(
            NativeMethodsShell.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero) return "";

        try
        {
            uint length = 0;
            // First call: probe the required buffer length (expected ERROR_INSUFFICIENT_BUFFER).
            NativeMethodsShell.GetApplicationUserModelId(hProcess, ref length, null);
            if (length == 0) return "";

            var buffer = new char[length];
            int rc = NativeMethodsShell.GetApplicationUserModelId(hProcess, ref length, buffer);
            if (rc != 0) return "";   // non-zero incl. APPMODEL_ERROR_NO_APPLICATION → not packaged

            return new string(buffer, 0, (int)length).TrimEnd('\0');
        }
        catch (Exception ex)
        {
            AppLogger.Debug(
                "web_app.process_aumid_query_failed",
                "Could not query a process AppUserModelID",
                ex,
                LogField.Public("processId", processId),
                LogField.Public("errorCategory", "process_aumid_query"));
            return "";
        }
        finally
        {
            NativeMethodsShell.CloseHandle(hProcess);
        }
    }

    // ── Shortcut index ────────────────────────────────────────────────────────

    /// <summary>
    /// Looks up the installed web app whose shortcut carries <paramref name="aumid"/>.
    /// Rebuilds the shortcut index once on a miss so newly installed apps are picked up
    /// without restarting WindowAnchor.
    /// </summary>
    public WebAppInfo? FindByAumid(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return null;

        string key = aumid.ToLowerInvariant();

        if (_shortcutIndex == null)
        {
            _shortcutIndex = BuildShortcutIndex();
            _indexBuiltAt  = DateTime.UtcNow;
        }

        if (_shortcutIndex.TryGetValue(key, out var hit)) return hit;

        // Miss. Most misses are ordinary browser windows (AUMID "Chrome", "Brave", …), so only
        // rescan when the index is stale — otherwise a newly installed web app would never be
        // picked up, but every plain browser window would trigger a full Start-Menu scan.
        if (!WebAppService.LooksLikeWebAppAumid(aumid)) return null;
        if (DateTime.UtcNow - _indexBuiltAt < RebuildCooldown) return null;

        _shortcutIndex = BuildShortcutIndex();
        _indexBuiltAt  = DateTime.UtcNow;
        return _shortcutIndex.TryGetValue(key, out hit) ? hit : null;
    }

    /// <summary>Forces the next lookup to re-scan the Start Menu and Desktop.</summary>
    public void InvalidateCache()
    {
        _shortcutIndex = null;
        _indexBuiltAt  = DateTime.MinValue;
    }

    /// <summary>
    /// Scans the Start Menu and Desktop (user + all-users) for <c>.lnk</c> files that carry
    /// an explicit AUMID <em>and</em> a Chromium app switch (<c>--app-id=</c> or <c>--app=</c>).
    /// Those are exactly the shortcuts Chromium writes when a web app is installed.
    /// </summary>
    private Dictionary<string, WebAppInfo> BuildShortcutIndex()
    {
        var index = new Dictionary<string, WebAppInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in GetShortcutRoots())
        {
            foreach (var lnk in EnumerateShortcuts(root))
            {
                try
                {
                    var info = ReadShortcut(lnk);
                    if (info == null) continue;
                    if (string.IsNullOrEmpty(info.AppUserModelId)) continue;
                    if (!HasChromiumAppSwitch(info.Arguments)) continue;

                    // First match wins (user Start Menu is scanned before all-users/desktop).
                    index.TryAdd(info.AppUserModelId.ToLowerInvariant(), info);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug(
                        "web_app.shortcut_read_failed",
                        "Could not read a candidate web-app shortcut",
                        ex,
                        LogField.Path("shortcutPath", lnk),
                        LogField.Public("errorCategory", "shortcut_read"));
                }
            }
        }

        AppLogger.Info(
            "web_app.shortcut_index_built",
            "Built the installed web-app shortcut index",
            LogField.Public("webAppCount", index.Count));
        return index;
    }

    private static IEnumerable<string> GetShortcutRoots()
    {
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };

        return roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Recursive .lnk enumeration that tolerates per-folder access errors.</summary>
    private static IEnumerable<string> EnumerateShortcuts(string directory)
    {
        // No yield inside try/catch: collect first, then yield.
        string[] files = Array.Empty<string>();
        try { files = Directory.GetFiles(directory, "*.lnk"); } catch { /* unreadable folder */ }

        foreach (var f in files) yield return f;

        string[] subDirs = Array.Empty<string>();
        try { subDirs = Directory.GetDirectories(directory); } catch { /* unreadable folder */ }

        foreach (var sub in subDirs)
            foreach (var f in EnumerateShortcuts(sub))
                yield return f;
    }

    /// <summary>Reads target, arguments and AUMID out of a <c>.lnk</c> file.</summary>
    private static WebAppInfo? ReadShortcut(string lnkPath)
    {
        object? comObj = null;
        try
        {
            comObj = new NativeMethodsShell.ShellLinkCoClass();
            var link    = (NativeMethodsShell.IShellLinkW)comObj;
            var persist = (NativeMethodsShell.IPersistFile)comObj;

            persist.Load(lnkPath, NativeMethodsShell.STGM_READ);

            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, NativeMethodsShell.SLGP_RAWPATH);

            var args = new StringBuilder(2048);
            link.GetArguments(args, args.Capacity);

            string aumid = "";
            if (comObj is NativeMethodsShell.IPropertyStore store)
            {
                using var pv = new NativeMethodsShell.PropVariant();
                var key = NativeMethodsShell.PKEY_AppUserModel_ID;
                if (store.GetValue(ref key, pv) == 0)
                    aumid = pv.AsString() ?? "";
            }

            return new WebAppInfo(
                AppUserModelId: aumid,
                ShortcutPath:   lnkPath,
                TargetPath:     target.ToString(),
                Arguments:      args.ToString(),
                DisplayName:    Path.GetFileNameWithoutExtension(lnkPath));
        }
        finally
        {
            if (comObj != null) Marshal.ReleaseComObject(comObj);
        }
    }

    // ── Heuristics ────────────────────────────────────────────────────────────

    /// <summary>True when the argument string contains a Chromium app-mode switch.</summary>
    public static bool HasChromiumAppSwitch(string arguments) =>
        arguments.Contains("--app-id=", StringComparison.OrdinalIgnoreCase) ||
        arguments.Contains("--app=",    StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fallback detection when no shortcut is found: Chromium web-app AUMIDs end in the
    /// 32-character extension-style app id (characters <c>a</c>–<c>p</c>) or use the legacy
    /// <c>_crx_</c> prefix. A plain browser window's AUMID is just <c>Chrome</c>,
    /// <c>Brave</c>, <c>Chrome.&lt;profilehash&gt;</c> etc.
    /// </summary>
    public static bool LooksLikeWebAppAumid(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        if (aumid.Contains("_crx_", StringComparison.OrdinalIgnoreCase)) return true;

        // Chromium app ids are lower-case a–p strings. 32 characters is the documented length,
        // but real installs also produce shorter ids (observed: 26), so accept a range.
        // A plain browser AUMID is "Chrome", "Brave" or "Chrome.<hex profile hash>" and never
        // reaches this length in the a–p alphabet.
        string last = aumid.Split('.').Last();
        return IsChromiumAppId(last);
    }

    /// <summary>True for a Chromium extension/app id: 20–32 characters from the a–p alphabet.</summary>
    private static bool IsChromiumAppId(string candidate) =>
        candidate.Length is >= 20 and <= 32 && candidate.All(c => c is >= 'a' and <= 'p');

    /// <summary>
    /// Extracts the 32-character Chromium app id from an AUMID, or <c>null</c>.
    /// Used to build a <c>--app-id=</c> command line when the shortcut is missing.
    /// </summary>
    public static string? ExtractAppIdFromAumid(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return null;

        int crx = aumid.IndexOf("_crx_", StringComparison.OrdinalIgnoreCase);
        if (crx >= 0)
        {
            // Everything after "_crx_" is the app id — its length varies (26 and 32 both occur).
            string tail = aumid[(crx + 5)..].Split('.')[0];
            return tail.Length > 0 ? tail : null;
        }

        string last = aumid.Split('.').Last();
        return IsChromiumAppId(last) ? last : null;
    }
}
