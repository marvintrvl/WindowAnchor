# Cleanup Phase 3 — Low-Risk Consolidation

**Implemented:** 2026-09-05
**Starting point:** completed cleanup Phases 0-2 on the `v1.5.1` code line
**Scope:** internal orchestration and file ownership only; no persisted schema or public feature was removed

## Changes

### Shared Save Workspace workflow

`UI/SaveWorkspaceWorkflow.cs` now owns the sequence previously duplicated in `App.xaml.cs` and
`SettingsWindow.xaml.cs`:

1. enumerate the per-monitor save preview;
2. show `SaveWorkspaceDialog` with the correct optional owner;
3. copy WPF-bound selections on the UI thread;
4. show and update `SaveProgressWindow` when file discovery is enabled;
5. capture through `WorkspaceService.CaptureWorkspaceAsync`;
6. persist once through the named-workspace store with the existing partial-browser policy;
7. emit the structured success/failure events; and
8. restore owner state and close progress UI on every exit.

The tray remains responsible for its success balloon. Settings remains responsible for refreshing
its workspace list and is disabled only while capture/persistence is active. Cancellation of the
dialog, selected-window counts, optional file discovery, browser capture, and error text are
unchanged. Preview-enumeration failure now follows the existing safe tray behavior in both entry
points: it is logged and the dialog opens with an empty preview rather than escaping an `async void`
Settings handler.

### Shared restore-result presentation

The Standard, Align and Minimize, and Selective wrappers in `LayoutCoordinator` now delegate to one
private execution-and-notification method. Each caller still supplies its original:

- `RestoreMode`;
- structured mode field and optional monitor count;
- starting, success, and failure titles/messages; and
- cancellation token and progress sink.

Checkpoint failure, cancellation, completed status, and attention status use one ordering. The two
adjacent `ShouldNotifyUser` checks in switch close-wait progress were combined into one gate, keeping
both the structured event and balloon on the same throttle decision.

### Dedicated UI files

- `DelayedSubmenuMenuItem` moved from `App.xaml.cs` to
  `UI/DelayedSubmenuMenuItem.cs`, retaining the `WindowAnchor` namespace required by `App.xaml`.
- `WorkspaceWindowsDialog` moved from `SettingsWindow.xaml.cs` to
  `UI/WorkspaceWindowsDialog.cs` with the same namespace, dimensions, resources, event wiring,
  grouping, saved-entry removal, and atomic storage call.

No dialog or control was removed, and the XAML type reference is compiled as part of the Release
gate.

### Canonical process-name normalization

`Services/ProcessIdentityNormalizer.cs` is now the single canonical comparison-key implementation
for inventory, matching, planning, readiness, execution, and Chromium-family detection. It:

- accepts null/empty values;
- trims surrounding whitespace;
- lowercases invariantly; and
- removes one terminal `.exe` suffix case-insensitively.

Original process names remain available for display and logs. Connector support, matching policy,
readiness policy, and launch policy retain their separate browser lists/decisions; only their string
normalization is shared. A table-driven corpus covers empty input, casing, whitespace, suffix
handling, repeated suffixes, and non-suffix text.

## Preservation evidence

- Release suite after implementation: **184 passed, 0 failed, 0 skipped**.
- Existing matching, one-HWND assignment, packaged-app, browser, readiness, switching, checkpoint,
  persistence, migration, placement, and privacy tests remain enabled.
- WPF XAML compilation resolves the moved `DelayedSubmenuMenuItem`.
- Both save callers now contain only context-specific post-success behavior.
- Repository search finds no remaining private `NormalizeProcessName` copies.

## Manual gate

Before release, use the final published executable to verify both Save Workspace entry points,
progress ownership/closing, the workspace-window removal dialog, tray submenu grace behavior,
Standard/Selective/Align completion messages, and ambiguous matching with process names originating
from real Win32 process APIs. This is part of the existing manual matrix in
`docs/cleanup-phase-baseline.md`.
