# WindowAnchor v1.4.0 — Browser Extension Review Submission

## Summary

This release updates the project to reflect the browser extension review submission and provides a supported local/manual installation workflow while the extension is under review.

## What’s new

- Browser extension submitted for review in the Chrome Web Store
- Manual installation flow documented for local testing and validation
- Release notes now explicitly state that local installation works and how to install it manually
- Browser connector setup clarified for Chrome and Edge developer mode

## Manual installation / local setup

### Desktop app

1. Download the Windows executable from this release.
2. Run the app once to confirm it launches correctly.
3. If Windows shows a security prompt, allow the app to run.

### Browser extension (manual install)

1. Download the browser connector archive from this release.
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

The browser extension is currently under review, but the local/manual installation flow is working and available for testing in the meantime. This release is intended to support both the submission process and local validation.
