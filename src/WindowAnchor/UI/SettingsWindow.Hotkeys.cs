using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.UI;

public partial class SettingsWindow
{
    private readonly List<HotkeyRow> _hotkeyRows = new();
    private HotkeyRow? _recordingRow;

    private void InitialiseHotkeyUI()
    {
        _suppressToggle = true;
        HotkeysToggle.IsChecked = _settingsService.Settings.HotkeysEnabled;
        _suppressToggle = false;

        // Build HotkeyRow list from resolved shortcuts (defaults + custom overrides)
        _hotkeyRows.Clear();
        var resolved = HotkeyService.GetResolvedShortcuts(_settingsService.Settings);
        for (int i = 0; i < HotkeyService.Defaults.Length; i++)
        {
            var def = HotkeyService.Defaults[i];
            var res = resolved[i];
            bool isCustom = res.Modifiers != def.Modifiers || res.Key != def.Key;
            _hotkeyRows.Add(new HotkeyRow
            {
                ActionId         = def.ActionId,
                ActionName       = def.ActionName,
                Modifiers        = res.Modifiers,
                Key              = res.Key,
                DisplayShortcut  = res.DisplayShortcut,
                DefaultModifiers = def.Modifiers,
                DefaultKey       = def.Key,
                IsCustom         = isCustom,
            });
        }
        HotkeyList.ItemsSource = _hotkeyRows;
    }

    private void OnHotkeysToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressToggle) return;
        bool enabled = HotkeysToggle.IsChecked == true;
        _settingsService.Settings.HotkeysEnabled = enabled;
        _settingsService.Save();

        // Notify the app to register/unregister hotkeys
        if (System.Windows.Application.Current is App app)
            app.ApplyHotkeySettings();
    }

    // ── Hotkey recording ─────────────────────────────────────────────────────

    private void OnChangeHotkey(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not HotkeyRow row) return;

        // Cancel any existing recording
        CancelHotkeyRecording();

        _recordingRow = row;
        row.IsRecording = true;
        row.DisplayShortcut = "Press keys\u2026";
    }

    private void OnResetHotkey(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not HotkeyRow row) return;

        CancelHotkeyRecording();

        row.Modifiers = row.DefaultModifiers;
        row.Key       = row.DefaultKey;
        row.IsCustom  = false;

        SaveCustomHotkeys();
    }

    private void OnHotkeyRecordKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_recordingRow == null) return;

        // Escape → cancel recording
        if (e.Key == Key.Escape)
        {
            CancelHotkeyRecording();
            e.Handled = true;
            return;
        }

        // Resolve the actual key (Alt combos send Key.System)
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore standalone modifier presses
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var mods = Keyboard.Modifiers;
        // Require at least one modifier
        if (mods == ModifierKeys.None) return;

        _recordingRow.Modifiers = mods;
        _recordingRow.Key       = key;
        _recordingRow.IsRecording = false;
        _recordingRow.IsCustom  = mods != _recordingRow.DefaultModifiers || key != _recordingRow.DefaultKey;
        _recordingRow = null;

        SaveCustomHotkeys();
        e.Handled = true;
    }

    private void CancelHotkeyRecording()
    {
        if (_recordingRow == null) return;
        // Restore previous display text
        _recordingRow.DisplayShortcut = HotkeyService.FormatShortcut(_recordingRow.Modifiers, _recordingRow.Key);
        _recordingRow.IsRecording = false;
        _recordingRow = null;
    }

    private void SaveCustomHotkeys()
    {
        // Build list of custom (non-default) bindings
        var customs = new List<HotkeyBinding>();
        foreach (var row in _hotkeyRows)
        {
            if (row.IsCustom)
            {
                customs.Add(new HotkeyBinding
                {
                    ActionId  = row.ActionId,
                    Modifiers = HotkeyService.FormatModifiers(row.Modifiers),
                    KeyName   = row.Key.ToString(),
                });
            }
        }
        _settingsService.Settings.CustomHotkeys = customs.Count > 0 ? customs : null;
        _settingsService.Save();

        // Re-register hotkeys with new bindings
        if (System.Windows.Application.Current is App app)
            app.ApplyHotkeySettings();
    }

}
