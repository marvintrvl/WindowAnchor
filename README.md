# WindowAnchor

**Restore your workspace in one click** — WindowAnchor remembers every open app, every file, and every window position, and puts it all back exactly where it was, even after reboots or monitor configuration changes.

---

## Features

| | |
|---|---|
| **Workspace snapshots** | Save named snapshots of all running apps, open files, and window positions |
| **Monitor-aware profiles** | Automatically detects your current monitor layout and saves positions per configuration |
| **Smart restore** | Re-launches closed apps with the correct file argument, then repositions every window |
| **Jump-list file detection** | Identifies recently-opened files for each app without user input |
| **Tray-only — zero chrome** | Lives entirely in the system tray; no taskbar entry, no splash screen |
| **Autostart support** | Optional "start with Windows" — app wakes up silently and is ready to restore |
| **Inline editing** | Rename or delete workspaces and profiles directly in the Settings panel |
| **Win 11 UI** | Built with WPF-UI (Fluent design) — looks and feels native on Windows 11 |

---

## Download

Grab the latest `WindowAnchor.exe` from the [**Releases**](../../releases) page — no installer, no .NET SDK required.

---

## Quick start

1. Double-click `WindowAnchor.exe` — it minimises to the system tray.
2. Open your apps and arrange your windows.
3. Right-click the tray icon → **Save Workspace** → give it a name.
4. Reboot, change monitors, or just close everything.
5. Right-click → **Restore Workspace** → pick your saved workspace.

---

## Usage

### Tray menu

| Action | Description |
|---|---|
| **Save Workspace** | Snapshot all running apps + window positions |
| **Restore Workspace ▸** | Sub-menu of saved workspaces; click one to restore |
| **Save Monitor Profile** | Save window positions for the current monitor layout |
| **Restore Layout Now** | Reposition all open windows to the saved positions for the current monitors |
| **Settings** | Open the Settings panel |
| **Exit** | Close WindowAnchor |

### Settings panel

- **Workspaces** — list of saved snapshots; rename or delete inline
- **Monitor Profiles** — one or more profiles per monitor configuration; rename or delete inline
- **General** — toggle "Start with Windows"

---

## How it works

1. **Monitor fingerprint** — WindowAnchor computes a stable SHA-256 hash of your connected monitors (using Windows Display Config + EDID data). This fingerprint is used as the key for monitor profiles, so the right profile is loaded automatically when you reconnect the same set of monitors.

2. **Window snapshot** — `EnumWindows` walks every visible, non-system window. For each window the executable path, process name, window class, title, and DPI-aware screen rectangle are recorded. Open-file detection uses two tiers: (1) parse the window title for a recognisable file path; (2) query the Windows jump-list database to find recently-opened files for the app.

3. **Restore phases**
   - *Phase 1* — already-running windows are repositioned immediately.
   - *Phase 2* — closed apps are re-launched with the saved file argument.
   - *Phase 3* — wait for the new processes to create their windows (up to 8 s).
   - *Phase 4* — reposition the newly-launched windows.
   - *Phase 5* — maximise/minimise windows that were in those states when saved.

4. **Storage** — everything is plain JSON in `%AppData%\WindowAnchor\`:
   - `workspaces/{name}.workspace.json`
   - `profiles/{guid}.profile.json`
   - `app.log` (rolling, max 2 MB)

---

## Building from source

Requirements: .NET 8 SDK, Windows 10/11.

```
git clone https://github.com/YOUR_USERNAME/WindowAnchor
cd WindowAnchor/src/WindowAnchor
dotnet build -c Release
```

### Publishing a self-contained single-file exe

```
dotnet publish -c Release -p:PublishSingleFile=true
```

Output: `bin/Release/net8.0-windows/win-x64/publish/WindowAnchor.exe`

---

## Requirements

- Windows 10 (1903+) or Windows 11
- x64 processor
- No .NET runtime installation needed (self-contained binary)

---

## License

MIT
