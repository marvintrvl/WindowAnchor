# WindowAnchor v1.6.0 — Diagnostics and Topology

## Summary

WindowAnchor 1.6.0 makes complex restore decisions more visible and more resilient to changing
desktops. It adds structured restore diagnostics, deterministic restore-plan simulation, display
topology stabilization, named layout variants, logical path aliases, application adapters, and
practical recovery controls for persistent apps and insufficiently visible foreground windows.

The existing safe restore modes, per-entry policies, transaction checkpoints, preview controls,
and in-app Help & Guide remain the foundation of the release.

## Highlights

- Review structured action and entry results after a restore and produce a privacy-safe diagnostics
  report when a desktop needs investigation.
- Run `--simulate-restore <fixture.json>` to exercise the pure planner from versioned synthetic
  inputs, without enumerating or changing the live desktop.
- Let startup and display-change restoration wait for monitor identity, geometry, work area, DPI,
  primary state, and orientation to settle within a bounded, cancellable window.
- Save, select, rename, and remove topology-specific layout variants without duplicating the
  workspace's shared application, file, and browser context.
- Resolve configured `${ALIAS}` paths only when the original saved resource is unavailable. Aliases
  remain local to the device and are never used to broaden arbitrary execution.
- Keep selected applications open across workspace switches through a Settings list with live app
  icons and one-click removal.
- Use tray rescue to move a foreground window that is mostly off-screen into the nearest work area
  while preserving restored/maximized behavior.
- Capture and launch Chromium PWAs, dedicated browser URLs, Explorer folders, and generic Win32
  applications through focused adapters with shared restore safety boundaries.
- Restore legacy Store app captures by deriving a package family from their versioned WindowsApps
  path when no AppUserModelID was saved, avoiding false missing-resource results after updates.
- Make title-only VS Code/Cursor skips explicit when WindowAnchor cannot safely recreate a file or
  workspace context or assign the same live window twice.

## Compatibility and migration

- Workspace and settings schemas remain backward-compatible with prior restore modes, policies,
  stable IDs, monitor layout data, checkpoints, and browser preferences.
- Existing named workspaces, learned matches, checkpoints, monitor aliases, hotkeys, startup
  behavior, browser configuration, and notification preferences are retained.
- Aliases change only an immutable launch resource selected for the current plan and are revalidated
  immediately before launch.

## Internal quality work

The v1.6.0 architecture places capture, desktop/native operations, matching, restore execution,
storage/repositories/migrations, system integration, and workspace coordination in focused service
folders. `WorkspaceService` now composes checkpoint, diagnostics, and layout-variant collaborators;
application adapters own application-specific capture and launch behavior.

## Verification

- 292 Release tests cover schema migration, restore policies, ambiguity-safe assignment, adapters,
  topology stabilization, layout variants, diagnostics, simulation, path aliases, persistent-app
  policy, rescue behavior, preview/checkpoint behavior, and cleanup equivalence boundaries.
- The release workflow rebuilds and retests the tagged commit before uploading versioned assets and
  their SHA-256 checksums.

## Release assets

- `WindowAnchor-v1.6.0.exe` — self-contained Windows x64 desktop application.
- `WindowAnchor-Browser-Connector-v1.6.0.zip` — optional Chromium connector and native-host setup.
- `SHA256SUMS.txt` — SHA-256 checksums generated from the uploaded executable and connector package.

## Suggested verification for updated applications

1. Save a workspace that includes an app, folder, browser URL, or Chromium PWA, then use the
   restore diagnostics after completion to inspect its actions and outcomes.
2. Disconnect, reconnect, or otherwise change a monitor topology, then confirm the selected layout
   variant remains visible after the bounded topology wait.
3. Disable routine preview, then restore a safe plan and a plan containing an ambiguity or blocker;
   only the latter should require review.
4. Add an open application to the persistent-app list, switch workspaces, and confirm it remains
   open; remove it from the expanded list and confirm the policy no longer applies.

## Updating

1. Exit the running WindowAnchor instance from its tray menu.
2. Download `WindowAnchor-v1.6.0.exe` and replace the previous executable, or run it directly.
3. Existing data under `%AppData%\WindowAnchor` is migrated and retained automatically.

The desktop executable is self-contained for 64-bit Windows and does not require a separate .NET
installation. It is not digitally signed, so Windows may display a security prompt.
