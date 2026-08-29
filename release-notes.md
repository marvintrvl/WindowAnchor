# WindowAnchor v1.4.2 — Reliable Tray Workspace Submenu

## Summary

This patch release fixes the **Workspaces** submenu in the system tray. The
left-opening submenu now remains available while the pointer moves from the main
tray menu into the workspace commands at a normal speed.

## Fixed in v1.4.2

- Added a short 250 ms grace period before an open workspace submenu may close.
- Entering either the **Workspaces** header or its submenu cancels the pending close.
- Kept the submenu popup stationary and slightly overlapping the parent menu so
  there is no fragile dead zone at the screen edge.
- Application, assembly, and file versions now consistently report `1.4.2`.

## Release assets

- `WindowAnchor-v1.4.2.exe` — self-contained Windows x64 desktop application.
- `WindowAnchor-Browser-Connector-v1.4.2.zip` — optional Chromium browser
  connector and local native-host registration files. The connector itself is
  unchanged from v1.4.1 and is included for complete installations.
- `SHA256SUMS.txt` — SHA-256 checksums for both downloadable packages.

## Updating the desktop app

1. Exit any running WindowAnchor instance from its tray menu.
2. Download `WindowAnchor-v1.4.2.exe`.
3. Replace the previous executable or run the new file directly.
4. Open the tray menu and confirm the **Workspaces** submenu remains open while
   moving the pointer into it.

## Optional browser connector setup

1. Download and extract `WindowAnchor-Browser-Connector-v1.4.2.zip`.
2. Open `chrome://extensions` or `edge://extensions`.
3. Enable Developer mode.
4. Click **Load unpacked** and select the extracted extension directory.
5. Copy the extension ID from the browser extension card.
6. Update the native host manifest and register the native host using the included PowerShell script:

```powershell
powershell -ExecutionPolicy Bypass -File .\register-native-host.ps1 -ExtensionId <id> -WindowAnchorPath <path-to-exe>
```

7. Reload the extension and verify the browser tabs and tab groups can be captured and restored.

## Notes

The desktop EXE is self-contained for 64-bit Windows and does not require a
separate .NET installation. It is not digitally signed, so Windows may show a
security prompt. The browser connector remains under store review; manual local
installation is still available.
