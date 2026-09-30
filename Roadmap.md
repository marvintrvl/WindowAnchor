# WindowAnchor Roadmap

WindowAnchor stays focused on dependable local workspace capture and restoration. Its distinction
from PowerToys Workspaces is preserving supported application, file, folder, browser, Explorer-tab,
Terminal-tab, and virtual-desktop context.

The detailed ticket history lives in the private planning repository. This public roadmap lists
only shipped work and the small active pipeline.

## Current release: v1.7.0 — Desktop Context

Version 1.7.0 adds:

- Windows Terminal tab profiles, starting directories, order, and active-tab restoration through
  an optional PowerShell integration and save-time editor.
- Automatic capture across Windows virtual desktops, recreation of missing desktops, and placement
  of existing or launched windows onto their saved desktop.
- ATLauncher startup through its stable launcher instead of an invalid bare Java runtime command.
- Temporary display recovery after a stabilized resolution, docking, or monitor excursion.
- Non-fatal diagnostics for applications that allow launch/desktop movement but reject geometry.

It also includes the v1.6.x Explorer multi-tab, browser-profile, matching, recovery, diagnostics,
and adaptive-layout work.

## Active pipeline

1. **Checkpoint and Undo verification (WA-006A)** — finish the real-Windows Restore, Exact Switch,
   Undo, and undo-of-undo checklist.
2. **Local workspace import/export (WA-033)** — add a small versioned UI, conflict preview, and old
   transfer-schema migration without accounts or cloud sync.

The deterministic restore fixture matrix (WA-046) is complete: synthetic inputs cover monitor
mapping, matching, mixed DPI, browser/PWA identity, VS Code projects, unavailable resources, and
logical path aliases without changing the desktop.

The only open public GitHub ticket, [#12](https://github.com/marvintrvl/WindowAnchor/issues/12), has
an implemented update-safe Squirrel path resolver and remains open only for reporter verification.
Firefox AMO publication and a signed-add-on smoke are external distribution gates, not unfinished
desktop restore architecture.

## Not in the active roadmap

Cross-device restoration, cloud synchronization, community templates/recipes, general automation
engines, scheduled or unlock-triggered desktop mutation, audio/device profiles, monitor-mode
switching, and a public compatibility database are not current product commitments. Their old
planning records are retained only for traceability.

## Release quality bar

Every release must preserve one-to-one window assignment, reject stale destructive intent, keep
private user content out of logs, migrate older stores without data loss, pass the Release test
suite, and publish tagged artifacts with matching SHA-256 checksums. Features that depend on real
Windows applications also require a recorded live smoke before they are described as verified.
