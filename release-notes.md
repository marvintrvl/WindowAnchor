# WindowAnchor v1.5.0 — Restore Planning and Safety

## Summary

WindowAnchor 1.5.0 introduces a reviewable restore pipeline. Manual restores now show an immutable
plan before changing the desktop, allow individual entries to be disabled, and reject previews that
became stale while they were open. This release also consolidates the versioned storage, stable-ID,
matching, diagnostics, and testability foundations built since 1.4.2.

## Highlights

- Preview exact, adapted, ambiguous, missing, skipped, move, launch, browser, wait, and minimize
  outcomes before a manual restore.
- Disable individual entries without recomputing matches or changing the original preview.
- Clearly distinguish blocking errors and destructive minimize actions. The preview explicitly
  confirms when no windows will be closed.
- Revalidate HWND/PID identity, the eligible candidate set, browser capability, and launch resources
  before any approved mutation begins.
- Return a clear stale-preview warning instead of silently targeting a replacement window or
  launching a duplicate application.
- Preserve one-click startup, display-change, and hotkey restores for existing automation flows.
- Keep installed PWAs, dedicated browser sites, documents/projects, packaged apps, DPI adaptation,
  selective restore, cancellation, and browser-session fallback behavior covered by deterministic
  service tests.
- Present clean Cancel and Restore selected labels while retaining Tab navigation, Enter approval,
  Escape cancellation, and screen-reader automation names.

## Data and reliability foundations

- Workspace and settings documents use versioned schemas with stable workspace and entry IDs.
- Named workspaces, checkpoints, and temporary captures use separate typed repositories and atomic
  replacement.
- Snapshot construction is independent from persistence and optional browser enrichment.
- Privacy-safe structured logging redacts sensitive paths, URLs, titles, identifiers, workspace
  names, and command-line values.

## Release assets

- `WindowAnchor-v1.5.0.exe` — self-contained Windows x64 desktop application.
- `WindowAnchor-Browser-Connector-v1.5.0.zip` — optional Chromium connector and native-host setup.
- `SHA256SUMS.txt` — SHA-256 checksums for the executable and connector package.

The tagged release workflow rebuilds and tests the application, then generates the checksum file
from the exact assets it uploads.

## Suggested manual verification

1. Open a manual restore from the tray and verify the preview is keyboard navigable.
2. Confirm exact/adapted outcomes, target monitors, and all action descriptions are visible.
3. Disable one entry and verify it is neither moved nor minimized.
4. Open or close a matching app while the preview is open, then approve it; confirm WindowAnchor
   reports that the preview is stale and does not apply stale actions.
5. Test standard, selective, align/minimize, browser-session fallback, and cancellation flows.
6. Confirm startup/display-change/hotkey restoration still follows the configured one-click path.

## Updating

1. Exit the running WindowAnchor instance from its tray menu.
2. Download `WindowAnchor-v1.5.0.exe` and replace the previous executable, or run it directly.
3. Existing workspace and settings files are migrated automatically and retained under
   `%AppData%\WindowAnchor`.

The desktop executable is self-contained for 64-bit Windows and does not require a separate .NET
installation. It is not digitally signed, so Windows may display a security prompt.
