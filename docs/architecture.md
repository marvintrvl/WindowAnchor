# WindowAnchor — Architecture

This document describes the internal architecture of WindowAnchor v1.5.0 for contributors and maintainers.

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
- **`GetCurrentMonitors()`** — returns a `List<MonitorInfo>` where each entry has a stable `MonitorId` (same EDID-based format as the fingerprint), a friendly name from `DisplayConfigGetDeviceInfo`, geometry from `EnumDisplayMonitors`, and a primary flag.
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

### `WindowService`
Applies explicit policies to raw inventory and owns live-window enrichment/mutation.

- **`SnapshotWindows(policy, monitors?)`** — filters raw observations with the named policy,
  enriches selected windows with placement/browser/folder data, and optionally assigns monitors.
- **`GetWindowsWithPids(policy)`** — returns a policy-selected
  `HWND → (PID, WindowRecord)` dictionary for restore matching.
- **`InspectUserWindows(SwitchRiskCandidate)`** — supplies raw safe-switch preflight risks,
  including owned save dialogs that capture excludes.
- **`CloseAllUserWindows(SwitchCloseCandidate)`**, **`CountUserWindows(SwitchRiskCandidate)`**,
  and **`MinimizeUserWindowsExcept(MinimizeCandidate, keep)`** make mutation/risk policy explicit.
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
selection stays deterministic.

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

### Restore plan execution and staleness

`RestoreExecutor` is the only boundary that turns an approved `RestorePlan` into process, browser,
or window mutations. It executes only actions already present in that plan through injectable
process-launch, browser-session, window-inventory/mutation, resource-validation, and clock
boundaries. Browser restoration is itself an explicit plan action; its ordinary-browser fallback
is also explicit and conditional on that action becoming unavailable.

Immediately before each placement, the executor enumerates eligible windows again and verifies the
approved HWND, PID, and saved identity. Immediately before each launch, it revalidates the approved
file, folder, URL, executable, or packaged-app identity. Closed/reused HWNDs and changed resources
produce structured `RestoreExecutionResult` stale-plan outcomes and receive no mutation. The
executor retains the compatibility three-second and two-second launch waits; later readiness and
verification tickets replace those waits without changing the plan contract.

### Restore preview and approval

`RestorePlanPreviewBuilder` projects the immutable plan into exact, adapted, ambiguous, missing,
ready, skipped, and cancelled entry states plus move, launch, browser, wait, minimize, and no-change
action labels. It never reads live state. `RestorePlanPreviewDialog` renders only this projection,
uses keyboard-focusable entry checkboxes and automation labels, and distinguishes blocking errors
from destructive minimize actions. Because close behavior is not part of the current restore plan,
the preview explicitly states that no windows will be closed; safe-switch close analysis remains a
separate future boundary.

Unchecking an entry calls `RestorePlanner.DeriveApprovedPlan`. The derived plan retains the original
candidate evidence and placement but removes that entry's actions and blocking errors without
changing the preview object. Its selected HWND is protected from align/minimize. Disabling an
ordinary-browser entry also removes the global browser-session action and makes enabled browser
fallbacks explicit, preventing the connector from recreating a disabled entry.

Manual tray and Settings restores pass the derived plan to `ExecuteApprovedRestorePlanAsync`.
Before any mutation, the executor compares the current eligible candidate inventory with the
preview and revalidates every referenced resource. Changed state returns `StalePlan`, displays a
clear retry message, and is never silently replanned. Startup, display-change, and hotkey restores
retain their existing one-click compatibility path.

### `WorkspaceService`
The main capture/restore orchestration service. Called by `LayoutCoordinator` and directly by UI
code. Snapshot construction and persistence are deliberately separate boundaries.

- **`TakeSnapshot(name, saveFiles, monitorIds, progress)`** — the side-effect-free window capture:
    1. Get fingerprint and current monitors.
    2. Enumerate live windows.
    3. Filter to selected monitors (when `monitorIds` is not null).
    4. For each window: Tier 1 title parse → Tier 2 jump-list lookup → Tier 3 file search.
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
    2. Pass that plan to `RestoreExecutor`, which revalidates every external target immediately
       before executing its approved action.
    3. Reconcile windows that appear after launches using the compatibility three-second and
       two-second waits, while preserving one-to-one HWND assignment across the execution.
    4. Return structured per-action and per-entry outcomes; the legacy restore-session result remains
       an adapter for existing callers during migration.
- **`CreateRestorePlan(snapshot, mode)`** — performs the read-only observation and pure planning
  phases for preview or explicit approval workflows.
- **`ExecuteRestorePlanAsync(plan, token)`** — executes an already-approved plan and reports stale
  preconditions without silently replanning.
- **`RestoreWorkspaceSelectiveAsync(snapshot, monitorIds, token)`** — builds a selective plan that
  retains excluded entries as explicit results, then executes only included-entry actions.

### `StorageService`
Atomic, versioned JSON persistence and migration. `StorageService` remains the application-facing
compatibility façade; typed repositories prevent permanent, recovery, and short-lived artifacts
from being mixed.

- **Storage paths** (under `%AppData%\WindowAnchor\`):
    - `workspaces/{workspaceId}.workspace.json` — permanent named workspaces.
    - `checkpoints/{workspaceId}.checkpoint.json` — recovery checkpoints.
    - `temporary-captures/{workspaceId}.temporary.json` — short-lived captures.
    - `settings.json` — versioned application settings with ID-based workspace references.
    - `last_fingerprint.txt` — persists the last-known fingerprint across restarts.
    - `.migrated_v2` — completion marker for the legacy monitor-profile import only.
- `NamedWorkspaceRepository`, `CheckpointRepository`, and `TemporaryCaptureRepository` enumerate
  only their own directory and extension. Checkpoint or expiry cleanup therefore cannot discover or
  delete permanent workspaces.
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

- **`HandleDisplayChangeAsync()`** — debounces the event (1 s), computes the new fingerprint, looks up a matching workspace, and calls `WorkspaceService.RestoreWorkspaceAsync` if one is found.
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
        → RestoreExecutor preflights every approved external reference
        → execute only actions already described by the approved plan
        → return structured action and entry results

Startup, display-change, or configured hotkey request
    → WorkspaceService.RestoreWorkspaceAsync(snapshot)
        → build the same immutable plan
        → execute it through the same preflight and mutation boundary
        → retain one-click behavior without opening preview UI
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
