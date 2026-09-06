# WindowAnchor v1.5.2 — Restore Control and Update Recovery

## Summary

WindowAnchor 1.5.2 makes the safe restore pipeline easier to use day to day. Versioned desktop
applications such as Discord Canary and Discord PTB can survive Squirrel updates, routine preview
and checkpoint costs are independently configurable, and every manual restore entry point follows
the same workflow. Workspaces also gain explicit restore modes and per-entry policies.

New users now receive a one-time introduction to the tray-based application, and a permanent
in-app Help & Guide explains capture, restore, switching, policies, matching, recovery, browser
support, privacy, and operational limits without requiring the GitHub documentation.

## Highlights

- Recover a missing Squirrel executable only when the saved path has the recognized
  `<install-root>\app-<version>\<name>.exe` shape, the root contains `Update.exe`, and an immediate
  version sibling contains the exact same executable name. The newest parsed version wins.
- Keep restore data declarative and safe: v1.5.2 does not store or execute arbitrary user-defined
  regexes or wildcard paths.
- Choose Repair, Move Existing, Resume, Launch Fresh, Exact Switch, or Preview Only as a workspace
  default or as a one-time “Restore As” action.
- Override individual entries with reuse, launch-if-missing, always-launch-new, never-launch,
  never-close, or ignore-during-switch policies. Existing workspaces retain Resume behavior.
- Disable routine Restore Preview in Settings for direct execution. Ambiguous or blocked plans still
  open the review dialog because they require an explicit choice.
- Disable pre-restore checkpoints independently when lower latency matters more than creating a new
  Undo point. The transaction gate and single-flight restore/switch behavior remain active.
- Use the same restore/default-workspace/preview route from the tray, Settings, and hotkeys.
- Undo the complete captured context through switch-style reconciliation, including normal close
  requests for unrelated windows, rather than only moving or launching checkpoint entries.
- Compensate adapted semantic placements for invisible DWM resize borders so visible window frames
  can remain flush with a monitor work-area edge after topology changes.
- Open Help & Guide from the tray or Settings at any time. Fresh interactive installs see the same
  guide once with direct Save Workspace and Settings actions; upgrades, minimized startup, and
  native-messaging launches remain unobtrusive.

## Compatibility and migration

- Workspace schema v5 stores the workspace restore mode and per-entry policy while retaining stable
  IDs, exact rectangles, semantic/normalized layout data, and backward-compatible defaults.
- Settings schema v5 stores restore-preview, checkpoint, and onboarding choices. Existing settings
  migrate as onboarding-complete so an upgrade does not display first-run UI unexpectedly.
- Existing named workspaces, learned matches, checkpoints, monitor aliases, hotkeys, startup
  behavior, browser configuration, and notification preferences are retained.
- Squirrel rebinding changes only the immutable launch resource selected for the current plan. The
  concrete replacement path is revalidated immediately before launch.

## Internal quality work

The v1.5.2 codebase cleanup splits large restore planning/execution, capture, matching, settings,
native-messaging, and UI orchestration files into focused components. Compatibility façades and
equivalence tests preserve observable behavior while reducing coupling and making future review
safer for contributors.

## Verification

- 232 Release tests cover schema migration, explicit restore policies, ambiguity-safe assignment,
  Squirrel resolution, optional preview/checkpoint behavior, Undo reconciliation, visible-frame
  geometry, native-messaging framing, onboarding policy, and cleanup equivalence boundaries.
- The release workflow rebuilds and retests the tagged commit before uploading versioned assets and
  their SHA-256 checksums.

## Release assets

- `WindowAnchor-v1.5.2.exe` — self-contained Windows x64 desktop application.
- `WindowAnchor-Browser-Connector-v1.5.2.zip` — optional Chromium connector and native-host setup.
- `SHA256SUMS.txt` — SHA-256 checksums generated from the uploaded executable and connector package.

## Suggested verification for updated applications

1. Save Discord Canary/PTB or another recognized Squirrel-installed application.
2. Update it so the saved `app-<version>` executable no longer exists.
3. Restore from the tray, Settings, and the Restore Default hotkey and confirm each route resolves
   and launches the new executable.
4. Test once with Restore Preview enabled and once disabled. A safe executable plan should proceed
   directly when disabled; ambiguity or blockers should still request review.
5. If placement was saved on a disconnected or changed monitor, confirm the adapted visible frame
   reaches the expected work-area edge.
6. If checkpoints are enabled, perform a restore and then Undo Last Restore. If disabled, confirm
   the restore proceeds without creating a new Undo point.

## Updating

1. Exit the running WindowAnchor instance from its tray menu.
2. Download `WindowAnchor-v1.5.2.exe` and replace the previous executable, or run it directly.
3. Existing data under `%AppData%\WindowAnchor` is migrated and retained automatically.

The desktop executable is self-contained for 64-bit Windows and does not require a separate .NET
installation. It is not digitally signed, so Windows may display a security prompt.
