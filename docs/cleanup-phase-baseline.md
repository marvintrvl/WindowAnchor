# Cleanup Behavioral Baseline

This document freezes the observable baseline used for the first WindowAnchor cleanup program.
Measurements were taken from the unmodified `v1.5.1` production code at commit `1ab6293` on
2026-09-05. Values are diagnostic baselines, not performance guarantees.

## Automated baseline

- Command: `dotnet test WindowAnchor.sln --configuration Release --no-restore`
- Result: **177 passed, 0 failed, 0 skipped**
- Framework: .NET 8 application and test targets, Windows x64 host
- Clean single-file publish: successful
- Published executable: 80,892,798 bytes
- Baseline SHA-256: `BB033770BEFE3F83329A6581175782AE15BF0BD14F913374C5F96CB57063A645`
- Baseline publish output: `publish/cleanup-baseline/WindowAnchor.exe` (ignored build artifact)
- Observed publish wall time: approximately 32 seconds on the audit machine

The repository already contains committed compatibility fixtures for:

- legacy v1 profile import;
- legacy v2, current v3, and current v4 workspaces;
- legacy v1, current v2, and current v3 settings;
- corrupt and unsupported-future workspace/settings documents; and
- invalid v3 workspace and structured-redaction cases.

These fixtures are part of the cleanup compatibility contract and must not be deleted with legacy
migration code.

## Observed production timing baseline

The privacy-safe structured log from a real nine-entry automatic restore on 2026-09-05 recorded:

| Marker | Observed value |
|---|---:|
| `app.starting` to `display.initial_topology` | 408 ms |
| Metadata-only checkpoint snapshot | 568 ms |
| Complete durable checkpoint operation | 620 ms |
| Approved restore execution | 11,915 ms |
| Checkpoint plus execution | approximately 12,534 ms |
| Assigned and verified windows | 6 |
| Placement retries | 0 |

The operation completed successfully. The current event taxonomy does not expose a separate restore
preview-construction duration, and the retained log did not contain a real full named capture with
Jump List indexing. Those two measurements must be collected before a later phase changes planner
observation or full capture performance. Adding such timing must remain privacy-safe and must not
include window titles, paths, URLs, or raw monitor identifiers.

## Phase 0-2 comparison

After a clean restore/test/publish of Phases 0-2:

- Release tests: **177 passed, 0 failed, 0 skipped**;
- final executable: 79,517,693 bytes;
- final SHA-256: `901B509E6DEABCD531ACFAEE821703C4E21FB60B3B578533FF866740D28F2CD2`;
- size change from the baseline publish: **-1,375,105 bytes** (-1.70%); and
- published validation path: `publish/cleanup-phases-0-2/WindowAnchor.exe`.

The resolved application dependency graph now contains only the three direct runtime packages that
have production callers: `H.NotifyIcon.Wpf`, `OpenMcdf`, and `WPF-UI`. The publish output is an
ignored local validation artifact and is not intended to be committed.

## Phase 3 comparison

After a clean restore/test/publish including Phase 3:

- Release tests: **184 passed, 0 failed, 0 skipped**;
- final executable: 79,518,095 bytes;
- final SHA-256: `F26A0647936921E318EF4EADA57D81CA852C7EB76437F0F27ADDE06EE2B8F605`;
- size change from the baseline publish: **-1,374,703 bytes** (-1.70%);
- size change from the Phase 0-2 executable: **+402 bytes**; and
- published validation path: `publish/cleanup-phase-3/WindowAnchor.exe`.

The seven added test cases define the canonical process-name normalization corpus. The publish
output remains an ignored local validation artifact and is not intended to be committed.

## Phase 4 incremental comparison

After the completed Phase 4 extraction:

- Release tests: **187 passed, 0 failed, 0 skipped**;
- validation executable: 79,532,665 bytes;
- validation SHA-256: `9FD7EB21636D7EF621758648D5CBCCB14F1D693A21946998D72444A198251786`; and
- published validation path: `publish/cleanup-phase-4/WindowAnchor.exe`.

The published output remains an ignored local validation artifact and is not intended to be committed.

## Phase 5 incremental comparison

After lifecycle and interop hardening plus its correctness review:

- Release tests: **194 passed, 0 failed, 0 skipped**;
- focused analyzer build: **0 warnings, 0 errors**;
- validation executable: 79,562,415 bytes;
- validation SHA-256: `A6F7279C07C243DBEF346FA8C9D1366D30E86CAAE90F281EB57347C7E4AA8AF5`;
- published validation path: `publish/cleanup-phase-5-reviewed/WindowAnchor.exe`; and
- the validation executable is ReadyToRun, self-contained, single-file, compressed, and win-x64.

The published output remains an ignored local validation artifact and is not intended to be committed.

## Phase 6.0/6.1 incremental comparison

After the equivalence gate and detailed capture decomposition:

- Release tests: **203 passed, 0 failed, 0 skipped**;
- focused analyzer build: **0 warnings, 0 errors**;
- `WorkspaceService.cs`: 892 lines, down from the 1,889-line reviewed Phase 5 baseline;
- validation executable: 79,555,244 bytes;
- validation SHA-256: `C226038E6CC00D8439F17EB187AAFF50D8B1ECC69F02890F7B4B650F3633B8C0`;
- published validation path: `publish/cleanup-phase-6-1/WindowAnchor.exe`; and
- the validation executable is ReadyToRun, self-contained, single-file, compressed, and win-x64.

Capture progress/order, browser-after-native ordering, cancellation, entry/resource shapes, and
whole-plan output now have explicit equivalence coverage. The published output remains an ignored
local validation artifact and is not intended to be committed.

## Phase 6.2/6.3 incremental comparison

After the identity and pure restore-planning decomposition:

- Release tests: **203 passed, 0 failed, 0 skipped**;
- analyzer-enabled Release build: **0 warnings, 0 errors**;
- the 853-line identity implementation is separated into public models, extraction, public façade,
  and internal matching policy without changing names or accessibility;
- `RestorePlanner.cs` is 440 lines, down from 1,287 at the Phase 6 entry point;
- the complete redacted-plan SHA-256 fixture is unchanged, including ordered actions, protected
  handles, warnings/errors, confidence, and evidence; and
- no publish is scheduled for this increment; the next Phase 6 validation executable follows 6.4.

These changes move pure logic only. They do not change schemas, external observation, native calls,
browser protocol, persistence, or executor phase order.

## Phase 6.4-6.6 final automated comparison

After ordered executor extraction, Settings feature ownership, and convergence cleanup:

- Release tests: **203 passed, 0 failed, 0 skipped**;
- analyzer-enabled Release build: **0 warnings, 0 errors**;
- all **26 Settings XAML handlers** resolve exactly once across the WPF partial class;
- `RestoreExecutor.cs`: 192 lines, down from 1,314 at the Phase 6 entry point;
- `WorkspaceService.cs`: 701 lines, down from 1,889 at the reviewed Phase 5 baseline;
- `SettingsWindow.xaml.cs`: 127-line root plus four feature partials, down from one 801-line file;
- final validation executable: 79,492,993 bytes;
- final validation SHA-256: `A1561D04A936EE9C00C07ED25FEE8372E1829A7C74B858088F98DD6097DFF13E`;
- baseline publish size change: **-1,399,805 bytes** (-1.73%); and
- validation path: `publish/cleanup-phase-6-final/WindowAnchor.exe`.

The validation artifact remains ignored and is not a release. The native-window, disconnected-
monitor, installed-browser, keyboard/focus, and 100%/125%/150% Windows scaling matrix remains a
manual release gate.

## Active-path characterization added for cleanup

The obsolete `WorkspaceService.BuildProcessStartInfo` helper had the only direct assertion that a
dedicated browser uses `--new-window <saved URL>`. That behavior is now characterized at the active
pure `RestorePlanner` boundary, including the action kind, target, arguments, shell-execute mode, and
readiness action. Existing active-path tests already cover:

- Store/MSIX activation through `explorer.exe shell:AppsFolder\\<AUMID>`;
- missing and stale launch resources as blocked plan outcomes;
- selective and align/minimize restore modes through the coordinator/execution path;
- exact switch-risk observations and close-handle tracking; and
- clearing remembered matches directly through `SettingsService`.

## Manual release gate

Automated service tests deliberately do not launch or move real desktop windows. Before a cleanup
release is tagged, run the following against the final published executable:

- [ ] Start with existing settings; verify tray initialization, Settings, and clean exit.
- [ ] Save from both tray and Settings, including a subset and connector-unavailable capture.
- [ ] Restore a single-monitor workspace and approve/disable entries in Preview.
- [ ] Restore a saved multi-monitor workspace while one monitor is disconnected.
- [ ] Resolve an ambiguous Explorer/browser match by mouse and keyboard and remember the choice.
- [ ] Switch workspaces; verify destination windows remain open and unrelated windows receive only
      normal close requests.
- [ ] Cancel during readiness or close-wait and confirm no endless notifications.
- [ ] Run Undo Last Restore and undo the undo.
- [ ] Verify browser tabs/groups/pinned state and ordinary-launch fallback.
- [ ] Verify hotkeys, monitor aliases, workspace order, startup behavior, and progress windows.
- [ ] Export a diagnostic log containing test-sensitive data and verify redaction.

The detailed invariants and extended scaling/failure matrix remain in
`docs/codebase-cleanup-audit.md`.
