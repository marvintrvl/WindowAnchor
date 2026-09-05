# WindowAnchor v1.5.1 — Restore Reliability and Recovery

## Summary

WindowAnchor 1.5.1 turns the restore foundation introduced in 1.5.0 into a complete, observable,
and recoverable workflow. Restores and workspace switches now resolve ambiguous windows safely,
adapt saved layouts across monitor changes, wait for launched applications without fixed sleeps,
verify the final placement, and create an undo checkpoint before the first desktop mutation.

This release also fixes the long switch delays and repeated waiting notifications caused by
background or transient surfaces. Window eligibility is now based on Windows-native task semantics
rather than product-specific process, class, or title lists.

## Highlights

- Classify matches as exact, strong, probable, ambiguous, missing, or ineligible with explainable
  score evidence and a deterministic ambiguity margin.
- Resolve ambiguous entries directly in Restore Preview, optionally remember a stable composite
  identity, and clear learned choices from Settings. HWNDs, PIDs, and title-only hints are never
  persisted.
- Use cancellable 250 ms readiness polling with a 45-second monotonic wall-clock budget. Each wait
  starts only after a successful launch or browser action related to that saved entry.
- Show live checkpoint, resource, browser, launch, readiness, close-wait, and verification progress,
  including item counts, elapsed time, limits, and cancellation.
- Store exact pixels together with monitor work areas, DPI, normalized geometry, anchors, and
  semantic full/half/third/centered/custom layouts. Changed or missing monitors use adaptive
  placement and remain fully visible.
- Re-read final normal bounds, show state, and DPI; tolerate harmless pixel noise and perform at
  most two corrections to the same assigned HWND when an application rejects or overrides a move.
- Atomically save a recovery checkpoint before restore, automatic display restore, adaptive
  placement, align/minimize, workspace switch, or undo. Failure of this durability gate produces
  zero desktop mutation.
- Expose Undo Last Restore through the tray. Undo uses the normal planner and first captures the
  state it is replacing, so undo-of-undo remains possible.
- Preserve approved destination windows during a switch, close only unrelated windows, track only
  the handles actually asked to close, cancel superseded requests, and rate-limit waiting notices.
- Rebind stale versioned Store/MSIX executable paths to the currently registered package and launch
  through a stable AppUserModelID.

## Generalized window behavior

Capture, matching, switching, and minimize policies now evaluate visibility, ownership, root/app
window status, `WS_EX_TOOLWINDOW`, `WS_EX_NOACTIVATE`, `WS_EX_APPWINDOW`, and DWM cloaking.
Background-only processes are recognized separately from eligible task windows, so they are
explained and skipped instead of being relaunched or awaited. Captured multiplicity is preserved,
one live HWND can satisfy only one saved entry, and cross-process hosted matching requires a shared
AppUserModelID within the same package family.

The policy contains no application-name, executable-name, window-class, or title blacklists.

## Data and compatibility

- Workspace schema v4 adds semantic/normalized layout data while retaining legacy exact bounds and
  separate maximized/minimized state.
- Settings schema v3 stores optional learned identities by stable workspace and entry IDs.
- Existing workspace and settings documents migrate automatically; older snapshots without semantic
  geometry continue through the compatible DPI-aware placement path.
- Recovery checkpoints use isolated versioned storage, a reconstructable metadata index, atomic
  replacement, and default retention of ten checkpoints for seven days.
- Structured diagnostics remain privacy-safe: paths, URLs, titles, workspace names, identifiers,
  command lines, and secrets are classified and redacted.

## Verification

- 177 Release service tests pass.
- Coverage includes matching ambiguity, learned choices, stale plans, package updates, semantic
  geometry, missing monitors, readiness timeout/cancellation, placement retry, checkpoint failure
  with zero mutation, undo-of-undo, switch serialization, native task-window policy, background-only
  processes, and session-wide HWND ownership.
- The tagged release workflow rebuilds and retests the application from the release commit before
  generating checksums and uploading assets.

## Release assets

- `WindowAnchor-v1.5.1.exe` — self-contained Windows x64 desktop application.
- `WindowAnchor-Browser-Connector-v1.5.1.zip` — optional Chromium connector and native-host setup.
- `SHA256SUMS.txt` — SHA-256 checksums generated from the uploaded executable and connector package.

## Suggested manual verification

1. Restore a workspace on its original topology and confirm exact placements are retained.
2. Restore a workspace saved with a disconnected monitor and confirm every target remains visible.
3. Resolve an ambiguous multi-window entry, change the selected radio
   button, and verify only the final selection is used.
4. Switch between two workspaces and confirm the progress window identifies slow stages without
   repeated waiting notifications.
5. Confirm background/tray applications are skipped rather than awaited when they expose no
   eligible task window.
6. Move or close a target while the preview is open and confirm stale state blocks mutation.
7. Complete a restore, choose Undo Last Restore, and confirm the prior desktop state is planned and
   restored through the normal preview/execution path.

## Updating

1. Exit the running WindowAnchor instance from its tray menu.
2. Download `WindowAnchor-v1.5.1.exe` and replace the previous executable, or run it directly.
3. Existing data under `%AppData%\WindowAnchor` is migrated and retained automatically.

The desktop executable is self-contained for 64-bit Windows and does not require a separate .NET
installation. It is not digitally signed, so Windows may display a security prompt.
