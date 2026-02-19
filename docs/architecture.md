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

### `WindowService`
Owns everything related to live windows.

- **`SnapshotAllWindows(monitors?)`** — calls `EnumWindows`, filters via `ShouldIncludeWindow`, calls `CaptureWindowRecord` for each visible window, and optionally tags each record with a monitor via `GetMonitorForWindow`. Returns a flat `List<WindowRecord>`.
- **`ShouldIncludeWindow(hWnd)`** — excludes invisible, cloaked, zero-area, tool, and known-OS-chrome windows (class allow-list in `OsWindowClassSkipList`).
- **`CaptureWindowRecord(hWnd)`** — calls `GetWindowPlacement` (DPI-aware normalised rect), `QueryFullProcessImageName`, `GetClassName`, `GetWindowText`. Returns a `WindowRecord`.
- **`RestoreWindow(hWnd, record)`** — calls `SetWindowPlacement` then, for maximised windows, a second `ShowWindow(SW_MAXIMIZE)` pass to ensure the maximised state is applied on the correct monitor.
- **`GetAllWindowsWithPids()`** — returns an `HWND → (PID, WindowRecord)` dictionary used by `WorkspaceService` during the restore matching passes.

### `WorkspaceService`
The main orchestration service. Called by `LayoutCoordinator` and directly by UI code.

- **`TakeSnapshot(name, saveFiles, monitorIds, progress)`** — the save pipeline:
    1. Get fingerprint and current monitors.
    2. Enumerate live windows.
    3. Filter to selected monitors (when `monitorIds` is not null).
    4. For each window: Tier 1 title parse → Tier 2 jump-list lookup → Tier 3 file search.
    5. Build and return a `WorkspaceSnapshot` (does not save to disk — caller decides).
- **`RestoreWorkspaceAsync(snapshot, token)`** — the restore pipeline:
    1. Launch any missing executables via `Process.Start` (with saved `LaunchArg`).
    2. Wait up to 8 seconds for them to create windows, polling `GetAllWindowsWithPids`.
    3. Match live HWNDs to `WorkspaceEntry` records by executable path + class name.
    4. Call `WindowService.RestoreWindow` for each matched pair.
    5. Perform a second pass for windows that arrived late.
- **`RestoreWorkspaceSelectiveAsync(snapshot, monitorIds, token)`** — same as above but filters entries to the specified monitor IDs before restoring.

### `StorageService`
Plain JSON file I/O. No business logic.

- **Storage paths** (under `%AppData%\WindowAnchor\`):
    - `workspaces/{name}.workspace.json` — one file per `WorkspaceSnapshot`.
    - `last_fingerprint.txt` — persists the last-known fingerprint across restarts.
    - `.migrated_v2` — sentinel written after the one-time v1→v2 migration.
- **`MigrateToV2()`** — on first run, converts any `profiles/*.profile.json` legacy files to `WorkspaceSnapshot` objects. Runs once, guarded by the `.migrated_v2` sentinel.

### `LayoutCoordinator`
Reacts to `WM_DISPLAYCHANGE` events forwarded from `App.xaml.cs`.

- **`HandleDisplayChangeAsync()`** — debounces the event (1 s), computes the new fingerprint, looks up a matching workspace, and calls `WorkspaceService.RestoreWorkspaceAsync` if one is found.
- Owns all notification balloon calls via the private `NotifyBalloon` helper, which marshals to the UI thread.

### `JumpListService`
Reads the Windows Jump-List AutoDestList binary files from `%AppData%\Microsoft\Windows\Recent\AutomaticDestinations\` using the OpenMcdf library to extract recently-opened file paths per application.

### `TitleParser`
Stateless utility class. `ExtractFilePath(processName, titleSnippet)` applies a set of regular expressions to the window title to extract a file path and returns a `(path, confidence)` tuple.

---

## Data Flow: Save

```
User clicks "Save Workspace"
    → SaveWorkspaceDialog collects name + monitor selection
    → WorkspaceService.TakeSnapshot(name, saveFiles, monitorIds, progress)
        → MonitorService.GetCurrentMonitors()
        → WindowService.SnapshotAllWindows(monitors)
        → JumpListService.BuildSnapshotCache()
        → per window: TitleParser + JumpListService → WorkspaceEntry
    → StorageService.SaveWorkspace(snapshot)
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
5. Use `AppLogger.Info` / `AppLogger.Warn` for all diagnostic output — never `Debug.WriteLine`.
