# Workspace Import and Export

WindowAnchor transfers one saved workspace at a time. It never exports the whole application
configuration, settings, alias-root mappings, credentials, cookies, document contents, or native
browser-host registration data.

## Export

In **Settings → Saved Workspaces**, open a workspace's **…** menu and select **Export…**. Choose
the destination and the metadata categories to include.

- **Exact backup** starts with all selected workspace metadata, including local layout and paths.
  Keep the resulting `.windowanchor.json` file private when it contains file locations or browser
  URLs.
- **Portable / redacted** starts without layout, files/folders, browser URLs, machine identifiers,
  or absolute local paths. It can retain logical `${ALIAS}` path forms, but never exports the local
  root assigned to an alias.

The category controls let you include or exclude layout, application identity, files/folders,
browser URLs, machine identifiers, and logical aliases. Disabling a category removes that data
from the file. A portable export always removes raw absolute fallback paths even if application
identity is retained.

## Import

Select **Import Workspace** in the same Settings section and choose a local
`.windowanchor.json` file. WindowAnchor stages and validates it before showing a preview with the
workspace name, window count, transfer mode, migration state, and any name/stable-ID conflict.

Import always creates a separate copy. A matching stable ID is replaced with a new ID; a matching
display name receives an `Imported` suffix. Existing workspaces are never overwritten. The file is
checked again after preview, so a changed file must be reviewed again before it can be imported.

Transfer files are limited to 5 MB. Current exports use transfer schema v2; schema v1 files and
older supported workspace payloads migrate during staging. Future or malformed schemas are
rejected without writing workspace storage. The final imported document uses the normal atomic
workspace repository, so a failed commit leaves existing workspaces intact.

Importing a workspace does not launch or restore any application. Restore the new workspace
separately once you have reviewed its saved metadata.
