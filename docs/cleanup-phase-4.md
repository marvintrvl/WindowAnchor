# Cleanup Phase 4 — Additive Service Decomposition

**Implemented increment:** 2026-09-06
**Status:** Complete as an additive-boundary phase; detailed core decomposition continues in Phase 6

Phase 4 was delivered as small extractions behind the existing `WorkspaceService`, planner,
executor, and Settings entry points. This kept each change reviewable and made the existing golden
capture/plan and service tests the compatibility gate.

## Completed boundaries

### Restore observation builder

`Services/RestoreObservationBuilder.cs` now owns the read-only environment boundary used to build a
restore plan:

- conversion of live HWND/PID records into immutable live identities;
- launch, executable, packaged-app, and web-app shortcut observations;
- running-application and remembered-match observations;
- browser-session capability classification; and
- current monitor topology adaptation and exact-topology comparison.

`WorkspaceService` remains the façade and delegates plan observation to this builder. The existing
`MonitorTopologiesMatchExactly` compatibility helper forwards to the extracted policy.

### Capture and transaction boundaries

`Services/WorkspaceCaptureBuilder.cs` now coordinates snapshot construction, optional browser
enrichment, cancellation, and finalization without persistence. `WorkspaceService` supplies the
existing snapshot-construction delegate and remains responsible for the public capture façade.
The detailed entry construction and file-recovery policies intentionally remain in
`WorkspaceService` and are explicitly scheduled for Phase 6; Phase 4 did not complete that deeper
move.

`Services/RestoreTransactionCoordinator.cs` owns single-flight checkpoint admission, cancellation
mapping, and guaranteed gate release. Checkpoint-specific capture and storage policy remains in
`WorkspaceService`; switch, restore, and undo callers use the same coordinator path.

### Pure planner policies and placement geometry

`Services/RestorePlannerPolicies.cs` contains store-app detection, browser-process classification,
and running-application identity policy. `Services/RestorePlacementGeometry.cs` contains normalized
layout adaptation, work-area clamping, and DPI scaling. `RestorePlanner` still owns action ordering,
candidate assignment, confidence/evidence, and launch decisions.

`RestoreExecutor` now creates one `RestoreExecutionSession` containing entry state, action results,
assigned HWNDs, indexed actions, and browser-session outcome. The execution order remains preflight,
browser/launch, correlated readiness, mutation, verification/retry, and aggregation.

### Settings row models

`UI/SettingsRows.cs` now owns the workspace, hotkey, and monitor binding rows. `SettingsWindow` keeps
the event handlers and collections that coordinate those rows, preserving all XAML names, tags,
keyboard handling, focus behavior, and owner assignment.

`Services/WorkspaceOrderPolicy.cs` owns the pure preferred-order/newest-fallback policy used by the
Settings workspace list.

## Preservation evidence

- Release tests: **187 passed, 0 failed, 0 skipped**.
- Published validation executable: `publish/cleanup-phase-4/WindowAnchor.exe`.
- Executable size: 79,532,665 bytes.
- SHA-256: `9FD7EB21636D7EF621758648D5CBCCB14F1D693A21946998D72444A198251786`.
- Existing restore-plan golden cases, semantic placement cases, monitor-topology cases, readiness,
  checkpoint, switching, persistence, migration, and privacy tests remain enabled.

## Follow-on work

Phase 5 completed lifecycle/interop hardening, analyzer baselining, native/COM result review,
observation measurement, and browser connector version policy. Phase 6 is planned in
`docs/cleanup-phase-6-plan.md`. The manual UI and multi-monitor smoke matrix remains a release gate
for the published executable.
