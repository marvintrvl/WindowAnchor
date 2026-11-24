# WindowAnchor v1.7.0.2 — Browser Tab Restore

This release fixes the browser tab restore failure reported with Ungoogled Chromium. The desktop app now uses the same JSON field names as the Chromium and Firefox connectors, so saved sessions retain their tabs, groups, window state, and browser profile. The browser bridge also handles connection cleanup without discarding valid replies, and restore detects when a saved session or profile could not be restored. [Issue #19](https://github.com/marvintrvl/WindowAnchor/issues/19)

After updating, save the workspace again with the intended tabs open if an earlier version captured empty browser sessions. Those captures need a fresh save to include the tab data.

## Downloads

- `WindowAnchor-v1.7.0.2.exe` — self-contained Windows x64 application.
- `WindowAnchor-Browser-Connector-v1.7.0.2.zip` — optional Chromium connector.
- `WindowAnchor-Firefox-Connector-AMO-v1.7.0.2.zip` — unsigned Firefox review and development package. Firefox Release and Beta users can install the [Mozilla-signed connector](https://addons.mozilla.org/en-US/firefox/addon/windowanchor-browser-connector/).
- `WindowAnchor-Terminal-Integration-v1.7.0.2.ps1` — optional PowerShell prompt integration.
- `SHA256SUMS.txt` — checksums for the four versioned downloads.

Windows may show a security prompt for the unsigned executable.

## Updating

1. Exit WindowAnchor from its tray menu.
2. Download `WindowAnchor-v1.7.0.2.exe` and verify its checksum.
3. Replace the previous executable or run the new file directly. If the executable path changes, run the browser setup again so the native host points to the new file.
