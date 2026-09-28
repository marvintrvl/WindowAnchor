# Implemented Ticket Verification

**Audit date:** 2026-09-28
**Automated gate:** `dotnet test .\WindowAnchor.sln --configuration Release --no-restore` — 345 passed, 0 failed, 0 skipped
**Explorer live gate:** `WINDOWANCHOR_LIVE_EXPLORER_SMOKE=1` isolated Windows 11 25H2
three-tab restore — passed, including active-tab selection and zero duplicate tabs on rerun
**Firefox behavior gate:** `node --test .\firefox-extension\tests\background.test.cjs` — 4 passed, 0 failed
**Firefox add-on gate:** `web-ext 10.7.0 lint` — 0 errors, 0 notices, 0 warnings
**Simulation smoke:** `single-monitor.json` and the published-artifact run of
`adversarial-topology.json` — exit code 0, no expectation failures
**Dependency audit:** no known vulnerable direct or transitive packages from the configured NuGet sources

This record separates implemented and automated behavior from manual release gates and partial
internal groundwork. A green service-layer suite is not a substitute for a real Windows UI,
application-launch, browser, or multi-monitor smoke pass.

## Ticket status

| Ticket | Verification result | Evidence and remaining work |
|---|---|---|
| WA-005A | Verified in automation | Exact-topology Resume no-op revalidation, zero mutation/waits, bounded browser capture, conditional checkpoints, shared readiness deadline, and stage timings are covered. The requested warm-Windows benchmark was not rerun in this audit. |
| WA-006A | Implemented; manual gate open | Automated tests cover checkpoint-before-mutation, write failure, cancellation boundaries, conservative switch reconciliation, healthy-checkpoint selection, Undo, and undo-of-undo. The required real-Windows Restore, Exact Switch, Undo, and undo-of-undo checklist has not been recorded. |
| WA-008 | Verified in automation | The shared startup/display-change stabilizer samples a complete restore-relevant topology signature, resets its settle interval on change, honors cancellation, and times out without restoring unstable state. Synthetic tests cover DPI, orientation, work area, negative origins, duplicate identities, and missing displays. |
| WA-009 | Verified in automation | Workspace schema migration creates one default variant; exact topology wins, monitor-overlap fallback is deterministic, preview exposes the selected variant, and add/rename/delete preserve shared entries and browser context. |
| WA-012 | Verified in automation | Stable executable/AUMID identities persist, apply across workspace IDs, do not use titles, are excluded from switch risk, and can be removed. |
| WA-014 | Verified in automation | Typed reports survive partial failure, serialize redacted diagnostics, expose timings/per-item outcomes, avoid new observation waits, and keep routine success quiet. |
| WA-016 | Verified in automation | Adapter registry/contracts plus separate Chromium PWA, dedicated-browser, Explorer-folder, and generic Win32 adapters preserve launch characterization and fallback behavior without third-party loading. |
| WA-017 | Verified in automation | The VS Code adapter captures high-confidence `.code-workspace` and folder context, distinguishes multiple project windows, migrates existing saved Code targets, uses documented new/reuse-window CLI flags, and leaves Cursor on its established fallback path. Live remote authority is not inferred from a title because the capture boundary has no supported source for it. |
| WA-018 | Implemented and AMO-lint clean; publication/live smoke open | A separately packaged Firefox Desktop 142+ add-on captures/restores normal windows, tabs, pinned/active state, groups, bounds, and profile-scoped exact-URL reuse. Browser-family pipes allow Chromium and Firefox to coexist; registration uses Mozilla's `allowed_extensions` manifest and exact Gecko ID. Per-tab failures are isolated, private/internal content and cookies/history/container identity are excluded, and Mozilla data categories are declared. AMO signing/publication and a live Firefox round trip remain external gates. |
| WA-019 | Verified in automation; browser-store smoke open | Protocol v2 captures an opaque extension-local profile key, window/session and unique desktop-entry/monitor linkage; the planner propagates the selected reuse/reopen/review policy. Exact URLs, including query/path, are considered only within the same profile; `file://` and profile-unknown legacy sessions are duplicate-only. Tests cover v7 migration, payload propagation, redaction, timeout/unavailable outcomes, and protocol mismatch. A live multi-profile browser smoke is still required before a release claim. |
| WA-021 | Implemented in automation; live Terminal gate open | The optional PowerShell prompt hook tracks profile GUID and current directory by `WT_SESSION`; an accessibility title marker maps reports to tab order. The save editor requires each tab directory and permits manual correction for untracked shells. The adapter builds a multi-tab `wt` command; preview/preflight check directories and prefer the installed alias, with a narrowly scoped shell-alias fallback. A real Windows Terminal two-tab round trip, including distinct current paths and tab focus, remains required. Old prototype saves must be recaptured. |
| WA-022 | Verified in automation; corrected live capture needs rerun | Settings defaults to disabled and schema v11 keeps upgraded installs opt-in. Capture uses documented `GetWindowDesktopId` and `IsWindowOnCurrentVirtualDesktop`; a narrow Shell-cloak exception includes genuine inactive-desktop task windows while retaining app/tool/shell exclusions. The Save list groups observed desktops and monitors, and restore matching sees inactive-desktop windows to avoid duplicate launches. Planning and preview selection attach a move only to a revalidated HWND; execution uses only `MoveWindowToDesktop`. The implementation cannot create, enumerate, name, order, or switch desktops. Unsupported, missing, or rejected targets are skipped and leave the window accessible. Rerun the real Windows 10/11 two-desktop round trip and deleted-desktop fallback smoke before release. |
| #15 | Verified in automation and Windows 11 25H2 live smoke | File Explorer captures every folder tab and active selection per top-level window when file/folder capture is enabled. Restore revalidates the assigned HWND, adds only missing tab occurrences, preserves unrelated tabs, and reselects the saved active tab. Workspace v8 migration seeds the legacy active folder; portable-redacted export removes tab paths. The isolated three-folder smoke passed and its second restore opened zero tabs. |
| WA-030A | Verified in automation | Deterministic geometry covers sufficiently visible, fully off-screen, negative-origin, and maximized cases. The foreground-window command is wired through the native boundary and tray; live desktop behavior remains part of release smoke testing. |
| WA-030B | Verified in automation; live display-change smoke open | A stabilized departure/return tracker offers the saved layout once when it returns. Settings support disabled, ask-first, and automatic modes; automatic recovery creates a mandatory checkpoint and pauses while a foreground window covers its monitor. Synthetic tests cover return-only triggering, duplicate suppression, fullscreen geometry, and checkpoint gating. A real resolution/dock/fullscreen smoke remains required before release. |
| WA-032 | Verified in automation | Most-specific alias capture, per-device persistence, exact-path-first resolution, mapped fallback, and unresolved-alias handling are covered while retaining the absolute path. |
| WA-033 | Partial | The internal transfer service has atomic exact/portable export, bounded schema validation, redaction, and collision-safe clone/reject behavior. User-facing export/import, configurable inclusion, conflict preview, and old transfer-schema migration are missing. |
| WA-034 | Partial | `ISyncProvider` and an atomic generic folder transport exist and preserve the previous provider copy on write failure. Staging into local validation/migration, manifests, device IDs, conflict detection/copies, retries, exclusion policy, orchestration, and UI are missing. |
| WA-046 | Partial | The planner-only CLI is deterministic, redacted, versioned, and exercised by the test suite and a direct command smoke. Only two fixture files exist; the full required topology/application/resource matrix and explicit CI fixture job are incomplete. |

## Structural verification

Application services are grouped under `Services/AppAdapters`, `Browser`, `Capture`, `Desktop`,
`Restore`, `Storage`, `System`, and `Workspace`. Matching and native window operations have their
own testable subfolders. Jump Lists, repositories, migrations, and layout coordination likewise
have explicit owners. `WorkspaceService` remains the application-facing façade for capture,
restore, and persistence while checkpoint, diagnostics, and layout-variant behavior live in
focused collaborators.

## Release gates still open

1. Run and record the real-Windows WA-006A Restore/Exact Switch/Undo/undo-of-undo checklist.
2. Smoke the tray rescue command with restored and maximized foreground windows on a changed
   monitor topology.
3. Record the warm exact-topology Resume benchmark requested by WA-005A.
4. Expand WA-046 to the fixture matrix listed in `restore-simulation.md`.
5. Keep WA-033 and WA-034 out of shipped-feature claims until their missing workflows are built.
6. Submit/sign the Firefox connector on AMO, replace the search URL with its exact listing URL, and
   record a live Firefox capture/restore smoke including a partial tab failure.
