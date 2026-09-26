# WindowAnchor Firefox Connector Privacy Notice

The WindowAnchor Firefox Connector reads open-tab and window metadata only to save and restore the
browser portion of a WindowAnchor workspace.

## Data handled

- supported tab URLs and tab titles
- pinned, active, index, and tab-group state
- normal browser-window bounds and state
- an opaque random key stored by the add-on to distinguish Firefox connector profiles

Firefox describes these as browsing activity, website content, and personally identifying
information for extension-consent purposes because the add-on transfers them through native
messaging to an application outside Firefox.

## Local transfer and storage

The data is sent only to the WindowAnchor native application on the same Windows PC. WindowAnchor
stores selected browser-session metadata in the user's local workspace files so that the user can
restore it later. The connector has no analytics, advertising, telemetry, or remote service and
does not send this data to the publisher or a third party.

WindowAnchor data leaves the PC only when the user explicitly exports it or configures a separate
WindowAnchor synchronization destination.

## Exclusions

The connector does not capture private windows. It does not request or read cookies, passwords,
general browsing history, page bodies, downloads, bookmarks, authentication data, or Firefox
container identities. Browser-internal and extension URLs are skipped.

Users can stop the transfer by disabling or uninstalling the add-on, or by removing the native
browser connection in WindowAnchor. Deleting a WindowAnchor workspace removes the browser session
metadata stored in that workspace.

WindowAnchor and this connector do not sell personal data, use it for advertising, or share it
with third parties.
