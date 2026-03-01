using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace WindowAnchor.Services;

/// <summary>
/// Registers and dispatches system-wide keyboard shortcuts via the
/// Windows <c>RegisterHotKey</c> / <c>UnregisterHotKey</c> API.
/// Call <see cref="Initialise"/> once on the UI thread, then <see cref="Register"/>
/// for each shortcut.  Dispose to clean up.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    // ── P/Invoke ──────────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;

    // Modifier flags for RegisterHotKey
    private const uint MOD_ALT     = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT   = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000; // prevent repeat while held

    // ── State ─────────────────────────────────────────────────────────────

    private HwndSource? _hwndSource;
    private readonly Dictionary<int, Action> _callbacks = new();
    private int _nextId = 9100; // arbitrary base to avoid collisions
    private bool _disposed;

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a hidden message-only window and hooks its message pump.
    /// Must be called on the UI (dispatcher) thread.
    /// </summary>
    public void Initialise()
    {
        if (_hwndSource != null) return;

        var parameters = new HwndSourceParameters("WindowAnchor_HotkeySink")
        {
            Width  = 0,
            Height = 0,
            // WS_POPUP so it doesn't appear anywhere
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);
        AppLogger.Info("HotkeyService: initialised message window");
    }

    /// <summary>
    /// Registers a global hotkey.  Returns the registration id (&gt;0) on
    /// success, or 0 if registration failed (e.g. shortcut already taken).
    /// </summary>
    public int Register(ModifierKeys modifiers, Key key, Action callback)
    {
        if (_hwndSource == null) return 0;

        uint fsModifiers = MOD_NOREPEAT;
        if (modifiers.HasFlag(ModifierKeys.Alt))     fsModifiers |= MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Control)) fsModifiers |= MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift))   fsModifiers |= MOD_SHIFT;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        int id  = _nextId++;

        if (!RegisterHotKey(_hwndSource.Handle, id, fsModifiers, vk))
        {
            AppLogger.Warn($"HotkeyService: RegisterHotKey failed for id {id} ({modifiers}+{key}) — error {Marshal.GetLastWin32Error()}");
            return 0;
        }

        _callbacks[id] = callback;
        AppLogger.Info($"HotkeyService: registered {modifiers}+{key} → id {id}");
        return id;
    }

    /// <summary>Unregisters all hotkeys and re-registers none.</summary>
    public void UnregisterAll()
    {
        if (_hwndSource == null) return;
        foreach (int id in _callbacks.Keys)
            UnregisterHotKey(_hwndSource.Handle, id);
        _callbacks.Clear();
        AppLogger.Info("HotkeyService: unregistered all hotkeys");
    }

    // ── Default shortcuts ─────────────────────────────────────────────────

    /// <summary>
    /// Describes one keyboard shortcut for display in the Settings UI.
    /// </summary>
    public record HotkeyInfo(string ActionId, string ActionName, string DisplayShortcut, ModifierKeys Modifiers, Key Key);

    /// <summary>
    /// The built-in default shortcut definitions.  Used both for registration
    /// and for the read-only table in Settings.
    /// </summary>
    public static readonly HotkeyInfo[] Defaults = new[]
    {
        new HotkeyInfo("QuickSave",      "Quick Save",               "Ctrl + Alt + S",           ModifierKeys.Control | ModifierKeys.Alt,                     Key.S),
        new HotkeyInfo("RestoreDefault", "Restore Default",          "Ctrl + Alt + R",           ModifierKeys.Control | ModifierKeys.Alt,                     Key.R),
        new HotkeyInfo("RestoreSlot1",   "Restore Workspace 1",      "Ctrl + Alt + 1",           ModifierKeys.Control | ModifierKeys.Alt,                     Key.D1),
        new HotkeyInfo("RestoreSlot2",   "Restore Workspace 2",      "Ctrl + Alt + 2",           ModifierKeys.Control | ModifierKeys.Alt,                     Key.D2),
        new HotkeyInfo("RestoreSlot3",   "Restore Workspace 3",      "Ctrl + Alt + 3",           ModifierKeys.Control | ModifierKeys.Alt,                     Key.D3),
        new HotkeyInfo("SwitchSlot1",    "Switch to Workspace 1",    "Ctrl + Alt + Shift + 1",   ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.D1),
        new HotkeyInfo("SwitchSlot2",    "Switch to Workspace 2",    "Ctrl + Alt + Shift + 2",   ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.D2),
        new HotkeyInfo("SwitchSlot3",    "Switch to Workspace 3",    "Ctrl + Alt + Shift + 3",   ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.D3),
        new HotkeyInfo("OpenSettings",   "Open Settings",            "Ctrl + Alt + W",           ModifierKeys.Control | ModifierKeys.Alt,                     Key.W),
        new HotkeyInfo("SwitchDefault",  "Switch to Default",        "Ctrl + Alt + Shift + W",   ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.W),
    };

    // ── Shortcut resolution (merge defaults + custom overrides) ───────────

    /// <summary>
    /// Returns the effective shortcuts by merging <see cref="Defaults"/> with
    /// any custom overrides stored in <paramref name="settings"/>.
    /// </summary>
    public static List<HotkeyInfo> GetResolvedShortcuts(Models.AppSettings settings)
    {
        var result = new List<HotkeyInfo>();
        foreach (var d in Defaults)
        {
            var custom = settings.CustomHotkeys?.Find(c => c.ActionId == d.ActionId);
            if (custom != null && Enum.TryParse<Key>(custom.KeyName, out var key))
            {
                var mods = ParseModifiers(custom.Modifiers);
                result.Add(new HotkeyInfo(d.ActionId, d.ActionName, FormatShortcut(mods, key), mods, key));
            }
            else
            {
                result.Add(d);
            }
        }
        return result;
    }

    /// <summary>Parses a modifier string like "Ctrl+Alt" into <see cref="ModifierKeys"/>.</summary>
    public static ModifierKeys ParseModifiers(string s)
    {
        var mods = ModifierKeys.None;
        if (s.Contains("Ctrl", StringComparison.OrdinalIgnoreCase))  mods |= ModifierKeys.Control;
        if (s.Contains("Alt", StringComparison.OrdinalIgnoreCase))   mods |= ModifierKeys.Alt;
        if (s.Contains("Shift", StringComparison.OrdinalIgnoreCase)) mods |= ModifierKeys.Shift;
        return mods;
    }

    /// <summary>Formats a modifier+key combination as a human-readable string.</summary>
    public static string FormatShortcut(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt))     parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift))   parts.Add("Shift");

        string keyName = key switch
        {
            Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3",
            Key.D4 => "4", Key.D5 => "5", Key.D6 => "6", Key.D7 => "7",
            Key.D8 => "8", Key.D9 => "9",
            Key.OemMinus   => "-",  Key.OemPlus    => "=",
            Key.OemComma   => ",",  Key.OemPeriod  => ".",
            Key.OemQuestion => "/", Key.Oem1       => ";",
            Key.Oem7 => "'",       Key.OemOpenBrackets => "[",
            Key.Oem6 => "]",       Key.Oem5 => "\\",
            _ => key.ToString(),
        };
        parts.Add(keyName);
        return string.Join(" + ", parts);
    }

    /// <summary>Formats <see cref="ModifierKeys"/> into a string like "Ctrl+Alt".</summary>
    public static string FormatModifiers(ModifierKeys mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt))     parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift))   parts.Add("Shift");
        return string.Join("+", parts);
    }

    // ── Message pump ──────────────────────────────────────────────────────

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_callbacks.TryGetValue(id, out var cb))
            {
                try { cb(); }
                catch (Exception ex) { AppLogger.Warn($"HotkeyService: callback error for id {id} — {ex.Message}"); }
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource?.Dispose();
        _hwndSource = null;
        AppLogger.Info("HotkeyService: disposed");
    }
}
