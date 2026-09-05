# WindowAnchor — Architecture

This document describes the internal architecture of WindowAnchor v1.5.1 for contributors and maintainers.

---

## Principle: One Responsibility Per Layer

```
┌─────────────────────────────────────────────────────────────────┐
│  UI Layer         App.xaml.cs · SettingsWindow · Dialogs        │
│  (WPF, tray)      Owns no business logic. Calls Coordinator.    │
├─────────────────────────────────────────────────────────────────┤
│  Coordinator      LayoutCoordinator · Preview Workflow          │
│                   Selects manual/automatic restore entry points │
│                   and owns user-facing restore notifications.   │
├─────────────────────────────────────────────────────────────────┤
│  Services         WorkspaceService · RestorePlanner/Executor    │
│                   WindowService · Monitor/Storage services      │
│                   Pure decisions plus explicit I/O boundaries.  │
├─────────────────────────────────────────────────────────────────┤
│  Models           WorkspaceSnapshot · WorkspaceEntry            │
│                   RestorePlan · execution/preview results        │
│                   Immutable intent and plain persisted data.     │
├─────────────────────────────────────────────────────────────────┤
│  Native           NativeMethods.Window · NativeMethods.Display  │
│                   All P/Invoke declarations. No logic.          │
└─────────────────────────────────────────────────────────────────┘
```

---

## Service Responsibilities

### `MonitorService`
Owns everything related to physical displays.

- **`GetCurrentMonitorFingerprint()`** — calls `QueryDisplayConfig` to enumerate active display paths, extracts EDID manufacturer ID + product code + connector instance for each display, sorts them, joins them, and returns the first 8 hex characters of a SHA-256 hash. This hash is stable: it does not change when resolution or refresh rate changes, only when the set of physical monitors changes.
- **`GetCurrentMonitors()`** — returns a `List<MonitorInfo>` where each entry has a stable `MonitorId` (same EDID-based format as the fingerprint), a friendly name from `DisplayConfigGetDeviceInfo`, full virtual-desktop bounds and taskbar-aware work area from `EnumDisplayMonitors`, effective DPI, and a primary flag.
- **`GetMonitorForWindow(hWnd, monitors)`** — static helper, calls `MonitorFromWindow` to map a live HWND to a monitor in the supplied list.

### `WindowInventory` and window policies
`WindowInventory` calls `EnumWindows` and returns policy-free `ObservedWindow` facts: HWND, PID,
owner, visibility, class, title, bounds, executable/process identity, and AUMID. Enumeration does
not decide whether a window is safe to capture or mutate.

`WindowPolicyEvaluator` applies one explicit named policy at each consumer boundary:

- `CaptureCandidate` preserves the legacy save exclusions for invisible, owned, shell-chrome,
  empty-title, and tiny utility windows.
- `RestoreMatchCandidate` selects the same layout-shaped windows for matching.
- `SwitchCloseCandidate` and `MinimizeCandidate` preserve the legacy layout exclusions and also
  reject WindowAnchor's own process.
- `SwitchRiskCandidate` is deliberately broader: safe-switch preflight can inspect owned dialogs
  and small/untitled transient windows without allowing those windows into a saved workspace.

The policy layer contains no third-party application, process, or title blacklist; only known
Windows shell-chrome classes remain platform exclusions. `WindowInventory` observes extended
styles, DWM cloaking, ownership, root ownership, and the current visible owner-chain
representative. Capture, restore matching, close, and minimize then select visible,
uncloaked, independently manageable task windows: `WS_EX_TOOLWINDOW` is excluded,
`WS_EX_NOACTIVATE` is excluded unless `WS_EX_APPWINDOW` explicitly opts the window into taskbar
behavior, and ordinary owned/transient windows are excluded. The representative is diagnostic
evidence rather than a hard gate because Microsoft documents the exact Alt+Tab algorithm as an
implementation detail; an independent root remains captured while a temporary modal popup is
open. `SwitchRiskCandidate` remains broader so ordinary owned dialogs still protect unsaved work.

The process inventory is independent of the filtered window inventory. Therefore a legacy entry
whose process is already running but exposes no eligible, unassigned user-facing window is shown
as explained and excluded rather than relaunched or allowed to consume a readiness timeout. Window
multiplicity is preserved at capture—there is no product-specific deduplication—and the restore
session's one-HWND-per-entry invariant handles assignment safely. Hosted windows may cross process
boundaries only when they expose the same AppUserModelID within the same package family; matching
never uses a title-only host exception.

### `WindowService`
Applies explicit policies to raw inventory and owns live-window enrichment/mutation.

- **`SnapshotWindows(policy, monitors?)`** — filters raw observations with the named policy,
  enriches selected windows with placement/browser/folder data, and optionally assigns monitors.
- **`GetWindowsWithPids(policy)`** — returns a policy-selected
  `HWND → (PID, WindowRecord)` dictionary for restore matching.
- **`InspectUserWindows(SwitchRiskCandidate)`** — supplies raw safe-switch preflight risks,
  including owned save dialogs that capture excludes.
- **`RequestCloseUserWindowsExcept(SwitchCloseCandidate, keep)`** posts `WM_CLOSE` only to
  unrelated layout candidates and returns the exact HWND set that the switch engine must track.
  Broad `SwitchRiskCandidate` observations remain preflight diagnostics and never control liveness.
- **`MinimizeUserWindowsExcept(MinimizeCandidate, keep)`** keeps mutation policy explicit.
- **`RestoreWindow(hWnd, record)`** — calls `SetWindowPlacement` then, for maximised windows, a second `ShowWindow(SW_MAXIMIZE)` pass to ensure the maximised state is applied on the correct monitor.
- **`IsWindowAlive(hWnd)`** — performs an unfiltered native liveness check before a restore
  session releases an assignment whose HWND disappeared from the user-window inventory.

### Window identity and matching

`WindowIdentityExtractor` is the single translation boundary from persisted `WorkspaceEntry`
and runtime `WindowRecord` data into `SavedWindowIdentity` and `LiveWindowIdentity`. Saved
identity contains stable evidence only; HWND and PID exist exclusively on the live model.

`WindowMatcher` is pure and returns every live window as a scored `WindowMatchCandidate` with
machine-readable `WindowMatchEvidence`. Matching proceeds from exact stable identities (PWA or
packaged-app AUMID and dedicated-browser site), through document/project evidence, generic
executable/class/title signals, and finally weak monitor/geometry context. Results are ordered by
eligibility, descending score, and ascending HWND, so equal candidates remain visible as ties while
presentation stays deterministic.

`WindowMatchPolicy` owns the minimum title/geometry/score thresholds, strong-confidence boundary,
top-vs-runner-up ambiguity margin, and learned-hint bonus. `WindowMatcher.Resolve` returns
`Exact`, `Strong`, `Probable`, `Ambiguous`, or `Missing`; ambiguous results deliberately contain no
selected candidate. Compatibility and post-launch reconciliation use the same resolver, so neither
can fall back to HWND ordering.

Learned choices are keyed by stable workspace and entry GUIDs in settings schema v3. Their
`WindowIdentityHint` combines executable/class or stronger AUMID, package, PWA, browser-site, or
folder evidence with secondary title tokens. HWND/PID never enter the persisted model, and a title
cannot be saved as the sole identity. A matching hint contributes explicit evidence and score; it
does not bypass kind, executable, or minimum-eligibility checks.

`WindowRestorePlanner` is the compatibility adapter between scored candidates and the existing
restore session. It proposes the highest-ranked eligible candidate and carries its evidence into
structured restore diagnostics; `RestoreSessionContext` remains the only owner allowed to commit
an entry-to-HWND assignment.

### Pure restore planning

`RestorePlanner` builds an immutable `RestorePlan` from four explicit inputs: a saved
`WorkspaceSnapshot`, a purpose-built `RestoreLiveInventory`, an already-observed
`RestoreMonitorTopology`, and a `RestoreMode`. It performs no window, process, browser,
file-system, or persistence operations.

Every saved entry remains present as a `RestorePlanEntry`, including entries excluded by a
selective restore or stopped by cancellation. Each entry records scored candidates and their
identity evidence, the deterministic selected match, DPI-aware target placement, launch
requirements, future `RestoreAction` descriptions, warnings, blocking errors, and one explained
outcome. Candidate HWNDs are consumed across the whole build so two entries cannot claim the same
live window.

Missing and stale resources are supplied as observations and become blocked outcomes instead of
exceptions. Browser-session unavailability becomes an explicit warning with ordinary launch
fallbacks. `RestorePlan.Redact()` and `ToRedactedJson()` produce deep privacy-safe projections for
diagnostics or preview.

`PackagedAppResolver` treats a `WindowsApps` executable as a versioned observation, not a durable
launch identity. It derives the package family from the saved package full name, finds the
currently registered package, reads the matching application ID from `AppxManifest.xml`, and
supplies `PackageFamilyName!ApplicationId` as an available packaged-app resource. This lets an old
workspace activate the updated package through `shell:AppsFolder` even when its saved executable
directory has been replaced. Capture uses the same resolver when a packaged child process does not
expose an AUMID directly.

### Semantic and monitor-relative placement

Workspace schema v4 retains the legacy absolute normal rectangle and separate `ShowCmd`, while
adding source monitor bounds/work area/DPI plus `NormalizedWindowLayout`. Capture derives X/Y/W/H
relative to the work area, horizontal and vertical anchors, and recognizable full, left/right
half, top/bottom half, thirds, centered, or custom layouts.

`RestoreMonitorTopology.IsExactMatch` requires the same ordered stable IDs, virtual bounds, work
areas, and DPI. The pure planner preserves exact pixels only for that case. Any geometry, work-area,
DPI, orientation, or monitor-set change selects semantic/normalized adaptation on the mapped
monitor. Legacy snapshots without normalized data retain DPI scaling but pass through the same
work-area clamp, so adaptation cannot produce a fully off-screen rectangle. `ShowCmd` is applied
after normal bounds and remains independent of maximized/minimized state.

### Restore plan execution and staleness

`RestoreExecutor` is the only boundary that turns an approved `RestorePlan` into process, browser,
or window mutations. It executes only actions already present in that plan through injectable
process-launch, browser-session, window-inventory/mutation, resource-validation, readiness-probe,
and clock boundaries. Browser restoration is itself an explicit plan action; its ordinary-browser
fallback is also explicit and conditional on that action becoming unavailable.

Immediately before each placement, the executor enumerates eligible windows again and verifies the
approved HWND, PID, and saved identity. Immediately before each launch, it revalidates the approved
file, folder, URL, executable, or packaged-app identity. Closed/reused HWNDs and changed resources
produce structured `RestoreExecutionResult` stale-plan outcomes and receive no mutation. The
approved assignment remains session-wide. After an approved launch, the executor uses the
readiness engine below; no fixed restore sleep remains.

Candidate-set staleness is directional: a newly appearing eligible HWND was not reviewed and
invalidates the plan, while disappearance of an unselected candidate is safe. The latter is
required by workspace switching because its own approved close phase intentionally removes
unrelated candidates before the destination plan executes. A selected HWND disappearing or being
reused by another PID still invalidates the plan.

### Application readiness

`SystemAppReadinessProbe` captures one shared read-only observation per poll: eligible live
windows, running process names, and HWND responsiveness. It deliberately avoids
`WaitForInputIdle`, which is unreliable for UWP/MSIX and multi-process browser applications.

`AppReadinessEngine` combines those facts with the existing ambiguity-safe `WindowMatcher` and
per-entry stability memory. The generic strategy reports `NotStarted`, `ProcessStarted`,
`WindowFound`, `Ready`, `TimedOut`, or `Failed`. A window becomes generically ready only when it is
safely assignable, responsive, and has retained the same HWND/PID/title/class/bounds signature for
the configured number of observations.

`AppReadinessStrategyRegistry` selects the first injected `IAppReadinessStrategy` that claims the
saved identity, then falls back to `GenericAppReadinessStrategy`. An adapter may replace the
generic readiness rule but cannot select a different or ambiguous window: matching remains owned
by `WindowMatcher`, and `Ready` without a selected candidate is rejected as `Failed`.

The executor polls every pending entry from the shared observation at 250 ms intervals with a
45-second monotonic wall-clock timeout and cancellation on every wait. Before polling, each wait
must correlate to a successful launch for the same entry/stable application identity or to its
successful browser-session action; unrelated launches cannot start passive timeouts.
Probe/enumeration work is included in that budget rather than counting only requested delays. Entries are tracked independently: a ready
entry is revalidated and positioned immediately while slower entries keep polling. Timeout and
failure states are carried in structured action/entry results; privacy-safe diagnostic events
record the stable entry ID, strategy, and timeout without window titles or paths.

### Post-restore placement verification

After every initially matched or newly ready window has been positioned, `RestoreExecutor` runs a
bounded verification phase. `SystemWindowPlacementProbe` re-reads the exact assigned HWND's normal
bounds, `ShowCmd`, and DPI. `WindowPlacementVerifier` compares those facts with the immutable plan
target using a default eight-pixel tolerance scaled to target DPI, and classifies the observation
as `Applied`, `Settling`, `Rejected`, `MovedByApp`, or `WindowGone`.

All assigned windows share the wait phase, so verification adds one short settling interval rather
than one delay per window. A mismatch is revalidated against the original PID and saved identity,
then the same assigned HWND receives at most two correction attempts. An HWND that is closed,
reused, or no longer eligible is never replaced silently. Immediate post-mutation observations
distinguish a placement the app later moved from one it never accepted.

`IWindowPlacementVerificationStrategy` may override delay, tolerance, and retry count for a known
application family; matching and HWND ownership remain outside the adapter. Verification ends with
the restore invocation and never becomes a background layout guard, so later user movement is not
fought. Entry/action execution results carry final state, retry count, strategy, and tolerance;
failed verification changes the overall result to `CompletedWithFailures` and is summarized in the
user-facing restore warning and privacy-safe structured log.

### Restore preview and approval

`RestorePlanPreviewBuilder` projects the immutable plan into exact, adapted, ambiguous, missing,
ready, skipped, and cancelled entry states plus move, launch, browser, wait, minimize, and no-change
action labels. It never reads live state. `RestorePlanPreviewDialog` renders only this projection,
uses keyboard-focusable entry checkboxes and automation labels, and distinguishes blocking errors
from destructive minimize actions. In switch mode it also explains that approved destination HWNDs
will be preserved while unrelated windows receive normal close requests after approval.

For an ambiguous entry the projection also carries only the close candidate set, with title,
process/class, monitor, bounds, confidence, score, and explained evidence. Radio-button selection
calls `RestorePlanner.ResolveAmbiguousMatch`, which verifies session-wide HWND ownership and derives
a new plan without observing the desktop. Optional “Remember this choice” data is persisted only
after the approved execution completes successfully; Settings can clear every learned choice.

Unchecking an entry calls `RestorePlanner.DeriveApprovedPlan`. The derived plan retains the original
candidate evidence and placement but removes that entry's actions and blocking errors without
changing the preview object. Its selected HWND is protected from align/minimize. Disabling an
ordinary-browser entry also removes the global browser-session action and makes enabled browser
fallbacks explicit, preventing the connector from recreating a disabled entry.

Manual tray and Settings restores pass the derived plan to `ExecuteApprovedRestorePlanAsync`.
Before any mutation, the executor compares the current eligible candidate inventory with the
preview and revalidates every referenced resource. Changed state returns `StalePlan`, displays a
clear retry message, and is never silently replanned. Switch commands use the same preview before
entering the close phase. Startup and display-change restores retain their one-click compatibility
path.

### Single-flight workspace switching

`WorkspaceSwitchEngine` owns switch cancellation and serialization. A new approved switch cancels
the previous invocation and waits for its lease, preventing overlapping poll/notification loops.
The engine preserves HWNDs referenced by approved `RestoreExistingWindow` actions, sends `WM_CLOSE`
to other close candidates, and polls only the returned requested-handle set with unfiltered HWND
liveness checks. The two-minute limit is measured by `Stopwatch` wall time. Count changes remain in
structured logs, while user waiting notifications are rate-limited to at most one per 30 seconds.
When the requested set clears, the exact reviewed plan executes; owned/transient risk windows never
hold the switch open merely because they were visible to preflight.

`LayoutCoordinator` wraps the entire switch—including the close phase—in the shared restore
transaction gate. The current desktop checkpoint is durable before `WorkspaceSwitchEngine` can
send its first `WM_CLOSE`. A superseding switch cancels the older transaction token whether the
older request is still capturing or is already waiting for windows to close.

Manual restore/switch workflows display `RestoreProgressReport` in a non-modal, cancellable
progress window. Checkpoint capture, browser work, launches, readiness, close polling, and placement
verification report distinct stages; the service/executor remain UI-independent through
`IProgress<RestoreProgressReport>`. A UI-side timer advances the elapsed display between reports,
so a long synchronous observation or resource read remains visibly timed instead of appearing
frozen at `0:00`.

### Transactional checkpoint and undo

Every executable user-facing restore plan enters `WorkspaceService`'s single transaction gate.
`CaptureWorkspaceAsync` records the current windows, placements/states, launch-resource metadata,
monitor topology, and optional browser-session metadata; it never captures screenshots or document
contents. `CheckpointRepository.Save` is then the durability boundary. The approved executor (or
switch close phase) is not called unless the checkpoint payload was atomically committed.

Checkpoint capture uses a fast recovery profile: title parsing, Explorer folder paths, PWA
identity, and browser metadata remain available, while all Jump List parsing and the recursive
Documents/Desktop/Downloads/OneDrive fallback are skipped. Named workspace saves retain
comprehensive Jump List discovery, but all Tier-3 folder traversals share one five-second
cumulative budget and honor cancellation. This prevents checkpoint creation from making a
reviewed HWND plan stale while it spends tens of seconds scanning unrelated or locked files.

`RestoreCheckpointOutcome` is attached to the structured execution result. A persistence failure
produces a rejected result and zero native/process/browser mutation; cancellation before commit
produces a cancelled result and zero mutation. A later executor failure leaves the committed
checkpoint available.

“Undo Last Restore” selects the newest healthy, non-expired checkpoint and sends it through the
same observation, `RestorePlanner`, staleness, readiness, placement, and verification path as a
named workspace. Undo uses the `Undo` trigger, so it commits the state being replaced before it
restores the older state. This makes undo-of-undo possible without a separate restore engine.

### `WorkspaceService`
The main capture/restore orchestration service. Called by `LayoutCoordinator` and directly by UI
code. Snapshot construction and persistence are deliberately separate boundaries.

- **`TakeSnapshot(name, saveFiles, monitorIds, progress)`** — the side-effect-free window capture:
    1. Get fingerprint and current monitors.
    2. Enumerate live windows.
    3. Filter to selected monitors (when `monitorIds` is not null).
    4. For each window: Tier 1 title parse → Tier 2 jump-list lookup → optional Tier 3 file search.
       Tier 3 uses one cumulative five-second budget for the complete named capture.
    5. Build and return a `WorkspaceSnapshot`; no repository is touched.
- **`CaptureWorkspaceAsync(...)`** — the reusable capture operation for named workspaces,
  checkpoints, and temporary captures. It first builds the window snapshot, then requests optional
  browser metadata, attaches any returned sessions, and returns one `WorkspaceCaptureResult`.
  Browser status is explicit: `Captured`, `Unavailable`, `TimedOut`, `Skipped`, or `Failed`.
- **`PersistCapture(result, destination, policy)`** — the sole final commit for a capture. The
  caller chooses the typed repository and explicitly allows a partial window-only save or requires
  complete browser capture. A capture is never persisted before browser capture finishes.
- **`RestoreWorkspaceAsync(snapshot, token)`** — the restore pipeline:
    1. Observe live windows, monitor topology, resources, and browser-session capability without
       mutation, then build an immutable `RestorePlan`.
    2. Serialize restore operations and atomically persist a complete pre-mutation checkpoint. If
       this fails, return a structured rejection without invoking the executor.
    3. Pass that plan to `RestoreExecutor`, which revalidates every external target immediately
       before executing its approved action.
    4. Poll launched entries for safe identity, responsiveness, and stable title/class/bounds;
       position each ready entry immediately while preserving one-to-one HWND assignment.
    5. Return structured per-action and per-entry outcomes; the legacy restore-session result remains
       an adapter for existing callers during migration.
- **`CreateRestorePlan(snapshot, mode)`** — performs the read-only observation and pure planning
  phases for preview or explicit approval workflows.
- **`ExecuteApprovedRestorePlanAsync(snapshot, plan, token)`** — transactionally checkpoints and
  executes an already-approved plan, reporting stale preconditions without silently replanning.
- **`UndoLastRestoreAsync(token)`** — loads the newest eligible checkpoint, builds a normal restore
  plan for it, and executes it with a new pre-undo safety checkpoint.
- **`RestoreWorkspaceSelectiveAsync(snapshot, monitorIds, token)`** — builds a selective plan that
  retains excluded entries as explicit results, then executes only included-entry actions.

### `StorageService`
Atomic, versioned JSON persistence and migration. Workspace schema v4 adds semantic layout data;
v2/v3 and legacy profile documents migrate without inventing unavailable geometry.
`StorageService` remains the application-facing
compatibility façade; typed repositories prevent permanent, recovery, and short-lived artifacts
from being mixed.

- **Storage paths** (under `%AppData%\WindowAnchor\`):
    - `workspaces/{workspaceId}.workspace.json` — permanent named workspaces.
    - `checkpoints/{workspaceId}.checkpoint.json` — recovery checkpoints.
    - `checkpoints/checkpoint-index.json` — versioned, reconstructable recovery metadata index.
    - `temporary-captures/{workspaceId}.temporary.json` — short-lived captures.
    - `settings.json` — versioned application settings with ID-based workspace references and
      composite learned window-match hints.
    - `last_fingerprint.txt` — persists the last-known fingerprint across restarts.
    - `.migrated_v2` — completion marker for the legacy monitor-profile import only.
- `NamedWorkspaceRepository`, `CheckpointRepository`, and `TemporaryCaptureRepository` enumerate
  only their own directory and extension. Checkpoint or expiry cleanup therefore cannot discover or
  delete permanent workspaces.
- `CheckpointRepository` stamps recovery schema version, stable checkpoint ID, trigger, UTC
  creation/expiry, source topology fingerprint, and target workspace ID. It keeps at most ten
  healthy checkpoints for seven days by default; newest healthy documents remain available when a
  corrupt peer is encountered. Retention and index maintenance run only inside the checkpoint
  directory.
- Documents are serialized and validated before an atomic writer creates a sibling temporary file,
  flushes it to disk, and replaces/moves it into place. Interrupted commits leave the previous final
  document intact; incomplete `.tmp` files are never enumerated as artifacts.
- Workspace display names are metadata. Stable `WorkspaceId` values are the filename/storage
  identity, so names that sanitize to the same filename can coexist.
- Workspace schema migrations run in order before deserialization. Current documents carry a stable `WorkspaceId`; every entry carries an `EntryId`.
- Settings migrations replace legacy workspace-name references with stable workspace IDs.
- Repository loads return healthy documents plus structured corruption, validation, unsupported-
  version, and I/O issues. One bad file does not prevent healthy documents from loading.
- Future schema versions and invalid documents are reported and left untouched.
- Legacy `profiles/*.profile.json` files are imported with deterministic IDs. The completion marker is written only when every legacy source succeeds.
- Legacy name-addressed workspace files are discovered only for migration. The ID-addressed copy is
  committed before the legacy source is removed.

### `LayoutCoordinator`
Reacts to `WM_DISPLAYCHANGE` events forwarded from `App.xaml.cs`.

- **`HandleDisplayChangeAsync()`** — debounces the event (1 s), computes the new fingerprint, looks
  up a matching workspace, and runs it with the `AutomaticDisplayRestore` checkpoint trigger.
- **`UndoLastRestoreAsync()`** — exposes recovery from the tray and reports checkpoint-gate or
  restore failures without treating them as success.
- Owns all notification balloon calls via the private `NotifyBalloon` helper, which marshals to the UI thread.

WindowAnchor's tray notifications are controlled by `AppSettings.NotificationsEnabled`. The final `App.ShowBalloon` display method checks this setting, so notifications initiated by startup, save, restore, switch, and display-change flows all follow the same preference. The setting only suppresses WindowAnchor messages; it does not change Windows notification or Focus Assist state.

### `JumpListService`
Reads the Windows Jump-List AutoDestList binary files from `%AppData%\Microsoft\Windows\Recent\AutomaticDestinations\` using the OpenMcdf library to extract recently-opened file paths per application.

### `TitleParser`
Stateless utility class. `ExtractFilePath(processName, titleSnippet)` applies a set of regular expressions to the window title to extract a file path and returns a `(path, confidence)` tuple.

---

## Data Flow: Save

```
User clicks "Save Workspace"
    → SaveWorkspaceDialog collects name + monitor selection
    → WorkspaceService.CaptureWorkspaceAsync(...)
        → TakeSnapshot(...) builds window entries without persistence
            → MonitorService.GetCurrentMonitors()
            → WindowService.SnapshotWindows(CaptureCandidate, monitors)
            → JumpListService.BuildSnapshotCache()
            → per window: TitleParser + JumpListService → WorkspaceEntry
        → IBrowserSessionConnector captures optional browser sessions
        → WorkspaceCaptureResult records browser outcome and complete snapshot
    → WorkspaceService.PersistCapture(result, NamedWorkspace, SavePartialWorkspace)
        → one atomic named-workspace commit
```

## Data Flow: Restore

```
Manual tray/Settings request
    → WorkspaceService.CreateRestorePlan(snapshot, mode)
        → observe inventory, resources, browser capability, and monitor topology
        → RestorePlanner.Build(...) returns immutable intent
    → RestorePlanPreviewDialog projects the plan and collects disabled entry IDs
    → RestorePlanner.DeriveApprovedPlan(original, disabled IDs)
    → WorkspaceService.ExecuteApprovedRestorePlanAsync(snapshot, approved plan)
        → CaptureWorkspaceAsync(current desktop)
        → CheckpointRepository atomically commits + bounds recovery history
        → RestoreExecutor preflights every approved external reference
        → execute only actions already described by the approved plan
        → return structured action and entry results

Startup, display-change, or configured hotkey request
    → WorkspaceService.RestoreWorkspaceAsync(snapshot)
        → build the same immutable plan
        → checkpoint, then execute through the same preflight and mutation boundary
        → retain one-click behavior without opening preview UI

Undo Last Restore
    → CheckpointRepository.GetLatest() isolates corrupt/expired documents
    → RestorePlanner.Build(checkpoint, current inventory, topology, Standard)
    → capture + commit a new Undo safety checkpoint
    → RestoreExecutor executes and verifies the normal plan
```

---

## Models

| Type | Purpose |
|---|---|
| `WorkspaceSnapshot` | Top-level save artifact. Contains a list of `MonitorInfo` and a list of `WorkspaceEntry`. |
| `WorkspaceEntry` | One saved window: app identity, optional file path, window position, and monitor assignment. |
| `MonitorInfo` | Physical monitor metadata: stable ID, friendly name, geometry, index, primary flag. |
| `WindowRecord` | Captured state of a live window: DPI-aware normalised rect, class name, title snippet, process name, executable path. |
| `RestorePlan` | Immutable restore intent, including candidates, placements, actions, warnings, blockers, and global actions. |
| `RestoreExecutionResult` | Structured stale-plan, per-action, and per-entry execution outcomes. |
| `WorkspaceCheckpointMetadata` | Versioned recovery identity, trigger, lifetime, source topology, and target workspace reference. |
| `RestoreCheckpointOutcome` | Created/failed/cancelled durability-gate result attached to restore execution. |
| `RestorePlanPreview` | UI-safe projection of a plan; it contains display state but performs no observation or mutation. |

---

## Adding a New Service

1. Create `Services/MyService.cs` in the correct namespace (`WindowAnchor.Services`).
2. Add a `<summary>` XML doc comment on the class and all public members.
3. Register the service as a singleton in `App.xaml.cs` alongside the existing services.
4. Inject it via the constructor of any service that needs it.
5. Use the structured `AppLogger` overloads for all diagnostic output — never `Debug.WriteLine`.

## Diagnostic logging and privacy

`AppLogger` writes JSON-lines events with a stable event ID, severity, static message, named fields, and optional exception metadata. Sensitive values must be carried in `LogField` instances so the centralized `LogRedactor` can apply the correct policy:

- Paths retain only their extension in shareable exports.
- URLs retain only their origin; credentials, path, query, and fragment are removed.
- Titles and workspace names are replaced with category markers.
- Stable identifiers become deterministic pseudonyms, preserving correlation across events.
- Command lines are scrubbed for credential switches, then redacted for embedded paths and URLs.
- Secrets are removed even from local diagnostic logs.

The local minimum severity comes from `AppSettings.DiagnosticLogLevel`. `AppLogger.ExportDiagnostics` defaults to `LogRedactionMode.Redacted`; the `--export-diagnostics [destination]` command-line option exposes that safe default without requiring manual log editing. Legacy unstructured messages are omitted from shareable exports because their sensitivity cannot be classified reliably.
