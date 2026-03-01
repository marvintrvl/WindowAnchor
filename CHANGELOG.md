# Changelog

All notable changes to WindowAnchor will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [1.3.0] - 2026-03-01

### Added
- **Monitor Renaming** — Assign custom names to monitors in Settings → Monitors. Aliases replace hardware names (e.g. “Generic PnP Monitor”) throughout all dialogs: Save Workspace, Selective Restore, View & Edit Windows. Aliases are keyed by stable EDID-based MonitorId and persist across reboots.
- **Switch Workspace** — Instant context switching: closes all open windows, then restores the target workspace. Available from the workspace context menu (“Switch to Workspace”), the system tray menu (“Switch to: …”), and via the new **Ctrl+Alt+Shift+W** hotkey (switches to the default workspace).
- **Switch to Default hotkey** — New configurable `SwitchDefault` keyboard shortcut (default: Ctrl+Alt+Shift+W) for one-key context switching to the default workspace.
- **Switch Slot hotkeys** — Three new `SwitchSlot1/2/3` keyboard shortcuts (default: Ctrl+Alt+Shift+1/2/3) for one-key context switching to workspace slots 1, 2, and 3 respectively (mirrors the Restore Ctrl+Alt+1/2/3 hotkeys but closes all open windows first).

### Changed
- Settings window now includes a “Monitors” section listing all connected displays with editable alias text fields.
- System tray “Workspaces” submenu now shows both “Restore:” and “Switch to:” entries for each saved workspace.

## [1.2.0] - 2026-03-01

### Added
- **Default Workspace Setting** — Choose what happens when WindowAnchor starts: restore a specific workspace, restore the most recently saved one, ask via a picker dialog, or do nothing. Configured under Settings → Startup.
- **Selective Window Save** — The save dialog now shows a per-window checkbox list grouped by monitor. Password managers (KeePass, 1Password, Bitwarden, etc.) and incognito/private browser windows are unchecked by default.
- **Global Keyboard Shortcuts** — Six built-in hotkeys for quick save, restore default, restore workspace #1/#2/#3, and open settings (default: Ctrl+Alt+S/R/1/2/3/W). Shortcuts are fully customisable in Settings → Keyboard Shortcuts.
- **Workspace Ordering** — Reorder workspaces via Move Up/Move Down in the context menu. The first three workspaces map to the Ctrl+Alt+1/2/3 hotkeys. Slot badges (#1, #2, #3) and a ★ default indicator are displayed in the workspace list.
- **Set as Default from Context Menu** — Right-click any workspace row → "Set as Default" to mark it as the startup workspace, or "Remove as Default" to clear it.
- **Browser Session Restore** — Chromium-based browsers (Chrome, Edge, Opera, Brave) launched without a specific URL now receive `--restore-last-session` to reopen previous tabs.
- **Settings Persistence** — All new settings (startup behaviour, hotkey customisations, workspace order) are saved to `%AppData%\WindowAnchor\settings.json`.

### Changed
- Save Workspace dialog redesigned from per-monitor checkboxes to a per-window checkbox list with smart exclusions.
- Settings window expanded with Startup Behavior, Keyboard Shortcuts, and workspace ordering sections.
- Tray menu and hotkey-based workspace restore now honour the user's custom display order.

## [1.1.1] - 2026-02-20

### Fixed
- Improved JumpList detection for Office Click-to-Run (MS 365) by bypassing `AppVLP.exe` redirection via process-name indexing.
- Added CRC-64/Jones hash lookup for direct per-app Jump List resolution (reliable for non-default handlers).
- Implemented "Tier 1.5" exact-filename matching against a larger (50) Jump List pool to catch files outside the top 10.
- Resolved Phase 2 restore collision where bare exe launches would "steal" DDE slots from pending document launches.
- Fixed Cursor AI editor support (added as Electron candidate for workspace folder promotion).
- Added missing title patterns for Adobe Acrobat, Notepad3, and Atom.
- Added detailed `[FileDetect]` debug diagnostics to `app.log` for troubleshooting file-path extraction.

## [1.1.0] - 2026-02-19

### Added
- Per-monitor workspace save/restore
- File detection via title parsing and jump lists
- Selective restore dialog (choose which monitors to restore)
- Progress window during workspace save

### Changed
- Unified "Monitor Profiles" and "Workspaces" into single feature

## [1.0.0] - 2026-02-18

### Added
- Initial release
- Basic window position save/restore
- Monitor fingerprinting
- System tray integration