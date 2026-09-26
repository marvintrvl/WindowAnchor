# WindowAnchor v1.6.1 — Session Fidelity

## Summary

WindowAnchor 1.6.1 restores more of the context inside application windows. Windows 11 File
Explorer workspaces now retain all folder tabs in each saved Explorer window and the active tab.
Browser sessions gain profile-aware matching, and a separately packaged Firefox connector reaches
the supported Chromium connector's tab, group, active/pinned state, and window-geometry coverage.

## Highlights

- Save all folder tabs associated with a Windows 11 File Explorer window when file/folder capture
  is enabled, instead of retaining only the active folder.
- Restore only missing Explorer tab occurrences into the revalidated assigned window, preserve
  unrelated tabs, reselect the saved active tab, and avoid duplicates on repeated restores.
- Keep Explorer tab paths out of saves made without file/folder capture, redacted restore
  diagnostics, and portable-redacted workspace exports.
- Match reusable Chromium and Firefox tabs only inside the opaque connector profile that captured
  them. Legacy sessions without a profile identity and `file://` tabs continue to open separately.
- Package an AMO-ready Firefox Desktop 142+ connector with private-window exclusion, per-tab
  failure isolation, native-host registration, and declared data categories.
- Preserve explicit VS Code `.code-workspace` and project-folder context through the application
  adapter pipeline.

## Compatibility and migration

- Workspace schema v9 migrates older Explorer entries by retaining their one legacy active folder
  as a one-tab session; it never invents tabs that were not captured.
- Existing named workspaces, monitor layouts, restore policies, browser sessions, learned matches,
  checkpoints, hotkeys, startup behavior, and settings are retained.
- Explorer multi-tab capture remains controlled by the existing **Save open files** choice.

## Verification

- 326/326 Release tests pass with no failures or skips.
- An isolated live Windows 11 25H2 smoke restored three real Explorer folder tabs, selected the
  saved active tab, and opened zero duplicate tabs on a second restore.
- The Firefox connector's four Node tests pass; `web-ext` 10.7.0 reports zero errors, notices, or
  warnings.
- The configured NuGet sources report no known vulnerable direct or transitive packages.
- The release workflow rebuilds and retests the tagged commit before uploading versioned assets
  and SHA-256 checksums.

## Release assets

- `WindowAnchor-v1.6.1.exe` — self-contained Windows x64 desktop application.
- `WindowAnchor-Browser-Connector-v1.6.1.zip` — optional Chromium connector and native-host setup.
- `WindowAnchor-Firefox-Connector-AMO-v1.6.1.zip` — Firefox AMO submission package.
- `SHA256SUMS.txt` — SHA-256 checksums for all three versioned assets.

The Firefox ZIP is intended for Mozilla review/signing and is not installable in normal Firefox
Release/Beta until Mozilla signs it through AMO. A live signed Firefox multi-profile smoke remains
an external publication gate.

## Suggested verification after updating

1. Save a workspace with **Save open files** enabled and an Explorer window containing several
   folder tabs, close that Explorer window, and restore the workspace.
2. Repeat the restore while the tabs are present and confirm no duplicates are added.
3. If using a browser connector, capture and restore normal windows in each intended profile and
   review the configured exact-URL reuse policy.
4. Review any `CompletedWithFailures` result when a saved folder no longer exists; WindowAnchor
   leaves unrelated tabs untouched.

## Updating

1. Exit the running WindowAnchor instance from its tray menu.
2. Download `WindowAnchor-v1.6.1.exe` and replace the previous executable, or run it directly.
3. Existing data under `%AppData%\WindowAnchor` is migrated and retained automatically.

The desktop executable is self-contained for 64-bit Windows and does not require a separate .NET
installation. It is not digitally signed, so Windows may display a security prompt.
