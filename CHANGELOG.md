# Changelog

All notable changes to WindowAnchor will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

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