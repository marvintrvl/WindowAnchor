# WindowAnchor Roadmap

This document summarizes shipped capabilities and the next implementation themes. The detailed,
dependency-checked ticket graph lives in the companion
[WindowAnchor-Planning repository](https://github.com/marvintrvl/WindowAnchor-Planning); this file
is intentionally a product-level view rather than a second issue tracker.

## Current release: v1.5.2 — Restore Control and Update Recovery

WindowAnchor 1.5.2 keeps the safe restore pipeline while making its routine behavior faster,
configurable, easier to understand, and resilient to versioned desktop-app updates:

- Manual tray and Settings restores show a per-entry plan before changing the desktop.
- Users can disable entries while keeping the original match evidence and preview immutable.
- Approved plans are rejected if HWND/PID identity, eligible candidates, launch resources, or
  browser capability changed while the preview was open.
- Restore intent and execution results are structured and privacy-redactable.
- Matching uses stable PWA, packaged-app, dedicated-browser, document/project, executable, class,
  title, monitor, and geometry evidence with session-wide one-HWND ownership.
- Workspace/settings schemas are versioned and use stable IDs.
- Named workspaces, recovery checkpoints, and temporary captures have isolated atomic stores.
- Capture construction is separate from persistence and optional browser enrichment.
- Window enumeration is policy-free; capture, matching, switch, risk, and minimize consumers choose
  explicit policies.
- Structured diagnostics centrally redact paths, URLs, titles, names, identifiers, command lines,
  and secrets.
- The service suite characterizes planning, migrations, matching, persistence, preview approval,
  stale-plan handling, execution boundaries, and compatibility behavior.
- Matching confidence uses explicit thresholds and an ambiguity margin; close candidates are shown
  for user resolution, and optional composite hints remember choices without HWND/PID persistence.
- Launched applications use cancellable per-entry readiness polling with safe matching,
  responsiveness and stability signals, app-strategy extension points, and structured timeouts.
- Exact topology retains pixel placement, while changed topology uses normalized work-area geometry,
  semantic anchors, and visible-monitor clamping.
- Every approved mutation is preceded by an atomic recovery checkpoint; Undo Last Restore runs
  through the same planner and captures an undo-of-undo safety point.
- Placement is verified after settling with DPI-aware tolerance and bounded corrections to the same
  assigned HWND.
- Workspace switching preserves approved destination windows, tracks only requested closures, and
  serializes/cancels overlapping requests.
- Native style, ownership, and DWM-cloaking capabilities define eligible task windows without
  product-specific process, class, or title exclusions.
- Restore progress identifies the active checkpoint, resource, browser, launch, readiness,
  close-wait, and placement-verification stage with elapsed/limit timing and cancellation.
- Recognized Squirrel `app-<version>` executables are rebound to the newest immediate version
  sibling with the same executable name; arbitrary wildcard execution is not accepted.
- Routine restore preview and checkpoint creation are independent Settings options. Plans requiring
  an ambiguity or blocker decision still open the preview, and mutation remains single-flight.
- Undo performs full desktop reconciliation, while visible-frame compensation prevents invisible
  DWM resize borders from producing gaps after topology adaptation.
- Workspaces expose Repair, Move Existing, Resume, Launch Fresh, Exact Switch, and Preview Only,
  composed with persisted per-entry reuse, launch, close, and switch policies.
- Fresh interactive installs receive one visible tray-app introduction. A permanent Help & Guide
  page in the tray and Settings documents operation, restore variants, policies, privacy, and limits.

## How the restore pipeline now works

1. Observe live windows, resources, browser capability, and monitor topology without mutation.
2. Build an immutable `RestorePlan` with candidate evidence, placements, actions, warnings, and
   blockers after resolving Repair, Move Existing, Resume, Launch Fresh, Exact Switch, or Preview
   Only plus any per-entry override.
3. For manual restores, project that plan into the preview and derive approval from disabled entry
   IDs; automatic restores keep their one-click path.
4. Preflight the approved plan against current external state. Never silently replan a stale
   preview.
5. Persist a complete pre-mutation recovery checkpoint; reject the operation with zero mutation if
   the durability gate fails.
6. Execute only approved, predeclared actions through isolated process, browser, resource, clock,
   readiness, inventory, and window-mutation boundaries. Position each entry when its matched
   window becomes responsive and stable; never wait forever.
7. Verify final state with DPI-aware tolerance and bounded corrections to the assigned HWND.
8. Return structured per-action and per-entry outcomes for UI and privacy-safe diagnostics.

## Next priorities

The current recommended P0 sequence after v1.5.2 is:

1. **Structured restore report** — turn executor results into clear per-item user diagnostics.
2. **Display topology stabilization** — debounce transient docking states before automatic restore.
3. **Layout variants** — select a saved arrangement for each stable monitor topology without adding
   work to ordinary exact-topology restores.
4. **Non-mutating workspace diff** — compare saved intent with the current desktop before restore.
5. **App-adapter architecture** — add specialized identity and launch strategies without bypassing
   shared matching, HWND ownership, readiness, or verification boundaries.

## Later themes

- Layout variants per workspace and display topology stabilization.
- Automatic checkpoint triggers, quick temporary captures, and recovery-timeline UX.
- Workspace health scans, deterministic simulations, and manual off-screen rescue.
- Logical path aliases, import/export, generic-folder sync, and cross-device monitor identity.
- Stable workspace catalog metadata, desk profiles, templates, and broader ecosystem integrations.

## Release quality bar

Each release must keep pure planning free of I/O, preserve one-to-one window assignment, reject
stale destructive intent, avoid logging private user content, migrate older data without loss, and
pass the Release service test suite. Published Windows and browser-connector assets must be built
from the tagged commit and accompanied by matching SHA-256 checksums.
