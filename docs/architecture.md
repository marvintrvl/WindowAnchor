# WindowAnchor — Architecture

This document describes the internal architecture of WindowAnchor v1.1.0 for contributors and maintainers.

---

## Principle: One Responsibility Per Layer

```
┌─────────────────────────────────────────────────────────────────┐
│  UI Layer         App.xaml.cs · SettingsWindow · Dialogs        │
│  (WPF, tray)      Owns no business logic. Calls Coordinator.    │
├─────────────────────────────────────────────────────────────────┤
│  Coordinator      LayoutCoordinator                             │
│                   Wires display-change events → WorkspaceService│
│                   Owns notification balloons.                   │
├─────────────────────────────────────────────────────────────────┤
│  Services         WorkspaceService · MonitorService             │
│                   WindowService · StorageService                │
│                   JumpListService · TitleParser                 │
│                   Pure logic, no UI dependencies.               │
├─────────────────────────────────────────────────────────────────┤
│  Models           WorkspaceSnapshot · WorkspaceEntry            │
│                   MonitorInfo · WindowRecord                    │
│                   Plain data, no logic, no dependencies.        │
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
    1. Create a `RestoreSessionContext` that owns per-entry state, timing, cancellation,
       browser/launch actions, structured results, and the entry ↔ HWND assignment maps.
    2. Reposition already-running windows, then launch missing apps and resources.
    3. Reconcile newly appeared windows after 3 seconds and slow launchers after another 2 seconds.
    4. `WindowIdentityExtractor` centralizes saved/live evidence and `WindowMatcher` returns
       deterministic scored candidates with reasons. `WindowRestorePlanner` proposes the highest
       eligible candidate; only the session can commit a one-to-one assignment, and committed live
       HWNDs stay unavailable in every later pass.
    5. Release a stale assignment only when the current inventory omits it and `IsWindowAlive`
       verifies destruction; the affected entry then explicitly becomes eligible for rematching.
    6. Align/minimize preserves the final set of HWNDs still owned by the restore session.
- **`RestoreWorkspaceSelectiveAsync(snapshot, monitorIds, token)`** — same as above but filters entries to the specified monitor IDs before restoring.

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

## Data Flow: Restore (Auto)

```
WM_DISPLAYCHANGE arrives at App.xaml.cs
    → LayoutCoordinator.HandleDisplayChangeAsync()
        → debounce 1 s
        → MonitorService.GetCurrentMonitorFingerprint()
        → WorkspaceService.FindWorkspaceByFingerprint(fingerprint)
        → WorkspaceService.RestoreWorkspaceAsync(snapshot)
            → launch missing apps
            → poll for new windows (up to 8 s)
            → WindowService.RestoreWindow() for each match
        → NotifyBalloon("Workspace Restored", ...)
```

---

## Models

| Type | Purpose |
|---|---|
| `WorkspaceSnapshot` | Top-level save artifact. Contains a list of `MonitorInfo` and a list of `WorkspaceEntry`. |
| `WorkspaceEntry` | One saved window: app identity, optional file path, window position, and monitor assignment. |
| `MonitorInfo` | Physical monitor metadata: stable ID, friendly name, geometry, index, primary flag. |
| `WindowRecord` | Captured state of a live window: DPI-aware normalised rect, class name, title snippet, process name, executable path. |

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
