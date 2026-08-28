# WindowAnchor Browser Connector

This is the Manifest V3 Chromium extension for Issue #5. It captures normal browser windows and restorable `http`, `https`, and `file` tabs, including tab order, active state, pinned state, and tab groups. Incognito windows and tabs are excluded.

## Local testing and manual installation

The browser connector is now submitted for review and is supported for local/manual installation while the review is pending.

1. Build or publish WindowAnchor so `WindowAnchor.exe` is available at the path in `native-host-manifest.json`.
2. Load this directory unpacked from `chrome://extensions` or `edge://extensions` with Developer mode enabled. The browser displays the generated extension ID on the extension card; copy that ID.
3. Copy `native-host-manifest.template.json` to `native-host-manifest.json` and replace `REPLACE_WITH_PUBLISHED_EXTENSION_ID` with the extension ID.
4. Register the manifest for each browser being tested. The included `register-native-host.ps1` writes the manifest and current-user registry keys for Chrome and Edge:
   `powershell -ExecutionPolicy Bypass -File .\register-native-host.ps1 -ExtensionId <id> -WindowAnchorPath <path-to-exe>`
   Chrome uses:
   `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.windowanchor.browser`
   Edge uses:
   `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.windowanchor.browser`
   The registry default value is the absolute path to `native-host-manifest.json`.
5. Reload the extension after source changes. Inspect errors in the browser Extensions page and native-host diagnostics in `%AppData%\WindowAnchor\app.log`.

If the host is not registered, the extension reports an informational message and remains idle. Chrome's `Specified native messaging host not found` message means step 4 has not been completed for the current extension ID; it is not a WindowAnchor application crash.

Developer Mode and Load unpacked are for local development and manual installation. For general users, the extension is also intended for the Chrome Web Store or Microsoft Edge Add-ons once review is complete. A desktop app cannot silently install a consumer browser extension.

The extension requires the `tabs`, `tabGroups`, and `nativeMessaging` permissions. The native host protocol is JSON framed by Chromium and carries only session metadata; it must never log or persist credentials, cookies, or page contents.

## Supported protocol messages

The persistent native port uses request/response messages with `protocolVersion: 1`:

- `capture`: returns browser sessions.
- `restore`: creates browser windows and restores supported tabs and groups.
- `ping`: checks host connectivity.

WindowAnchor sends app requests to the host through the `WindowAnchor.BrowserBridge` named pipe. The extension ID must be explicitly allow-listed in the native host manifest; wildcard origins are not valid for production.
