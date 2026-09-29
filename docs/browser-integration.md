# Browser Integration

WindowAnchor has separate browser connector packages:

- `browser-extension/` is the Chromium Manifest V3 connector for Chrome, Edge, Brave, and Opera.
- `firefox-extension/` is the Firefox connector for Firefox Desktop 142 and later.

Both implement protocol v2, but they are packaged separately because Firefox uses a Gecko add-on
ID, `allowed_extensions`, Mozilla's registry location, and `background.scripts`. The Firefox add-on
uses a persistent Manifest V2 background page deliberately: Firefox MV3 event pages may unload and
close a native-messaging port while WindowAnchor is waiting to initiate a desktop-side request.
Mozilla continues to support Manifest V2 extensions.

## Data captured

Connectors capture only normal browser windows and supported `http`, `https`, and exposed `file`
tabs selected by the WindowAnchor save dialog. They transfer tab URL, title, order, active and
pinned state, browser-window bounds/state, tab-group metadata, a runtime window ID, and a locally
generated opaque connector-profile key. WindowAnchor assigns a stable saved session ID and records
desktop-entry/monitor linkage only when title and geometry identify one entry uniquely.

Incognito and Firefox private windows are excluded. The connectors do not request cookies,
passwords, general browsing history, page bodies, form data, downloads, bookmarks, browser account
identity, or profile names. Firefox container identity is not requested or restored.

Mozilla treats data passed to a native application as transmitted outside the add-on even when it
never leaves the PC. The Firefox manifest therefore declares `browsingActivity`, `websiteContent`,
and `personallyIdentifyingInfo` for URLs, titles/group labels, and the opaque profile key. It does
not falsely declare `none`.

## Communication design

Each extension opens `runtime.connectNative("com.windowanchor.browser")`. The native host routes
Chromium and Firefox through separate named pipes:

- `WindowAnchor.BrowserBridge` (the existing Chromium endpoint)
- `WindowAnchor.BrowserBridge.Firefox`

WindowAnchor captures only from the connector families represented by selected desktop windows,
then combines their sessions. Restore groups sessions by browser and sends each group back through
the matching pipe, so Chrome and Firefox can participate in one workspace without crossing native
hosts.

Native messaging frames each UTF-8 JSON message with a 32-bit native-endian length prefix. The host
limits messages to 1 MiB, writes protocol data only to stdout, and uses logs/stderr for diagnostics.
Requests carry `requestId` and `protocolVersion`; incompatible responses fail safely. A missing
browser family reports unavailable without preventing unrelated desktop applications from
restoring.

## Profile-aware restore

`storage.local` contains an opaque random key scoped to the installed connector. It is not an
account, profile-directory name, or display label. Restore never reuses a tab from a different key.
**Reuse matching tab** focuses exact `http`/`https` URL matches in the same profile, **Always
reopen** creates new tabs, and **Ask before reuse** returns a structured conflict for WindowAnchor
to resolve. `file://` and legacy profile-unknown sessions are duplicate-only.

Firefox restore is isolated per tab. One URL failure produces a partial browser result while other
valid tabs, browser windows, and workspace applications continue. Pinned/active state, tab groups,
and normal/maximized/minimized/fullscreen state are restored where Firefox accepts them.

## Restore planning and fallback

Browser restoration participates in the immutable restore plan. Planning records connector
availability and adds an explicit browser-session action without creating windows. Immediately
before execution, `RestoreExecutor` verifies that capability still matches the preview. If session
restore is unavailable, only a fallback already declared by the plan may launch an ordinary
browser. Disabling the linked browser entry removes its session action and protects it from launch,
placement, and terminal minimize behavior.

Browser work uses the same checkpoint, progress, cancellation, readiness, and structured-result
boundaries as desktop restoration.

## Store setup

The Chromium connector is published in the
[Chrome Web Store](https://chromewebstore.google.com/detail/windowanchor-browser-conn/liiklnjpifhhmjncifbjjfgplonkkinh)
with ID `liiklnjpifhhmjncifbjjfgplonkkinh`. **Set up Chrome** registers the current-user host with
that exact allowed origin and opens the listing.

The Mozilla-signed Firefox connector is published on
[Firefox Add-ons](https://addons.mozilla.org/en-US/firefox/addon/windowanchor-browser-connector/).
**Set up Firefox** registers the current-user Mozilla native host and opens that exact listing.
Release/Beta Firefox accepts only Mozilla-signed add-ons.

Desktop applications cannot silently install either extension. The browser/store remains in
control of installation.

## Local Firefox testing and AMO packaging

1. Run **Set up Firefox** in WindowAnchor, or execute
   `firefox-extension/register-native-host.ps1` with the WindowAnchor executable path.
2. Open `about:debugging#/runtime/this-firefox`, choose **Load Temporary Add-on**, and select
   `firefox-extension/manifest.json`.
3. Run `web-ext lint` and `web-ext build` from `firefox-extension/`.
4. Maintainers publish signed updates through AMO using the fixed add-on identity and the checked-in
   `amo-metadata.json`; GitHub release ZIPs remain unsigned development/review artifacts.

The fixed ID `windowanchor-browser-connector@windowanchor.app` appears in the add-on manifest,
native-host allow-list, registration script, and desktop routing tests. Firefox starts a native
host with the manifest path and add-on ID as command-line arguments; WindowAnchor recognizes only
that exact ID as its Firefox connector.

## Local Chromium testing

1. Publish WindowAnchor and choose the executable used by the native host.
2. Run `browser-extension/register-native-host.ps1` with the unpacked extension ID and executable
   path.
3. Load `browser-extension/` unpacked from `chrome://extensions` or `edge://extensions`.
4. Reload the extension after code changes.

Unpacked Chromium builds have generated IDs, so their exact origin must replace the template value;
wildcards are not valid in `allowed_origins`.

## Limitations

Runtime browser window IDs are session-scoped metadata; `BrowserSessionId` is WindowAnchor's stable
saved identity. Browser-internal/extension URLs are skipped. Firefox container identities are not
preserved because the connector intentionally does not request contextual-identity or cookie
access. A `file://` tab is captured only when the browser exposes its URL and is never reused as an
existing match. A stale approved preview is rejected rather than silently targeting a changed
browser window.

## Official references

- Firefox background scripts: https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/manifest.json/background
- Firefox native messaging: https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/Native_messaging
- Firefox native manifests: https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/Native_manifests
- Firefox tabs: https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/API/tabs
- Firefox tab groups: https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/API/tabGroups
- Firefox data consent: https://extensionworkshop.com/documentation/develop/firefox-builtin-data-consent/
- Firefox signing: https://extensionworkshop.com/documentation/publish/signing-and-distribution-overview/
- `web-ext`: https://extensionworkshop.com/documentation/develop/getting-started-with-web-ext/
- Chrome native messaging: https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging
- Edge native messaging: https://learn.microsoft.com/en-us/microsoft-edge/extensions-chromium/developer-guide/native-messaging
