# Cleanup Phase 6 Plan — Responsibility-Based Core Decomposition

**Status:** 6.0-6.6 implementation and automated gates complete; manual release matrix pending
**Prepared:** 2026-09-06
**Entry baseline:** 194 Release tests after the Phase 3–5 correctness review
**Current automated gate:** 203 Release tests after 6.6

## Purpose

Phase 6 will finish the useful decomposition that Phases 3–5 prepared. It will not split files at
an arbitrary line-count threshold. A file is a candidate when it contains multiple independently
changing policies, makes behavior difficult to test, or forces unrelated contributors to edit the
same orchestration surface.

Current priority indicators are:

| File | Approximate size | Current concern |
|---|---:|---|
| `WorkspaceService.cs` | 701 lines after 6.6 | Active capture, observation, checkpoint, execution, and undo orchestration remains; superseded compatibility projection/wrappers are gone. |
| `RestoreExecutor.cs` | 192 lines after 6.4 | Public façade owns construction and fixed phase sequencing; detailed execution lives in internal phase objects. |
| `RestorePlanner.cs` | 440 lines after 6.3 | Deterministic composition and ordered action construction remain in the public façade; assignment, approval, launch, and placement policies are separate. |
| Identity subsystem | 1,007 lines across four focused files after 6.2 | Public models, extraction, matching façade, and internal scoring now have separate ownership without changing public type names. |
| `SettingsWindow.xaml.cs` | 127-line root plus four feature partials after 6.5 | One WPF visual tree remains while startup, hotkey, monitor, and workspace-list event ownership is separated. |

These numbers are diagnostic context, not acceptance criteria. `SchemaMigration.cs`,
`StructuredLogging.cs`, repository implementations, data-model files, and XAML may remain larger
when their content is cohesive or constrained by serialization, native, or UI contracts.

## Non-negotiable constraints

- No workspace/settings schema, stable-ID, connector protocol, or public behavior changes.
- Preserve checkpoint-before-mutation and executor order: preflight → browser/launch → correlated
  readiness → mutation → re-observation/verification → bounded retry → aggregation.
- Preserve one-HWND-per-entry assignment and stale-plan revalidation.
- Preserve repeated native observations across waits and mutation boundaries.
- Preserve browser call ordering, normal `WM_CLOSE` switching, missing-monitor adaptation,
  placement clamping, and cancellation semantics.
- Keep `WorkspaceService`, `RestorePlanner`, and `RestoreExecutor` entry points as façades until all
  current callers and compatibility tests have migrated.
- Do not introduce application-name exception lists, cross-phase caches, a dependency-injection
  framework, or new public abstractions solely to reduce line count.

## Work sequence

### 6.0 — Strengthen equivalence tests before moving logic

**Status: Complete (2026-09-06).**

Add deterministic fixtures that compare complete outputs rather than individual helper methods:

1. Golden capture results for selected/all windows, file discovery enabled/disabled, bounded-search
   exhaustion, PWA/dedicated-browser entries, and connector captured/unavailable/timed-out results.
2. Golden plans for exact/missing topology, ambiguous and remembered matches, browser fallback,
   packaged applications, selective restore, and align/minimize.
3. Executor trace tests that record native observations and mutations and assert ordering,
   cancellation boundaries, assignment uniqueness, readiness correlation, and bounded retry.
4. Transaction stress tests for queued cancellation, shutdown during checkpoint/execution, and
   concurrent disposal.

**Gate:** output objects and ordered traces are frozen before production code moves.

The completed gate adds full selected/all-window capture projections, generated-ID/timestamp
invariants, file-enabled/disabled behavior, PWA/Explorer/dedicated-browser shapes, bounded-search
exhaustion, browser-after-native ordering, mid-capture cancellation, and a whole-plan redacted JSON
fingerprint. The existing planner corpus supplies the exact/missing topology, ambiguity, learned
hint, browser fallback, packaged-app, selective, and align/minimize scenarios. Executor tests now
include an explicit ordered observation/mutation/verification trace. Transaction coverage includes
queued cancellation, shutdown during checkpoint/execution, and concurrent disposal.

### 6.1 — Complete capture decomposition behind `WorkspaceService`

**Status: Complete (2026-09-06).**

The existing `WorkspaceCaptureBuilder` remains the top-level capture coordinator. Move detailed
work out of `WorkspaceService.TakeSnapshot` into focused internal collaborators:

- `WorkspaceSnapshotBuilder`: monitor/window selection and final snapshot/entry assembly;
- `CaptureResourceResolver`: title-derived resources, Jump List lookup, and bounded common-folder
  search with the existing budget accounting; and
- `CapturedWindowEntryFactory`: ordinary, Explorer, PWA, and dedicated-browser entry construction.

Dependencies must be constructor-injected through existing inventory/resolver interfaces. The
builder returns existing models and performs no persistence.

**Gate:** golden captures are structurally equal, progress stages/order match, cancellation stops
the same boundary, and browser capture remains after native snapshot construction.

At the 6.1 boundary, `WorkspaceService.TakeSnapshot` became a compatibility façade over
`WorkspaceSnapshotBuilder`; 6.6 later removed it after capture callers and tests used
`CaptureWorkspaceAsync` directly.
`CapturedWindowEntryFactory` owns ordinary, Explorer, PWA, and dedicated-browser entry shapes, and
`CaptureResourceResolver` owns title parsing, Jump List lookup, VS Code/Cursor launch adaptation,
and the cumulative common-folder search budget. `WorkspaceCaptureBuilder` remains responsible for
placing optional browser enrichment after native snapshot construction; none of the extracted
components persists data.

6.1 validation: 203 Release tests, analyzer-enabled Release build with 0 warnings/errors, and a
ReadyToRun compressed self-contained single-file win-x64 executable at
`publish/cleanup-phase-6-1/WindowAnchor.exe` (79,555,244 bytes; SHA-256
`C226038E6CC00D8439F17EB187AAFF50D8B1ECC69F02890F7B4B650F3633B8C0`). The artifact is local and
ignored; the manual native/UI/browser matrix remains pending.

### 6.2 — Split identity definitions, extraction, and matching

**Status: Complete (2026-09-06).**

Move types without changing names or accessibility:

- identity/evidence/result records to `WindowIdentityModels.cs`;
- saved/live extraction and normalization to `WindowIdentityExtractor.cs`; and
- candidate scoring, ambiguity policy, and learned-hint application to `WindowMatchResolver.cs`.

Keep `ProcessIdentityNormalizer` as the one canonical process-key implementation. Browser
capability lists remain separate because matching, launch, readiness, and connector support are
different policies.

**Gate:** existing scoring corpus plus golden candidate order, evidence text, ambiguity thresholds,
and remembered-choice behavior.

Identity and evidence/result records now live in `WindowIdentityModels.cs`, saved/live extraction
and normalization live in `WindowIdentityExtractor.cs`, and the detailed scoring, ambiguity, and
learned-hint implementation lives in internal `WindowMatchResolver.cs`. `WindowMatcher.cs` retains
the complete existing public API as a small compatibility façade. The canonical
`ProcessIdentityNormalizer` and the distinct browser capability policies were not merged or
duplicated. Existing scoring tests and the whole-plan fingerprint passed unchanged.

### 6.3 — Decompose pure restore planning

**Status: Complete (2026-09-06).**

Extract internal pure components while keeping `RestorePlanner.Build` and approval methods stable:

- `RestoreAssignmentPlanner`: session-wide candidate ranking and unique HWND assignment;
- `RestoreApprovalProjector`: disabled entries and user-resolved ambiguity projection;
- `RestoreLaunchPlanner`: resource availability, packaged/PWA/browser/file launch decisions; and
- retain `RestorePlacementGeometry` for topology/DPI placement math.

`RestorePlanner` should finish as the deterministic composer of these results and the ordered
action list.

**Gate:** byte-for-byte-equivalent serialized plans for the 6.0 fixtures, including warnings,
blocking errors, action order, protected handles, and confidence/evidence.

`RestoreAssignmentPlanner` now owns the planning-session handle set, candidate projection,
ambiguity protection, and learned-hint lookup. `RestoreApprovalProjector` owns disabled-entry and
explicit ambiguity-choice projection. `RestoreLaunchPlanner` owns resource availability and all
packaged/PWA/browser/file/application launch decisions. Topology/DPI math was moved into the
existing `RestorePlacementGeometry` boundary. The public `RestorePlanner` methods remain stable and
compose those pure results in their original order.

6.2/6.3 validation: 203 Release tests and an analyzer-enabled Release build with 0 warnings/errors.
The Phase 6 golden plan fingerprint remained unchanged, covering serialized warnings, blocking
errors, candidate confidence/evidence, action order, and protected handles. Per the planned gate
cadence, the next validation executable is produced after 6.4; the successful 6.1 artifact remains
the latest Phase 6 publish.

### 6.4 — Decompose ordered restore execution

**Status: Complete (2026-09-06).**

Promote the current session state into an internal execution context and extract phase objects:

- `RestorePreflightPhase`;
- `RestoreBrowserAndLaunchPhase`;
- `RestoreReadinessPhase`;
- `RestorePlacementVerificationPhase`; and
- `RestoreResultAggregator`.

The top-level executor invokes them sequentially. Phase objects must not start their own parallel
mutation, cache live HWNDs across phase boundaries, or bypass the shared session assignment set.

**Gate:** trace equivalence, cancellation at every boundary, closed/reused HWND cases, retry limits,
browser fallback, and no mutation after stale preflight rejection.

`RestoreExecutor` now constructs and sequentially invokes `RestorePreflightPhase`,
`RestoreBrowserAndLaunchPhase`, `RestoreReadinessPhase`, and
`RestorePlacementVerificationPhase`; `RestoreResultAggregator` projects the result. Every phase
shares one `RestoreExecutionContext`, assigned-HWND set, and `RestoreWindowRevalidator`. No native
observation is cached across phase boundaries and no mutation was parallelized. The established
executor trace, cancellation, stale-plan, browser fallback, readiness, and retry suites pass
unchanged.

### 6.5 — Reduce UI code-behind by feature ownership

**Status: Implementation and automated gate complete (2026-09-06); manual DPI/input matrix pending.**

Use WPF-safe partial classes or internal controllers for Settings startup/default-workspace,
hotkey recording, monitor aliases, and workspace-list actions. Extract preview row construction
only after keyboard, radio-button hit testing, focus, owner, theme, and DPI behavior is recorded.
Do not split `SettingsWindow.xaml` merely because it is long.

**Gate:** build plus manual mouse/keyboard/focus tests at 100%, 125%, and 150% scaling. Both tray
and Settings save paths must still use `SaveWorkspaceWorkflow`.

`SettingsWindow.xaml` and every routed-event handler name remain unchanged. The root partial owns
construction, browser integration, notifications, and remembered-match controls;
`SettingsWindow.Startup.cs`, `.Hotkeys.cs`, `.Monitors.cs`, and `.Workspaces.cs` own their respective
features. Binding rows remain in `SettingsRows.cs`, and both save entry points still use
`SaveWorkspaceWorkflow`. XAML compilation and the Release suite pass; physical DPI, mouse,
keyboard, and focus verification remains part of the final manual matrix.

### 6.6 — Convergence and façade cleanup

**Status: Implementation and automated gate complete (2026-09-06); final manual matrix pending.**

After all callers use the new internal boundaries, remove only forwarding members with repository-
wide zero callers. Update architecture documentation and dependency diagrams. Run the full manual
matrix before calling Phase 6 complete.

Tests that targeted the old assignment/session adapters now target `RestoreAssignmentPlanner`,
`RestoreExecutionContext`, `RestoreWindowRevalidator`, and `RestoreResultAggregator`. This made the
test-only `WindowRestorePlanner`, legacy `RestoreSessionContext` result model/projection, synchronous
`WorkspaceService.TakeSnapshot`, unused void restore wrapper, topology forwarder, execution
forwarder, fingerprint getter, and public same-site forwarder removable. A repository-wide sweep
found production callers for the remaining `WorkspaceService`, `RestorePlanner`, and
`WindowMatcher` entry points, so they remain. Architecture and data-flow documentation now names
the final component graph.

6.6 automated validation: 203 Release tests, 26/26 Settings XAML handlers resolved exactly once,
an analyzer-enabled Release build with 0 warnings/errors, and a ReadyToRun compressed
self-contained single-file win-x64 executable at
`publish/cleanup-phase-6-final/WindowAnchor.exe` (79,492,993 bytes; SHA-256
`A1561D04A936EE9C00C07ED25FEE8372E1829A7C74B858088F98DD6097DFF13E`). The physical multi-DPI,
monitor, native-window, and installed-browser release matrix remains manual.

## Commit and review strategy

Use one reviewable commit per numbered work item. Every production move must include its equivalence
test in the same commit. Do not combine capture, planner, executor, and UI movement into one commit.

For every work item run:

```powershell
dotnet test tests/WindowAnchor.Tests/WindowAnchor.Tests.csproj --configuration Release
dotnet build WindowAnchor.sln --configuration Release -p:RunAnalyzers=true
```

Publish a self-contained single-file win-x64 executable after 6.1, 6.4, and 6.6. The 6.6 artifact
must also pass the full manual release matrix in `docs/codebase-cleanup-audit.md`.

## Completion criteria

Phase 6 is complete when responsibilities are independently testable, not when every file is under
300 lines. Completion requires all automated gates, output/trace equivalence, a successful final
publish, the full manual UI/native/browser matrix, updated documentation, and no retained façade
whose only purpose is forwarding to a migrated internal component.
