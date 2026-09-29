# WindowAnchor v1.7.0 — Desktop Context

## Summary

WindowAnchor 1.7.0 restores workspace context across Windows Terminal tabs and Windows virtual
desktops. It also adds temporary display recovery and targeted compatibility for applications whose
normal process or ownership model cannot be restored generically.

## Highlights

- Capture Terminal tabs with their PowerShell profile, current directory, order, and active tab.
- Review or correct Terminal tab metadata before saving; restore the complete tab set with one
  supported `wt` command.
- Capture eligible applications across all current Windows virtual desktops and show them grouped
  by desktop and monitor in Save Workspace.
- Recreate missing virtual desktops and return existing or newly launched windows to their saved
  desktop without deleting extra desktops or switching the visible desktop.
- Restore ATLauncher through `ATLauncher.exe` instead of launching its bundled `javaw.exe` without
  the required command line.
- Recover a saved layout after a temporary, stabilized display change with ask-first, disabled, or
  checkpointed automatic behavior.
- Treat elevated IObit geometry rejection as a non-fatal limitation after its launch and desktop
  placement succeed.

## Compatibility and migration

- Workspace schema v11 stores ordered virtual-desktop topology and per-entry association. Older
  workspaces continue to load and simply contain no topology to recreate.
- Existing settings keep loading; the obsolete virtual-desktop opt-in is ignored because capture
  and restore now follow saved workspace data automatically.
- Terminal workspaces created by the earlier prototype did not contain trustworthy per-tab data and
  should be recaptured.
- The private Windows Shell path is build-selected for Windows 11 23H2 and 24H2/25H2. Unsupported
  versions skip only topology operations and leave windows accessible.

## Verification

- 371/371 Release tests pass with no failures or skips.
- A live two-tab Windows Terminal round trip restored distinct directories and tab selection.
- A reversible live desktop smoke created a desktop, moved a controlled window, verified
  membership, returned it, and removed the temporary desktop.
- A complete live workspace restore recreated desktop 2 and restored IObit plus both ATLauncher
  windows to it; the same restore also passed when desktop 2 already existed.
- The prior Windows 11 25H2 Explorer three-tab smoke remains covered.

## Release assets

- `WindowAnchor-v1.7.0.exe` — self-contained Windows x64 application.
- `WindowAnchor-Browser-Connector-v1.7.0.zip` — optional Chromium connector.
- `WindowAnchor-Firefox-Connector-AMO-v1.7.0.zip` — unsigned AMO submission package.
- `WindowAnchor-Terminal-Integration-v1.7.0.ps1` — optional PowerShell prompt integration.
- `SHA256SUMS.txt` — checksums for all versioned assets.

The executable is not digitally signed, so Windows may show a security prompt. Firefox Release and
Beta require Mozilla to sign the Firefox connector before normal installation.

## Updating

1. Exit WindowAnchor from its tray menu.
2. Download `WindowAnchor-v1.7.0.exe` from this release and verify its checksum.
3. Replace the previous executable or run the new file directly.
4. Existing data under `%AppData%\WindowAnchor` migrates automatically.
