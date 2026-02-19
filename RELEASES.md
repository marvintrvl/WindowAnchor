# Release Notes

All notable changes to the **WindowAnchor** project will be documented in this file.

## [1.1.0] - 2026-02-19
### Added
- **Save Progress Window**: Visual progress bar showing each window being processed during workspace save when file detection is enabled.
- **Selective Restore**: "Restore Selected Monitors…" context-menu option lets users restore only specific monitors from a saved workspace.
- **Per-Monitor Save**: Choose which monitors to include when saving via the Save Workspace dialog.

### Changed
- **Unified Workspaces**: Monitor Profiles feature removed; all saved configurations now use the Workspace model.
- **Restore Notifications**: Result balloon is shown after restore completes, not before.

### Migration
- Existing Monitor Profiles are automatically converted to Workspaces on first launch (positions preserved; file tracking not carried over).

---

## [1.0.0] - 2026-02-18
### Added
- **Monitor Profiles**: Automatically save and restore window positions based on monitor layout fingerprints.
- **Workspace Snapshots**: Capture active application states, including open files (via Tier 1 title parsing and Tier 2 jump-list integration).
- **Restoration Engine**: Multi-phase restore process to relaunch apps and reposition windows with DPI awareness.
- **Settings UI**: Win11-styled management for monitor profiles and saved workspaces.
- **Tray Integration**: Quick access to "Restore Now", "Save Now", and workspace management from the system tray.
- **Self-Contained Build**: Support for publishing as a single, zero-dependency executable.
