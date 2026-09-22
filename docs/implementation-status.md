# Implemented Ticket Verification

**Audit date:** 2026-09-22
**Automated gate:** `dotnet test .\WindowAnchor.sln --configuration Release --no-restore` — 292 passed, 0 failed, 0 skipped
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
| WA-030A | Verified in automation | Deterministic geometry covers sufficiently visible, fully off-screen, negative-origin, and maximized cases. The foreground-window command is wired through the native boundary and tray; live desktop behavior remains part of release smoke testing. |
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
