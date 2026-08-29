# WindowAnchor v1.4.1 — Browser Connector and Release Fixes

## Summary

This release provides an updated WindowAnchor desktop build together with the
browser connector package. It also repairs the automated GitHub release process,
updates the application's embedded version, and resolves known OpenMcdf security
advisories.

## What’s new

- Desktop application and browser connector are supplied as separate release assets
- Browser setup detects installed Chromium browsers and opens the appropriate extension page
- Missing native-host setup is handled quietly instead of repeatedly reporting unchecked errors
- Installed Chromium web apps are restored using their application identity instead of as plain browser windows
- WindowAnchor tray notifications can be disabled in Settings

## Fixed in v1.4.1

- Application, assembly, and file versions now consistently report `1.4.1`
- OpenMcdf updated from `3.1.0` to `3.1.4`, resolving two moderate-severity infinite-loop denial-of-service advisories
- GitHub Actions now has the required release permission and no longer uses the archived `actions/upload-release-asset@v1` action
- The release workflow now creates and uploads both the Windows executable and browser connector ZIP

## Manual installation / local setup

### Desktop app

1. Download `WindowAnchor-v1.4.1.exe` from this release.
2. Run the app once to confirm it launches correctly.
3. If Windows shows a security prompt, allow the app to run.

### Browser extension (manual install)

1. Download and extract `WindowAnchor-Browser-Connector-v1.4.1.zip`.
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

The browser extension is currently under review, but the local/manual installation
flow remains available for testing. The desktop EXE is self-contained for 64-bit
Windows and does not require a separate .NET installation.
