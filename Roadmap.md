# WindowAnchor Roadmap

This document summarizes shipped capabilities and the next implementation themes. The detailed,
dependency-checked ticket graph lives in the companion
[WindowAnchor-Planning repository](https://github.com/marvintrvl/WindowAnchor-Planning); this file
is intentionally a product-level view rather than a second issue tracker.

## Current release: v1.6.0 — Diagnostics and Topology

WindowAnchor 1.6.0 builds on the safe restore pipeline with restore diagnostics, deterministic
simulation, display-topology stabilization, layout variants, application adapters, and practical
recovery controls:

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
- Restore reports produce structured, privacy-redactable action and entry outcomes, and deterministic
  simulation fixtures make planner behavior reproducible without mutating the live desktop.
- Startup and display-change restoration use bounded topology stabilization. Workspaces retain named
  topology-specific layout variants alongside their shared application context.
- Logical path aliases, persistent keep-open app identities, and active-window rescue support safer
  recovery after device, path, or display changes.
- Chromium PWA, dedicated-browser URL, Explorer-folder, and generic Win32 behavior is owned by
  independently testable capture and launch adapters.

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

## Release verification limits

The following v1.6.0 capabilities are covered by the service-level suite. See
[`docs/implementation-status.md`](docs/implementation-status.md) for explicit verification limits.

- Structured per-entry restore diagnostics, deterministic planner simulation, display-topology
  stabilization, topology-specific layout variants, persistent application identities, active-window
  rescue, logical path aliases, and application adapters are implemented.
- Internal workspace transfer and generic-folder sync foundations are deliberately partial. They do
  not yet provide a user-facing import/export or synchronization workflow.
- Real-Windows Restore/Switch/Undo, changed-topology rescue, and warm exact-topology Resume checks
  remain release gates; service-layer tests do not substitute for those desktop interactions.

## Next priorities

The dependency-checked ready queue is maintained in the companion planning repository. Its current
order is:

1. **WA-017 VS Code workspace tracking** — capture and reopen `.code-workspace` and folder context.
2. **WA-019 Browser profile awareness and tab deduplication** — retain profile identity and avoid
   duplicate browser restoration.
3. **WA-020 File Explorer adapter** — capture and restore folders through a dedicated adapter.
4. **WA-021 Windows Terminal adapter** — capture and restore shells and profiles safely.
5. **WA-030B/WA-031 display recovery** — temporary-resolution and RDP-aware restoration.
6. **WA-036A/WA-036D portability resolution** — cross-device display mapping and moved-resource
   resolution above the existing path-alias foundation.
7. **WA-040A/WA-042A catalog and template foundations** — stable workspace metadata and templates.

## Later themes

- Non-mutating workspace health/diff views.
- Automatic checkpoint triggers, quick temporary captures, and recovery-timeline UX.
- Complete portable import/export, conflict preview, transfer-schema migration, staging validation,
  manifests, device identity, and conflict-copy orchestration.
- Stable workspace catalog metadata, desk profiles, templates, and broader ecosystem integrations.

## Release quality bar

Each release must keep pure planning free of I/O, preserve one-to-one window assignment, reject
stale destructive intent, avoid logging private user content, migrate older data without loss, and
pass the Release service test suite. Published Windows and browser-connector assets must be built
from the tagged commit and accompanied by matching SHA-256 checksums.
