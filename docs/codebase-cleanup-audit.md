# WindowAnchor Codebase Cleanup Audit

**Audit date:** 2026-09-05
**Audited baseline:** `v1.5.1` / commit `1ab6293`
**Scope:** tracked application source, tests, browser connector, build/release configuration, and documentation assets
**Status:** Phases 0-5 and Phase 6.0-6.6 implementation/automated gates complete; manual release gate remains

References in the findings identify the audited pre-cleanup baseline. See
`docs/cleanup-phase-baseline.md` for measured evidence and the release smoke gate.

## Execution status

- **Phase 0 — complete:** baseline publish and timings recorded, compatibility fixtures confirmed,
  and dedicated-browser launch behavior moved to an active planner characterization test.
- **Phase 1 — complete:** unused Hosting/WinForms inputs removed; stale release archives and two
  orphaned screenshots removed; maintained `Roadmap.md` is no longer ignored; local draft ignores
  are explicitly documented.
- **Phase 2 — complete:** the audited obsolete service façades were removed, useful launch coverage
  was transferred to the active planner path, and all remaining message-only logger calls and legacy
  logger overloads were removed.
- **Phase 3 — complete:** tray and Settings saves now share one workflow; restore variants share one
  notification/result path; embedded UI types have dedicated files; and process-name comparison uses
  one tested canonical normalizer without merging distinct browser capabilities.
- **Phase 4 — complete as additive boundaries:** capture/browser coordination, restore observation,
  transaction coordination, planner policies and geometry, executor session state, workspace
  ordering, and Settings row models sit behind existing façades. Detailed capture/planner/executor
  decomposition remains scheduled for Phase 6.
- **Phase 5 — complete:** observation timing, explicit async disposal, native-messaging stream reuse,
  focused analyzer policy, native/COM result handling, and browser connector timing/version policy
  are documented and implemented without changing protocol or restore ordering.
- **Correctness review:** fixed active-restore disposal races, cancellation-under-lock paths,
  display-change token ownership, swallowed browser cancellation, native-message document lifetime,
  and concurrent response framing; removed an ineffective publish-exclusion property.
- **Phase 6.0/6.1 — complete:** whole-output capture/plan characterization, ordered executor and
  transaction stress traces, and detailed capture/resource/entry construction extracted behind
  the unchanged `WorkspaceService` and `WorkspaceCaptureBuilder` façades.
- **Phase 6.2/6.3 — complete:** identity models, extraction, and matching have distinct owners;
  restore assignment, approval, launch, and placement policies are internal pure components behind
  the unchanged public matching/planning APIs.
- **Phase 6.4 — complete:** execution uses one shared context through explicit preflight,
  browser/launch, readiness, placement-verification, and result-aggregation components.
- **Phase 6.5 — implementation complete:** Settings is split into WPF partials by feature without
  changing XAML or event names; the physical DPI/input matrix remains manual.
- **Phase 6.6 — implementation complete:** obsolete compatibility paths were removed only after
  their tests moved to active boundaries; architecture and data-flow documentation is current.
- **Automated gate:** 203 passed, 0 failed, 0 skipped; 26/26 Settings handlers resolved; analyzer
  build clean; final single-file win-x64 validation publish successful.
- **Manual gate:** pending on the final published executable; this is intentionally a user-visible
  release gate rather than an automated native-window test.

## Executive summary

WindowAnchor has a healthy behavioral baseline and a strong set of service-level tests, but recent
restore work has concentrated too many responsibilities in a few large classes. The safest cleanup
is therefore not a broad deletion pass. It is a staged program that first removes independently
verifiable waste, then characterizes orchestration behavior, and only then decomposes the restore
pipeline without changing its ordering or safety boundaries.

The Release test baseline is **177 passed, 0 failed, 0 skipped**. An opt-in build with all current
.NET analyzers also compiled successfully, although it produced 864 diagnostic occurrences. Most
of that volume is low-value style or API-design noise for internal DTOs, P/Invoke declarations, and
descriptively named tests. It should be baselined selectively rather than fixed mechanically.

The highest-confidence cleanup opportunities are:

1. Remove the unused `Microsoft.Extensions.Hosting` package and unused WinForms project switch.
2. Stop tracking two obsolete root-level release archives, including a 74 MB historical manual
   install package; keep releases in GitHub release assets instead.
3. Remove or archive two unreferenced screenshots after checking for external links.
4. Delete obsolete public service façades whose only occurrences are their declarations (or
   characterization tests written directly against them).
5. Consolidate the duplicated Save Workspace workflow and repeated restore-notification wrappers.
6. Decompose `WorkspaceService`, `RestorePlanner`, `RestoreExecutor`, and the Settings code-behind in
   behavior-preserving stages.
7. Make lifecycle ownership and cancellation disposal explicit, and reuse native-messaging streams.
8. Centralize stable process/browser normalization while keeping matching, readiness, and launch
   policies separate.

No unused WPF window, dialog, or custom control was found. No redundant database queries exist
because the application has no database. The browser connector has no remote web API calls; its
Chrome extension calls are part of capture/restore semantics and are not confirmed waste.

## Non-negotiable functionality-preservation contract

Cleanup work must retain the following observable `v1.5.1` behavior:

- atomic, versioned named-workspace, checkpoint, temporary, and settings persistence;
- all supported workspace/settings migrations and one-time legacy profile import;
- selective capture, incognito/password-manager exclusion, file discovery, and Jump List recovery;
- optional Chromium tab/group/pin/active-state/window-geometry capture and restore, with ordinary
  browser launch as the fallback;
- stable workspace/entry IDs, composite identity, confidence/evidence, ambiguity selection, and
  remembered choices;
- one-HWND-per-entry assignment across the full restore session;
- native capability-based task-window filtering, including cloaking, ownership, styles, and hosted
  Windows identity;
- immutable preview, approval projection, stale-plan checking, missing-resource reporting, and safe
  cancellation before mutation;
- transactional pre-restore checkpoints, retention/corruption isolation, Undo Last Restore, and
  undo-of-undo behavior;
- semantic/normalized/DPI-aware placement, missing-monitor fallback, clamping, and final placement
  verification with bounded same-HWND retry;
- launch-correlated readiness with a real wall-clock limit and no waits for unrelated background
  processes;
- workspace switching that preserves approved destination windows, requests normal closure only,
  tracks exact HWNDs, is single-flight, and never force-closes;
- progress reporting for checkpoint, resource, browser, launch, readiness, close-wait, placement,
  verification, and cancellation stages;
- startup/default/last workspace behavior, monitor fingerprinting and aliases, hotkeys, tray actions,
  notifications, workspace ordering, and browser-connection setup;
- privacy-safe structured logging and export redaction; and
- self-contained, single-file Windows x64 publishing plus versioned browser-connector assets and
  checksums.

Repeated OS observations at planning, preflight, readiness, mutation, and verification boundaries
are part of this safety contract. They must not be removed merely because they look duplicative:
Windows handles can be reused, windows can disappear, plans can become stale, and applications can
move themselves after placement.

## Review method and interpretation limits

The audit used repository-wide declaration/call-site searches, project/dependency inspection,
tracked-file and size inspection, UI construction/XAML event tracing, targeted restore-flow reading,
the existing Release test suite, and an opt-in latest/all .NET analyzer build.

Reference counts are not sufficient evidence for removing WPF, JSON, COM, or native interop code.
XAML loading, serialization, finalizers, P/Invoke, and COM activation can all be invisible to an
ordinary text search. Items affected by those mechanisms are explicitly retained or assigned a
manual validation gate below.

## Findings and recommended changes

### C01 — Remove an unused hosting dependency

**Evidence:** `src/WindowAnchor/WindowAnchor.csproj:37` references
`Microsoft.Extensions.Hosting` 10.0.3, but there is no `IHost`, `Host.Create*`, hosting namespace, or
hosting-service registration in application or test source. `App.xaml.cs` manually owns the
composition root.

**Why unnecessary:** The package adds restore/publish inputs and implies an architecture that the
application does not use.

**Impact of removal:** Smaller dependency graph, less restore and vulnerability-audit surface, and
clearer startup architecture. Single-file size may improve slightly; measure rather than assume.

**Risk:** Low. A transitive assembly could theoretically be loaded dynamically, but no code,
configuration, or packaging evidence indicates that.

**Plan:** Remove only the package reference, restore/build/test/publish, start the packaged app, and
exercise tray and Settings startup. Do not introduce a host/container merely to justify the package.

### C02 — Remove the unused WinForms project switch

**Evidence:** `src/WindowAnchor/WindowAnchor.csproj:9` enables `UseWindowsForms`, while source contains
no `System.Windows.Forms`, `WindowsFormsHost`, or WinForms `Screen` use. Window/tray UI is WPF plus
`H.NotifyIcon.Wpf`.

**Why unnecessary:** It broadens the implied UI stack and generated references without supporting a
current feature.

**Impact of removal:** Clearer framework intent and potentially a smaller build graph.

**Risk:** Low, but verify tray icon, monitor discovery, file dialogs, and the packaged executable in
case a dependency has an undocumented build-time expectation.

**Plan:** Remove the property in its own commit and run the UI smoke checks listed below.

### C03 — Remove stale tracked release archives from the source tree

**Evidence:** The repository tracks:

- `WindowAnchor-Review-ManualInstall-win-x64.zip` — 74,364,879 bytes;
- `WindowAnchor-Browser-Connector.zip` — 37,506 bytes.

The release workflow creates versioned executable and browser connector assets plus
`SHA256SUMS.txt` from the tagged commit.

**Why unnecessary:** These archives duplicate release delivery, are not build inputs, and can become
stale independently of source. The 74 MB archive materially increases fresh-checkout size.

**Impact of removal:** Approximately 74.4 MB removed from the current Git tree and one ambiguous
manual-install path eliminated. Removing them now does **not** shrink existing Git history.

**Risk:** Low to medium. Existing documentation, issue comments, or external raw-file links may
point to the unversioned paths.

**Plan:** Search GitHub references before deletion, ensure the current tagged release assets remain
downloadable, remove the files in a repository-hygiene commit, and update any links to Releases.
Do not rewrite published history or the `v1.5.1` tag merely to purge the old blob.

### C04 — Resolve abandoned documentation images

**Evidence:** `docs/screenshots/settings_overview.png` and `docs/screenshots/tray_menu.png` are tracked
but unreferenced by tracked Markdown, HTML, or workflow files. The four other screenshots in that
directory are used by `README.md`. The `.gitignore` also excludes the actively maintained
`Roadmap.md` alongside local scratch-document names.

**Why unnecessary:** Unreferenced screenshots have no in-repository consumer, and file-specific
ignore rules obscure the intended documentation policy.

**Impact of removal:** Small size reduction (~145 KB) and less ambiguity about which screenshots
describe the current UI. Cleaning the ignore policy prevents accidental suppression after a file is
renamed or re-created.

**Risk:** Low to medium because external pages can embed raw GitHub assets without an in-repo link.

**Plan:** Check the repository website/release notes first. Remove truly unreferenced images, keep
the four README images, and replace obsolete name-by-name ignores with a documented generated-asset
policy. Do not ignore maintained Markdown documents.

### C05 — Remove obsolete service façades after targeted characterization

The following members have no production caller in the current tree:

| Candidate | Evidence | Recommended disposition |
|---|---|---|
| `WindowService.WriteDebugSnapshotToFile` (`WindowService.cs:410`) | Declaration only | Remove. It bypasses the normal privacy-safe diagnostics path; first confirm it is not invoked by an undocumented command-line flow. |
| `WindowService.CloseAllUserWindows` (`WindowService.cs:449`) | Declaration plus an XML-doc reference | Remove after preserving switch tests. It is superseded by exact-handle `RequestCloseUserWindowsExcept`, which supports bounded close tracking. |
| `WorkspaceService.GetMonitorDataForDialog` (`WorkspaceService.cs:176`) | Declaration only | Remove. Current dialogs derive their preview from `GetWindowPreviewForDialog`. |
| `StorageService.DeleteWorkspace(string name)` (`StorageService.cs:218`) | Declaration only | Remove. Name-based deletion conflicts with the stable-ID model and can be ambiguous. Retain ID/snapshot-based deletion. |
| `WorkspaceService.SaveWorkspace` (`WorkspaceService.cs:155`) | Declaration only | Remove the proxy. Current capture uses `CaptureWorkspaceAsync` plus `PersistCapture`; workspace editing uses `StorageService`. |
| `WorkspaceService.ClearAllWindowMatches` (`WorkspaceService.cs:1751`) | Declaration only | Remove. Settings calls `SettingsService.ClearAllWindowMatches` directly. |
| `WorkspaceService.RestoreWorkspaceSelectiveAsync` (`WorkspaceService.cs:1232`) | Declaration only | Remove the service compatibility wrapper after coordinator/preview tests prove the selective route. |
| `WorkspaceService.RestoreWorkspaceAlignAndMinimizeAsync` (`WorkspaceService.cs:1265`) | Declaration only | Remove the service compatibility wrapper after coordinator tests prove the current execution route. |
| `WindowService.CountUserWindows` (`WindowService.cs:526`) | Used only by one policy test | Test through `InspectUserWindows(...).Count`, then remove the redundant façade. |
| `WorkspaceService.BuildProcessStartInfo` (`WorkspaceService.cs:2194`) | Used only by one test block | Treat as legacy launch construction. Prove equivalent dedicated-app, Store/MSIX, and missing-resource behavior through planner/executor tests before removal. |

**Why unnecessary:** These members expose earlier storage/restore paths alongside the current
planner/coordinator/executor architecture. Keeping both increases the number of apparent supported
ways to perform safety-sensitive work.

**Impact of removal:** A smaller public surface, fewer misleading entry points, and easier future
decomposition of `WorkspaceService` and `WindowService`.

**Risk:** Medium as a group. WindowAnchor is an executable rather than a published library, but
removing restore methods without replacement characterization could silently drop an edge case.

**Plan:** Remove one logical group at a time. Before each deletion, move any valuable assertions
from direct helper tests to the current public workflow. Require repository-wide zero references,
Release tests, publish, and the relevant manual restore scenario. Do not interpret a test-only use
as automatic proof that behavior is obsolete.

### C06 — Consolidate the duplicated Save Workspace workflow

**Evidence:** `App.xaml.cs:213-286` and `UI/SettingsWindow.xaml.cs:755-811` independently perform the
same sequence: enumerate preview windows, show `SaveWorkspaceDialog`, read capture options, create
and update `SaveProgressWindow`, call `CaptureWorkspaceAsync`, persist the result, handle failure,
and close progress UI.

**Why unnecessary:** Two orchestration copies can drift on browser capture, selection semantics,
progress stages, error behavior, or future checkpoint/capture policy.

**Impact of consolidation:** One tested save contract for both tray and Settings entry points and
less UI code-behind.

**Risk:** Medium. The current callers intentionally differ in owner assignment, Settings-window
enable/refresh behavior, and tray notifications.

**Plan:** Introduce a small UI workflow/coordinator returning a typed outcome. Pass owner and
presentation callbacks/options explicitly. Keep persistence in the existing capture/persistence
boundary. Add workflow tests for cancel, empty selection, partial browser capture, success, and
failure before replacing either caller.

### C07 — Consolidate restore/switch notification wrappers

**Evidence:** `LayoutCoordinator.cs:113-216` contains three structurally similar restore variants
that log a request, notify the user, invoke the same execution boundary, and classify cancellation,
checkpoint failure, success, or failure. `ReportSwitchProgress` at `LayoutCoordinator.cs:485` has
adjacent `ShouldNotifyUser` branches for log and balloon handling.

**Why unnecessary:** Repeated outcome mapping makes notification text and failure handling easy to
diverge while the underlying operation is shared.

**Impact of consolidation:** Fewer policy branches and consistent user-facing outcomes.

**Risk:** Medium. Restore, selective restore, align/minimize, switch, and undo must retain distinct
labels, triggers, progress ownership, and checkpoint semantics.

**Plan:** Extract private typed presentation helpers, not a second execution engine. Snapshot the
messages/outcome matrix in tests. Consolidate the two `ShouldNotifyUser` blocks without changing
when notifications are emitted.

### C08 — Decompose `WorkspaceService` behind its existing façade

**Evidence:** `Services/WorkspaceService.cs` is about 2,365 lines. It currently handles storage
forwarding, capture construction, browser/PWA identity enrichment, file discovery, restore-resource
observation, topology mapping, transaction/checkpoint coordination, compatibility restore methods,
undo support, and launch-specification construction.

**Why overly complex:** These responsibilities have different reasons to change and different
side-effect constraints. The class is difficult to review for ordering mistakes and encourages
callers to bypass narrower boundaries.

**Impact of simplification:** Smaller units with explicit side effects, faster review, and tests that
can target capture, observation, and transaction behavior separately.

**Risk:** High. This class connects nearly every current reliability feature; a large rewrite could
break browser capture, matching observations, checkpoints, or UI-thread dispatch.

**Plan:** Use additive extraction, in this order:

1. `WorkspaceCaptureBuilder`: snapshot and entry construction, selection, file/Jump List discovery,
   browser/PWA enrichment; returns the existing `WorkspaceCapture` model.
2. `RestoreObservationBuilder`: one timestamped immutable set of live windows, running apps,
   monitors, browser capability, and launch resources for plan construction.
3. `RestoreTransactionCoordinator`: single gate, durable checkpoint, undo metadata, and failure
   mapping.
4. `LaunchSpecificationFactory` only if launch construction remains after C05.

Keep `WorkspaceService` as a forwarding façade while callers migrate. Compare generated snapshots
and restore plans before and after every extraction. Delete the façade only when no caller needs it.

### C09 — Split planner and executor by pure policy and execution phase

**Evidence:** `RestorePlanner.cs` (~1,391 lines) is pure but combines candidate assignment, match
confidence, resource/launch decisions, topology adaptation, placement math, and global action
construction. `RestoreExecutor.cs` (~1,309 lines) combines preflight, browser execution, process
launch, readiness, mutation, verification, retry, and result aggregation.

**Why overly complex:** The pure/impure top-level separation is sound, but each side now has enough
independent policy to hide regressions in large methods.

**Impact of simplification:** More focused tests and clearer enforcement of action ordering and HWND
ownership.

**Risk:** High. Reordering even apparently independent phases can cause duplicate launches, stale
HWND mutation, unnecessary waits, or lost verification.

**Plan:** Preserve `RestorePlan`, action/result types, and the top-level public entry points. Extract
internal pure planner policies first (assignment, launch/resource, placement) with golden-plan
tests. Then extract executor phase objects sharing one explicit session context. The required order
is preflight → browser/launch → correlated readiness → mutation → re-observation/verification →
bounded retry → aggregation. Do not parallelize mutation or browser-group reconstruction without a
separate correctness design.

### C10 — Reduce Settings and application composition code-behind

**Evidence:** `UI/SettingsWindow.xaml.cs` is about 1,230 lines and also contains the programmatic
`WorkspaceWindowsDialog` beginning at line 1020. `App.xaml.cs` contains application startup,
composition, tray menu construction, save/startup flows, hotkey binding, error handling, and the
`DelayedSubmenuMenuItem` custom control beginning at line 598.

**Why overly complex:** UI state, reusable workflow policy, and component definitions share files,
making visual behavior harder to isolate and review.

**Impact of simplification:** Clearer ownership and smaller change surfaces for future Settings and
tray work.

**Risk:** Medium to high. Programmatic WPF event wiring, owner/activation, focus, keyboard navigation,
theme resources, and delayed submenu behavior are easy to alter accidentally.

**Plan:** First move `WorkspaceWindowsDialog` and `DelayedSubmenuMenuItem` to their own files with no
logic change. Then extract Settings section controllers/view models incrementally. Keep XAML names,
event handlers, automation/focus behavior, and owner assignment stable. Validate at 100%, 125%, and
150% scaling and with keyboard-only navigation.

### C11 — Centralize stable identity normalization, not domain policy

**Evidence:** process-name normalization is independently implemented in `AppReadiness.cs:125`,
`RestoreExecutor.cs:1205`, `RestorePlanner.cs:1342`, `WindowIdentity.cs:286`, and
`WindowService.cs:541`. Browser-family knowledge also appears in `WebAppService`,
`WorkspaceService`, `RestorePlanner`, and `BrowserIntegrationService`.

**Why duplicate:** Small differences in extension removal, casing, aliases, or browser membership
can make planning, readiness, execution, and matching disagree about the same application.

**Impact of consolidation:** A single canonical representation and fewer cross-phase mismatches.

**Risk:** High if over-centralized. “Supported connector browser,” “browser-like process,” “same
identity,” and “eligible launch target” are different policies and must not be collapsed into one
boolean.

**Plan:** Extract only a pure `ProcessIdentityNormalizer` with a table-driven test corpus. Define
separate named browser catalogs/capabilities for connector support, matching, readiness, and launch.
Migrate one consumer at a time and compare plans/results. Do not add application-specific exceptions.

### C12 — Keep defensive re-observation; optimize only immutable same-phase snapshots

**Evidence:** live-window inventories are read during plan observation, executor preflight,
readiness, mutation/revalidation, and placement verification. Monitor and running-application
inventories are also gathered for capture and planning.

**Why it can look redundant:** Several native enumerations occur during one user operation.

**Why most calls are necessary:** Each phase answers a different time-sensitive question. Reusing an
old HWND or monitor snapshot across a wait would weaken stale-plan detection and same-window safety.

**Impact of safe consolidation:** A timestamped, immutable observation could avoid duplicate window
and process enumeration within plan creation itself. Cross-phase caching should not be introduced.

**Risk:** High. Stale caching can move the wrong window or wait for a process that no longer owns an
eligible task window.

**Plan:** Instrument phase duration and enumeration counts first. If a single planner phase performs
identical observations, construct one `RestoreObservation` and pass it to pure planning. Always
re-observe before mutation and during verification. Add HWND reuse/disappearance and topology-change
tests before any optimization.

### C13 — Make disposable and cancellation ownership explicit

**Evidence:** analyzer ownership diagnostics identify `_displayChangeCts` in `LayoutCoordinator`,
`_singleFlight` in `WorkspaceSwitchEngine`, and `_restoreTransactionGate` in `WorkspaceService`.
`App` does dispose `HotkeyService` and the tray icon on explicit exit/fatal paths, so that ownership
must be preserved. Synchronous `CancellationTokenSource.Cancel()` is also called inside asynchronous
switch paths (`LayoutCoordinator.cs:310`, `WorkspaceSwitchEngine.cs:87`).

**Why technical debt:** Long-lived token sources and semaphores have implicit application lifetime,
and synchronous cancellation callbacks can block an async/UI path.

**Impact of cleanup:** Deterministic shutdown and clearer single-flight cancellation behavior.

**Risk:** Medium to high. Disposing a semaphore or token source while work is active causes races;
changing cancellation ordering can allow two switches to overlap.

**Plan:** Document owners first. Add shutdown-while-idle, shutdown-during-restore, and superseded-switch
tests. Implement `IDisposable`/`IAsyncDisposable` only at composition-owned boundaries, cancel and
await active work before disposing primitives, and consider `CancelAsync` outside locks while
preserving the existing single-flight ordering.

### C14 — Reuse native-messaging standard streams

**Evidence:** `NativeMessagingHost.Run` repeatedly calls `Console.OpenStandardInput()` inside its
message loop, while `WriteMessage` repeatedly calls `Console.OpenStandardOutput()`.

**Why unnecessary:** A native-messaging host has one process-lifetime framed stdin/stdout channel;
reopening wrapper streams per message adds allocations and obscures stream ownership.

**Impact of cleanup:** Simpler protocol lifecycle and less per-message churn.

**Risk:** Medium. Closing or buffering stdout incorrectly can break framing and disconnect the
browser connector.

**Plan:** Pass process-owned input/output streams into the loop and message writer, leave them open
until host exit, flush every framed response, and add multi-message, truncated-frame, disconnect,
and concurrent-response tests. Do not write logs to stdout.

### C15 — Migrate the last unstructured logging calls

**Evidence:** the logger retains message-only overloads, and current production callers include
`App.xaml.cs:431`, `HotkeyService.cs:60,109,241`, and `WorkspaceService.cs:505`. Most of the
application already uses event IDs and classified `LogField` values.

**Why legacy:** Free-form logs are harder to query and easier to make privacy-unsafe. The export
redactor has to treat legacy content conservatively.

**Impact of cleanup:** Uniform event taxonomy and stronger guarantees that paths, titles, URLs, and
identifiers cannot leak through new messages.

**Risk:** Medium. Event IDs may be relied on for diagnosis, while over-redaction can make restore
failures impossible to understand.

**Plan:** Convert remaining message-only sites to stable event IDs and explicitly classified fields,
extend redaction tests with sensitive examples, then remove the legacy overloads. Preserve local log
rotation/export behavior and never log HWND/title/path/URL as public data.

### C16 — Adopt a focused analyzer baseline

**Evidence:** latest/all analysis produced 864 diagnostics. Largest categories included internal
visibility/API naming, broad catch clauses, culture-aware string overloads, P/Invoke declarations,
collection API shape, and async-context advice. High-signal individual findings include ignored
native return values, disposable ownership, synchronous cancellation, and native stream allocation.

**Why technical debt:** With analyzers effectively all-or-nothing, actionable regressions are buried
in known noise.

**Impact of cleanup:** New issues become visible in pull requests without forcing risky cosmetic
changes.

**Risk:** Low if baselined; high if all diagnostics are mass-fixed. Public-looking serialized models,
COM/P/Invoke signatures, and test names have compatibility/readability constraints.

**Plan:** Add `.editorconfig` severity only for selected correctness, disposal, interop, and async
rules. Suppress documented native/serialization exceptions locally. Ratchet down a checked-in
baseline; do not reformat or rename all tests/DTOs in one cleanup.

### C17 — Review ignored native/COM results individually

**Evidence:** analyzer findings include ignored results from `PropVariantClear`,
`GetApplicationUserModelId`, `GetClassName`, `GetWindowText`, and
`GetWindowThreadProcessId`. It also reports the COM-release null check in `WebAppService.cs:313` as
always true.

**Why technical debt:** Some ignored results are intentional probe patterns; others may erase useful
failure distinctions. Blindly satisfying the analyzer can make COM cleanup less safe.

**Impact of cleanup:** Explicit native failure semantics and more useful diagnostics.

**Risk:** Medium to high. Win32 often uses a zero result for both empty data and error, and COM
objects require release even through exceptional construction/use paths.

**Plan:** Document accepted return codes at each call, use `Marshal.GetLastWin32Error` only where the
API contract supports it, add native-boundary unit tests where abstraction permits, and keep a
defensive COM release guard unless control-flow proof covers constructor failure. Suppress a false
positive rather than weakening cleanup.

### C18 — Keep browser calls unless measurement proves waste

**Evidence:** the connector calls `chrome.windows.getAll({ populate: true })`, then
`chrome.tabGroups.query` per browser window to capture group metadata. Restore creates windows,
updates tab attributes, reconstructs groups, updates group properties, and finally restores window
state. There are no database or remote HTTP calls in this flow.

**Why retained:** The Chrome APIs expose window/tab and group metadata through separate calls, and
restore order affects tab indexes, grouping, active state, and final window state.

**Impact of removal:** Removing or parallelizing these calls could lose user-visible session data.

**Risk:** High.

**Plan:** Keep the sequence. If performance becomes a concern, add connector timing and fixture-based
round-trip tests before changing call shape. Document whether the manifest’s `0.1.0` version is an
independent connector version or should follow desktop releases; do not synchronize it accidentally.

## Dead-code and disconnected-file conclusions

### Confirmed or high-confidence candidates

- The dependency/property/archive candidates in C01-C04.
- The declaration-only and test-only compatibility members in C05, subject to the stated gates.
- Legacy message-only logger overloads after their remaining callers migrate.

### Not dead and must be retained

- All currently defined WPF dialogs/windows: `SaveWorkspaceDialog`, `SaveProgressWindow`,
  `SelectiveRestoreDialog`, `StartupWorkspaceDialog`, `RestorePlanPreviewDialog`,
  `RestoreProgressWindow`, `SettingsWindow`, and `WorkspaceWindowsDialog` each have a construction
  path. `DelayedSubmenuMenuItem` is referenced by `App.xaml`.
- `H.NotifyIcon.Wpf`, `OpenMcdf`, and `WPF-UI` have direct production uses.
- Root `index.html`, `privacy-policy.html`, and `privacy-policy.md` serve GitHub Pages/store privacy
  and review needs.
- Workspace/settings/checkpoint schema models, migrations, stable IDs, and legacy import.
- Native structs, COM declarations, P/Invoke wrappers, and JSON-populated properties even when text
  reference counts are small.
- Planner/executor abstractions and repository interfaces that provide test seams and side-effect
  boundaries.
- Re-observation and verification calls at different restore phases.
- Browser group/session API calls described in C18.

## Proposed cleanup sequence

Every phase should be a reviewable commit and must leave the suite and packaged app usable. Avoid a
single “big cleanup” commit.

### Finding-to-phase coverage

Every audit finding is assigned below. A split assignment means the safe mechanical portion happens
first while higher-risk redesign remains in its appropriate later phase. Coverage is **18/18**;
there are no unassigned cleanup findings.

| Finding | Assigned phase | Status / boundary |
|---|---|---|
| C01 unused Hosting dependency | Phase 1 | Complete |
| C02 unused WinForms switch | Phase 1 | Complete |
| C03 stale release archives | Phase 1 | Complete |
| C04 abandoned documentation assets/ignore policy | Phase 1 | Complete |
| C05 obsolete service façades | Phase 2 | Complete |
| C06 duplicated Save Workspace workflow | Phase 3 | Complete |
| C07 restore/switch notification duplication | Phase 3 | Complete |
| C08 `WorkspaceService` decomposition | Phase 4 + Phase 6 | Complete; capture policy extracted and superseded restore/capture compatibility projections removed |
| C09 planner/executor phase decomposition | Phase 4 + Phase 6 | Complete; planner policies and ordered executor phases share explicit session state |
| C10 UI/composition code-behind | Phase 3 + Phase 4 + Phase 6 | Implementation complete; controls, rows, workflows, and Settings feature partials separated; manual DPI/input gate pending |
| C11 identity normalization | Phase 3 | Canonical normalizer complete; browser policies intentionally remain separate |
| C12 observation measurement/caching decision | Phase 5 | Complete; timing/count metrics added, no cross-phase cache introduced |
| C13 disposable/cancellation ownership | Phase 5 | Complete; composition-owned cancellation and async disposal added |
| C14 native-messaging stream reuse | Phase 5 | Complete; process-lifetime streams and framing tests added |
| C15 unstructured logging migration | Phase 2 | Complete |
| C16 focused analyzer baseline | Phase 5 | Complete; selected lifecycle/interop rules are checked in |
| C17 native/COM result review | Phase 5 | Complete; probe and cleanup results are explicit |
| C18 browser call/connector version review | Phase 5 + ongoing | Complete for this phase; calls retained, timings added, connector version remains independent |

### Phase 0 — Freeze the baseline

1. Record the 177-test baseline, packaged executable size, cold startup time, capture time, preview
   time, and restore phase timings.
2. Add missing characterization tests for the C05 wrappers before deciding whether their behavior
   is already represented elsewhere.
3. Save representative v1/v2/v3/v4 workspace and v1/v2/v3 settings fixtures plus corrupted/newer
   schema samples.
4. Record a manual smoke matrix for single/multi-monitor and missing-monitor operation.

**Gate:** no production behavior change.

### Phase 1 — Repository and dependency hygiene

1. C01 unused Hosting package.
2. C02 WinForms project switch.
3. C03 stale release archives.
4. C04 documentation assets and ignore policy.

**Gate:** restore/build/177 tests, self-contained single-file publish, application launch, tray,
Settings, and browser package creation.

### Phase 2 — Dead compatibility surface

Remove C05 members in small groups after moving useful tests to the live planner/coordinator paths.
Migrate C15 logging sites and remove legacy overloads separately.

**Gate:** repository-wide zero callers, existing tests, new route-level characterization, and the
specific manual behavior represented by each removed method.

### Phase 3 — Low-risk consolidation

Consolidate C06 save orchestration and C07 result presentation. Move standalone UI classes to their
own files without redesigning the interface. Introduce the normalizer from C11 only after its corpus
test is complete.

**Gate:** identical snapshots/plans/results for fixed fixtures plus keyboard/focus/progress smoke
tests from both tray and Settings entry points.

### Phase 4 — Service decomposition

Perform C08 and C09 as additive extractions, and complete the Settings section decomposition from
C10. No public model/schema changes and no phase reordering. Keep forwarding façades until all
callers and tests use the extracted boundaries.

**Gate:** golden capture/plan comparisons, transaction/failure injection, cancellation races,
session-wide HWND multiplicity, readiness correlation, placement verification, switch-close
tracking, and manual multi-monitor restore.

### Phase 5 — Lifecycle and interop hardening (complete)

Applied C13, C14, C16, and C17. C12 now records observation duration and bounded counts without
cross-phase caching. C18 records connector timings and treats the browser manifest version (`0.1.0`)
as an independent connector protocol version; browser calls and ordering remain unchanged.

**Gate:** shutdown/supersession stress tests, browser native-messaging round trips, no protocol data
on stdout, no unobserved task failures, and no regression in phase timings.

### Phase 6 — Responsibility-based core decomposition (implementation complete)

6.0 freezes full capture projections, generated identity invariants, browser ordering, cancellation,
bounded search, a complete plan fingerprint, executor phase traces, and transaction races. Existing
planner/executor cases retain the broader topology, ambiguity, packaged-app, browser fallback,
selective/align, readiness, stale-plan, and retry matrix.

6.1 moves monitor/window selection and assembly to `WorkspaceSnapshotBuilder`, entry variants to
`CapturedWindowEntryFactory`, and title/Jump List/common-folder resource policy to
`CaptureResourceResolver`. Browser enrichment still follows native construction and persistence
remains a separate explicit call.

6.2 separates identity models, extraction/normalization, the stable public matcher façade, and the
internal scoring/ambiguity/learned-hint resolver. 6.3 separates session-wide HWND assignment,
approval projection, launch/resource decisions, and placement geometry while retaining
`RestorePlanner` as the deterministic public composer.

6.4 invokes preflight, browser/launch, correlated readiness, placement verification, and result
aggregation through ordered internal phase objects sharing one execution context and HWND set. 6.5
keeps one XAML visual tree while splitting Settings code-behind into startup, hotkey, monitor, and
workspace-list partials. 6.6 migrates compatibility tests to current boundaries, removes only the
now-unreferenced adapters/forwarders, and updates the contributor architecture graph.

**Gate:** 203 Release tests, 26/26 Settings handler resolution, analyzer build with 0 warnings/errors,
and the self-contained single-file win-x64 artifact recorded in `docs/cleanup-phase-6.md`. Manual
native/UI, multi-DPI, and browser verification remains pending.

## Required automated regression matrix

The current suite is a strong start. Cleanup changes should continue to cover, or add coverage for:

| Area | Required invariants |
|---|---|
| Persistence | Atomic replace; store separation; corruption isolation; no overwrite on failure; unsupported-future-schema rejection; idempotent migrations; stable IDs. |
| Capture | Selection; exclusion defaults; multiplicity; Explorer/file/Jump List/PWA data; browser unavailable/partial/success states; metadata-only checkpoint capture. |
| Planning | Pure/deterministic output; confidence/evidence; ambiguity and remembered hints; one HWND per entry; missing resources; topology fallback; explicit action ordering. |
| Approval/preflight | Unapproved actions never execute; expected close does not falsely stale a switch; unrelated HWND reuse/disappearance does stale or skip safely. |
| Readiness | Wait only after related successful activity; already-present eligible windows; background-only processes; timeout uses real wall clock; cancellation is prompt. |
| Execution | Browser fallback; Store/PWA rebinding; no duplicate launch; same assigned HWND through readiness/mutation/retry; per-entry failure isolation. |
| Placement | Normalized/semantic/DPI fallback; visible clamping; maximized/minimized state; tolerance; app-moved/rejected/window-gone outcomes; bounded retry. |
| Transaction/undo | Checkpoint before first mutation; checkpoint failure blocks all changes; retention/expiry; undo and undo-of-undo; concurrent-operation gate. |
| Switching | Destination preservation; exact unrelated-handle close tracking; no force-close; bounded wait; single-flight supersession; cancellation and stale preview behavior. |
| Privacy/logging | Stable event IDs; classification required; title/path/URL/identifier redaction; rotation/export; no native protocol contamination. |
| UI workflow | Both save entry points; preview radio/checkbox/keyboard behavior; progress stages; cancel; window ownership; tray/settings refresh and notifications. |

## Required manual smoke matrix

Before releasing cleanup work, test the published executable—not only the framework-dependent build:

1. Fresh start, existing-settings start, autostart, default/last/picker startup restore, and exit.
2. Save from tray and Settings with all windows, a subset, browser capture enabled/disabled, and
   connector missing.
3. Restore and switch between single-monitor and multi-monitor workspaces, including a disconnected
   saved monitor and changed DPI/orientation/work area.
4. Ambiguous File Explorer/browser matching using mouse and keyboard; remember and clear a choice.
5. Existing background-only process, already-open destination window, multiple same-process windows,
   slow launch, launch failure, readiness timeout, app-rejected placement, and closed HWND.
6. Cancel during checkpoint, resource discovery, readiness, close-wait, and verification.
7. Checkpoint failure and Undo Last Restore, including a second undo.
8. Browser tabs, pinned/active state, groups, browser window bounds/state, and fallback launch.
9. Hotkeys, workspace order/slots, monitor aliases, tray submenu timing, Settings dialogs, toast
   throttling, and 100%/125%/150% display scaling.
10. Export logs containing deliberately sensitive test paths, titles, URLs, and identifiers and
    verify they are redacted.

## Explicitly rejected cleanup shortcuts

- Do not remove migrations because all current files serialize at the newest schema. Users may
  upgrade directly from an old version.
- Do not use application-name blacklists to simplify native task-window filtering.
- Do not cache HWND or monitor observations across waits or mutation boundaries.
- Do not merge matching confidence, readiness, launch support, and browser connector support into
  one application-type switch.
- Do not parallelize window mutation or browser group restoration without a separate ordering and
  ownership proof.
- Do not replace all interfaces with concrete types in response to analyzer performance hints; the
  boundaries are valuable for pure tests and failure injection.
- Do not bulk-fix analyzer warnings in serialized DTOs, COM/P/Invoke signatures, or test names.
- Do not rewrite published Git history or move the `v1.5.1` tag to remove an old archive.
- Do not combine repository hygiene, dead-code removal, orchestration changes, and service
  decomposition into one commit.

## Completion criteria for the cleanup program

The cleanup is complete only when:

- each removed dependency, file, method, and abstraction has recorded evidence and a passing gate;
- all Release tests pass and new characterization covers the active replacement paths;
- the self-contained single-file executable and connector/checksums build successfully;
- saved data from supported prior schemas still loads and is migrated atomically;
- fixed capture inputs produce equivalent snapshots and fixed observations produce equivalent plans;
- execution preserves checkpoint-before-mutation, phase order, single-HWND ownership, cancellation,
  readiness, verification, and switch-close guarantees;
- the full manual matrix passes on the published executable;
- performance is equal or better by recorded phase metrics; and
- release notes describe structural cleanup without claiming removed user functionality.
