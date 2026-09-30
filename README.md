# WindowAnchor ⚓

WindowAnchor is a Windows 11 workspace manager that restores more than window rectangles. It saves
application placement together with supported files, browser sessions, File Explorer tabs, Windows
Terminal tabs, and virtual-desktop membership.

[![GitHub release](https://img.shields.io/github/v/release/marvintrvl/WindowAnchor)](https://github.com/marvintrvl/WindowAnchor/releases/latest)

The release badge and every download link in this README always point to the latest GitHub release.

![Saved workspace management](docs/screenshots/settings_saved_workspaces.png)

## Highlights

- Save selected windows across multiple monitors and Windows virtual desktops.
- Restore exact or adapted DPI-aware placement when monitor geometry changes.
- Reopen supported documents, folders, VS Code workspaces, browser sessions, Explorer tabs, and
  Terminal tabs.
- Recreate missing virtual desktops and return existing or launched apps to their saved desktop.
- Review ambiguous matches before restore; one live window is never assigned to two saved entries.
- Choose Repair, Move Existing, Resume, Launch Fresh, Exact Switch, or Preview Only.
- Keep optional recovery checkpoints and undo destructive workspace switches.
- Diagnose each restore with privacy-redacted, per-entry results.
- Recover layouts after temporary resolution or docking changes.
- Run as a self-contained tray application with configurable hotkeys and startup behavior.

WindowAnchor uses app adapters where Windows has no generic session API. Current adapters cover
Chromium browser sessions and PWAs, Firefox, File Explorer, Windows Terminal, VS Code, ATLauncher,
and normal Win32 applications.

## Quick start

1. Download `WindowAnchor-v<version>.exe` from the
   [latest release](https://github.com/marvintrvl/WindowAnchor/releases/latest) and verify it with
   that release's `SHA256SUMS.txt`.
2. Run the executable. WindowAnchor stays in the notification area.
3. Right-click its tray icon and choose **Save Workspace**.
4. Select the windows to include, name the workspace, and save it.
5. Restore it from the tray, Settings, or a configured hotkey.

The in-app **Help & Guide** explains restore modes, entry policies, checkpoints, browser support,
privacy, and operational limits.

![Workspace actions in the system tray](docs/screenshots/tray_workspace_actions.png)

## Session integrations

### File Explorer

With **Save open files** enabled, each Explorer window retains its folder tabs and active tab.
Restore adds only missing tab occurrences to the assigned window, preserves unrelated tabs, and
does not duplicate tabs on repeated restores.

### Windows Terminal

Windows Terminal does not expose every live tab's profile and current directory to external apps.
WindowAnchor therefore combines accessibility discovery with an optional PowerShell prompt hook.

Download `WindowAnchor-Terminal-Integration-<version>.ps1` from the matching release, place it in a
stable folder, and add this line at the end of the PowerShell profile used by the Terminal tab:

```powershell
. 'C:\path\to\WindowAnchor.Terminal.ps1'
```

Open that profile with `notepad $PROFILE`, then restart the tab. A short `[WA:...]` title marker
indicates tracking is active. Windows PowerShell and PowerShell 7 use separate profile files.

When saving, keep **Save open files** enabled and use **Configure Terminal tabs** to review the
left-to-right profile and directory list. Untracked cmd, WSL, and non-PowerShell tabs need manual
values. WindowAnchor restores profiles, starting directories, tab order, and active selection; it
does not restore command history, running programs, split panes, or arbitrary shell state.

Removing the profile line disables future tracking. Existing workspaces created by the earlier
prototype should be recaptured.

### Browser connectors

The Chromium connector is available from the
[Chrome Web Store](https://chromewebstore.google.com/detail/windowanchor-browser-conn/liiklnjpifhhmjncifbjjfgplonkkinh).
Use **Settings > Browser Integration > Set Up Chrome** to register the native host.

The Mozilla-signed [WindowAnchor Browser Connector for Firefox](https://addons.mozilla.org/en-US/firefox/addon/windowanchor-browser-connector/)
is available from Firefox Add-ons. Use **Settings > Browser Integration > Set Up Firefox** to
register the native host and open the listing. The source and development package remain in
[`firefox-extension/`](firefox-extension/).

Browser capture excludes private/incognito windows, cookies, passwords, page contents, and general
history. Exact tab reuse stays inside the opaque local browser profile that captured the session.

## Virtual desktops

When more than one Windows virtual desktop exists, Save Workspace groups applications by desktop
and monitor. Restore recreates missing desktops in saved order and returns matched or newly launched
windows to their assigned desktop.

This capability uses build-selected private Windows Shell interfaces on supported Windows 11
builds because Microsoft's public API cannot recreate desktop topology or reliably move another
process's window. Failures are isolated: WindowAnchor does not delete extra desktops, switch the
visible desktop, or make a window inaccessible. Applications running with higher privileges may
reject geometry changes even when their virtual-desktop move succeeds; that is reported as a
non-fatal warning.

## Safety and privacy

- Password managers and private browser windows are excluded by default.
- Restore plans revalidate windows and launch targets before mutation.
- Exact Switch and Undo require a durable checkpoint; routine checkpoints remain optional.
- Window closing uses normal close requests and never force-terminates applications.
- Diagnostic exports redact paths, titles, URLs, workspace names, identifiers, and command lines.
- Workspace data stays under `%AppData%\WindowAnchor` unless the user explicitly exports it.

See [privacy-policy.md](privacy-policy.md) for the complete privacy statement.

## Known limits

WindowAnchor cannot generically restore application-internal state that the application or Windows
does not expose. Examples include unsaved editor buffers, shell history, Terminal split panes,
arbitrary dialog state, and proprietary project state. Elevated applications may reject normal
window geometry changes from the unelevated app.

## Build and contribute

Prerequisite: .NET 8 SDK.

```powershell
dotnet test WindowAnchor.sln --configuration Release
dotnet publish src/WindowAnchor/WindowAnchor.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true
```

See [build.md](build.md) for release packaging and [CONTRIBUTING.md](CONTRIBUTING.md) before opening
a pull request.

## Documentation

- [Roadmap](Roadmap.md)
- [Architecture](docs/architecture.md)
- [Implementation and verification status](docs/implementation-status.md)
- [Browser integration](docs/browser-integration.md)
- [Workspace import and export](docs/workspace-import-export.md)
- [Restore simulation](docs/restore-simulation.md)
- [Changelog](CHANGELOG.md)

## License

WindowAnchor is licensed under the [MIT License](LICENSE).
