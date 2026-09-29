# WindowAnchor Firefox Connector

This is the Firefox-specific companion add-on for WindowAnchor. It uses Firefox's WebExtension
APIs and native messaging to capture and restore normal Firefox windows, supported tab URLs,
pinned and active state, tab groups, and window bounds.

The Firefox package is intentionally separate from the Chromium package. Firefox uses
`background.scripts`, identifies native-messaging callers by a Gecko add-on ID, allow-lists them
with `allowed_extensions`, and discovers the host under Mozilla's Windows registry key.

## Requirements

- Firefox Desktop 142 or later
- WindowAnchor for Windows
- The native host registered for add-on ID
  `windowanchor-browser-connector@windowanchor.app`

Firefox for Android is not supported because it does not provide native messaging.

## Install

Install the Mozilla-signed
[WindowAnchor Browser Connector](https://addons.mozilla.org/en-US/firefox/addon/windowanchor-browser-connector/)
from Firefox Add-ons. In WindowAnchor, choose **Settings > Browser Integration > Set up Firefox**
to register the current-user native host and open the listing.

## Local test setup

1. Build or publish `WindowAnchor.exe`.
2. In WindowAnchor, choose **Settings > Browser Integration > Set up Firefox**. This registers
   `%LOCALAPPDATA%\WindowAnchor\native-host-manifest-firefox.json` under
   `HKCU\Software\Mozilla\NativeMessagingHosts\com.windowanchor.browser`.
3. Open `about:debugging#/runtime/this-firefox`, choose **Load Temporary Add-on**, and select this
   folder's `manifest.json`.
4. Save a workspace containing one or more normal Firefox windows, then restore it.

For manual registration:

```powershell
.\register-native-host.ps1 -WindowAnchorPath 'C:\path\to\WindowAnchor.exe'
```

The fixed Gecko add-on ID in `manifest.json` must remain identical to the ID in
`allowed_extensions`. Firefox passes the host-manifest path and add-on ID as native-host command
line arguments; WindowAnchor uses the exact add-on ID to select the Firefox bridge.

## Validate and package

Install Mozilla's `web-ext`, then run these commands from this directory:

```powershell
node --test .\tests\background.test.cjs
web-ext lint
web-ext build
```

`web-ext-config.mjs` excludes development and submission-only files from the generated ZIP.
Firefox Release and Beta require Mozilla signing, so an unsigned ZIP is only a submission or
development artifact.

For a signed AMO update, create API credentials in the AMO Developer Hub and run:

```powershell
web-ext sign --channel=listed --amo-metadata=amo-metadata.json `
  --api-key=$env:AMO_JWT_ISSUER --api-secret=$env:AMO_JWT_SECRET
```

The checked-in metadata supplies the listing summary and Firefox category. Review the listing copy,
screenshots, support contact, and privacy-policy URL in AMO before publishing an update. The desktop
app's Firefox setup action must continue to target the exact public listing above.

## Privacy and recovery boundaries

The add-on declares Mozilla's `browsingActivity`, `websiteContent`, and
`personallyIdentifyingInfo` data categories because native messaging sends tab URLs, tab titles,
tab-group metadata, and an opaque connector-profile key to the local WindowAnchor application.
That data stays on the user's PC unless the user exports or synchronizes their WindowAnchor data.
See [PRIVACY.md](PRIVACY.md).

Private windows and tabs are not available to the add-on. It does not request or read cookies,
passwords, general browsing history, page bodies, downloads, bookmarks, or contextual identities.
Firefox container identity is therefore not restored. Internal URLs such as `about:` and extension
pages are skipped. Supported URLs are `http://`, `https://`, and `file://`; exact matching never
reuses `file://` tabs.

Restore is best effort per tab. A failed URL is reported for that browser window without preventing
other valid tabs or other workspace applications from restoring.
