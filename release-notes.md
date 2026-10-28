# WindowAnchor v1.7.0.1 — Restore Stability

## Summary

This release fixes the crash reported in [issue #16](https://github.com/marvintrvl/WindowAnchor/issues/16): after a layout restored successfully, WindowAnchor could close and its global hotkeys stopped working. The app now keeps the restore progress window open through its first render before starting the restore.

## Changes since v1.7.0

- Import or export saved workspaces locally, including a portable, redacted format and an import preview that creates a separate copy.
- Add deterministic, read-only restore simulation fixtures for monitor layouts, browser and app matching, unavailable resources, and path aliases.
- Open the published Mozilla-signed Firefox connector listing directly from **Set Up Firefox**.

## Verification

- Reproduced the issue's `Hwnd of zero is not valid` exception in a rapid progress-window close test before the fix; 25 rapid cycles completed without the exception afterward.
- All 380 Release tests passed locally.

## Downloads

- `WindowAnchor-v1.7.0.1.exe` — self-contained Windows x64 application.
- `WindowAnchor-Browser-Connector-v1.7.0.1.zip` — optional Chromium connector.
- `WindowAnchor-Firefox-Connector-AMO-v1.7.0.1.zip` — unsigned Firefox review/development package. Firefox Release and Beta users should install the [Mozilla-signed connector](https://addons.mozilla.org/en-US/firefox/addon/windowanchor-browser-connector/).
- `WindowAnchor-Terminal-Integration-v1.7.0.1.ps1` — optional PowerShell prompt integration.
- `SHA256SUMS.txt` — SHA-256 checksums for the four versioned downloads.

The executable is not digitally signed, so Windows may show a security prompt.

## Updating

1. Exit WindowAnchor from its tray menu.
2. Download `WindowAnchor-v1.7.0.1.exe` and verify its checksum.
3. Replace the previous executable or run the new file directly. Existing workspace data remains in `%AppData%\WindowAnchor`.
