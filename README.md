# WindowAnchor ⚓

**WindowAnchor** is a modern, Fluent-designed window management utility for Windows 11. It allows you to capture your entire workspace — including window positions, sizes, and even open files — and restore them with a single click or automatically when your monitor configuration changes.

[![GitHub release](https://img.shields.io/github/v/release/marvintrvl/WindowAnchor)](../../releases/latest)

![Saved workspace management](docs/screenshots/settings_saved_workspaces.png)

##  Key Features

- **Workspace Snapshots**: Save your complete desktop layout, including multi-head setups.
- **Selective Window Save**: Choose exactly which windows to include via a per-window checkbox list — password managers and incognito windows are excluded by default.
- **Deep File Detection**:
    - **Tier 1**: Recovers open files via window title parsing.
    - **Tier 2**: Uses Windows Jump-List integration to accurately identify and relaunch specific documents in supported apps (Office, VS Code, etc.).
- **Selective Restore**: Choose exactly which monitors to restore via a picker dialog.
- **Explicit Restore Modes and Entry Policies**: Choose Repair, Move Existing, Resume, Launch
  Fresh, Exact Switch, or Preview Only. Each saved entry can inherit that mode or override it with
  reuse, launch-if-missing, always-launch-new, never-launch, never-close, or ignore-during-switch
  behavior. Existing workspaces default to the compatible Resume mode.
- **Explainable Match Resolution**: Close candidates are never guessed by HWND order. Restore
  Preview shows titles, app/class, monitor, bounds, confidence, score, and evidence so you can pick
  the correct window, optionally remember that composite identity, or skip the entry.
- **Application Readiness**: Launched apps are positioned only after their safely matched window is
  responsive and its identity and bounds are stable. Polling is cancellable, bounded per entry,
  and extensible through app-specific strategies.
- **Verified Window Placement**: After positioning, WindowAnchor re-reads normal bounds and state
  with DPI-aware tolerance. Apps that reject or override placement receive bounded same-HWND
  retries, with `Applied`, `Rejected`, `MovedByApp`, and `WindowGone` outcomes in the result.
- **Optional Transactional Restore & Full Undo**: Recovery checkpoints are enabled by default and
  can be disabled in Settings for lower restore latency. “Undo Last Restore” reconciles the saved
  pre-restore desktop, including closing unrelated windows, and creates an undo-of-undo point when
  checkpoints remain enabled.
- **Optional Restore Preview**: Manual tray, Settings, and hotkey restores share one policy. The
  review dialog can be disabled for routine one-click restores; WindowAnchor still opens it when
  ambiguity or a blocking entry requires an explicit choice.
- **Update-Safe Store App Launching**: Versioned `WindowsApps` paths are rebound to the currently
  installed package and launched through their stable AppUserModelID, so Store/MSIX updates do not
  turn an otherwise valid workspace entry into a missing resource.
- **Update-Safe Squirrel App Launching**: Missing `app-&lt;version&gt;` executable paths are resolved
  inside a recognized Squirrel install root (for apps such as Discord channels). Resolution is
  limited to immediate version siblings with the same executable name—no user-defined wildcard.
- **Adaptive Semantic Layouts**: Saves exact pixels together with monitor work areas, DPI,
  normalized geometry, anchors, and recognizable full/half/third/centered layouts. Changed or
  missing monitors use the semantic representation and are clamped fully onto a visible work area.
  Visible DWM frame bounds are kept distinct from invisible resize borders so edge-aligned windows
  do not acquire the usual Windows 8-pixel inset after adaptation.
- **Default Workspace & Startup Restore**: Set a default workspace to auto-restore on launch, restore the last-used one, or choose from a picker dialog.
- **Global Keyboard Shortcuts**: Customisable hotkeys for quick save, restore, workspace switching (Ctrl+Alt+1/2/3), switch workspace (Ctrl+Alt+Shift+1/2/3) and settings.
- **Workspace Ordering**: Reorder workspaces with Move Up/Down — the first three map to the hotkey slots.
- **Monitor Renaming**: Assign custom names to monitors (e.g. “Left”, “Ultrawide”) — aliases replace hardware names throughout all dialogs.
- **Reviewed Workspace Switching**: Preview confidence, ambiguity, and monitor adaptation first.
  Approved destination windows stay open; only unrelated windows receive normal close requests,
  with bounded single-flight waiting and no force-close behavior. Expected closure of an
  unselected candidate does not invalidate the already-reviewed plan.
- **Native Task-Window Filtering**: Workspace windows are selected from OS capabilities rather
  than application names: DWM-cloaked, tool-only, non-activatable, and owned/transient surfaces
  are not saved as independent tasks. `WS_EX_APPWINDOW` remains an
  explicit opt-in. Existing background/tray processes with no eligible task window are explained
  and skipped instead of being relaunched and awaited for 45 seconds.
- **Safe Multiplicity and Hosted Identity**: Distinct captured windows remain distinct; one HWND
  can satisfy only one entry, and an unavailable duplicate is reported rather than silently
  collapsed. Cross-process hosted windows match only through shared Windows identity such as an
  exact AppUserModelID within the same package family—not a process-name or title exception.
- **Browser Session Restore**: The optional Chromium connector captures and restores supported tabs,
  groups, pinned/active state, and browser-window geometry; ordinary browser launch remains the
  graceful fallback when the connector is unavailable.
- **Save Progress Transparency**: A dedicated progress window tracks the discovery of file paths and jump-lists during the save process.
- **Restore Progress Transparency**: Restore, switch, and undo show the active checkpoint,
  resource detection, browser, launch, readiness, close-wait, and placement-verification stage,
  including item counts, elapsed/limit timing, and safe cancellation.
- **Zero Dependencies**: Available as a high-performance, single-file standalone executable.
- **Fluent UI**: Fully integrated with the Windows 11 design language and system tray.
- **First-Run and Permanent In-App Guide**: A fresh interactive installation explains that
  WindowAnchor lives in the notification area and offers direct Save Workspace and Settings
  actions. The same Help & Guide window remains available from the tray and Settings, with a
  complete reference for restore modes, entry policies, matching, checkpoints, browser support,
  privacy, and operational limits.

![Workspace actions in the system tray](docs/screenshots/tray_workspace_actions.png)

##  The Core Workflow

WindowAnchor operates silently in your system tray, watching your display configuration. Using **Monitor Fingerprinting**, it identifies your current setup (e.g., "Home Office" vs. "Travel") and restores your preferred layout instantly.

1. **Download**: Get the latest Windows executable from the [Releases](../../releases) page.
2. **Get oriented**: On the first interactive launch, use the in-app guide to save a workspace or
   open Settings. Reopen it later with **Help & Guide** from the tray or Settings.
3. **Save**: Right-click the tray icon and select "Save Workspace...".
4. **Restore**: Choose a workspace to review and approve its plan, or simply dock your laptop for
   the configured automatic one-click restore.

## Settings at a Glance

Configure Windows startup behavior, notifications, browser integration, automatic workspace
restore, optional manual previews, optional recovery checkpoints, and remembered window choices
from one place. Open a workspace’s “View & Edit Windows” dialog to set its default restore mode and
the policy for each saved entry; “Restore As” in the workspace menu runs any mode once without
changing that default. Recapturing a workspace with the same name preserves its configured mode
and uniquely matched entry policies. **Help & Guide** provides these explanations inside the app,
so normal operation does not require the GitHub documentation.

![System, browser integration, and startup settings](docs/screenshots/settings_system_browser_startup.png)

Customize global keyboard shortcuts and assign recognizable names to connected monitors.

![Keyboard shortcuts and monitor aliases](docs/screenshots/settings_hotkeys_monitors.png)

## Review Status & Manual Install

The browser extension is currently under review for the Chrome Web Store. While review is in progress, the extension can still be used locally via manual installation.

### Local desktop app install
1. Download the latest versioned `WindowAnchor-v*.exe` from the GitHub release page and verify it
   against `SHA256SUMS.txt`.
2. Run the executable once to confirm the app starts correctly.
3. If Windows prompts for security permissions, allow the app to run.

### Manual browser extension install
1. Download the browser connector package from the GitHub release assets.
2. Open `chrome://extensions` or `edge://extensions` and enable **Developer mode**.
3. Click **Load unpacked** and select the extracted browser extension folder.
4. Copy the generated extension ID shown on the extension card.
5. Update the native host manifest and register the native messaging host for Chrome or Edge using the included PowerShell script.
6. Reload the extension and verify that it can capture and restore tabs.

For local testing, the included script is the recommended setup path:
```powershell
powershell -ExecutionPolicy Bypass -File .\register-native-host.ps1 -ExtensionId <id> -WindowAnchorPath <path-to-exe>
```

This manual workflow is fully supported for local testing while the extension is being reviewed.

## 🛠 How It Works

1. **Monitor fingerprint** — WindowAnchor computes a stable SHA-256 hash of your connected monitors. This is used to automatically match workspaces when you reconnect monitors.

2. **Window snapshot** — Enumerates visible windows, recording exact normal bounds, monitor/work-area
   geometry, DPI, normalized anchors, semantic layout, and process info. File detection parses
   window titles and queries Windows jump-lists to relaunch files.

3. **Choose policy, then plan and optionally approve** — The workspace mode and each entry’s
   persisted override are resolved once by the pure planner. Manual tray, Settings, and hotkey
   commands build the same immutable plan. With Restore Preview enabled, you can see the selected
   mode and applied policies, resolve candidates, remember a choice,
   disable entries, use Tab to navigate, Enter to approve, and Escape to cancel. When preview is
   disabled, executable plans continue immediately; plans needing an explicit decision still open
   the dialog. Every approved plan is checked for stale windows, browser capability, and launch
   resources before an action starts.

   Repair changes only mismatched existing placements; Move Existing never launches; Resume
   reuses or launches; Launch Fresh requests a distinct window only where a reliable contract is
   available; Exact Switch closes unrelated context normally; Preview Only cannot reach a mutation
   boundary. Unsupported fresh-instance requests are reported and reuse the safest match.

4. **Optional checkpoint** — When enabled (the default), a fast metadata-only desktop capture is
   atomically committed before the first move, minimize, close, browser restore, or process launch.
   It retains title, Explorer-folder, PWA, and browser metadata, but skips Jump List parsing and
   recursive folder scans. A failed required checkpoint blocks mutation; disabling checkpoints
   keeps the same single-flight restore serialization but creates no new Undo point.

5. **Execute and report** — The executor launches only approved targets, polls process/window
   readiness against a 45-second real wall-clock limit instead of sleeping for fixed intervals,
   and starts a wait only when a successful launch/browser action is related to that entry. It applies final DPI-aware positions/states as
   each entry becomes ready, then verifies the observed placement and performs at most two
   same-HWND corrections. Structured per-item outcomes include readiness, verification, retry
   count, tolerance, and final failure state.

## Docs & Architecture

For a deep dive into how WindowAnchor handles monitor fingerprints, DPI-aware restoration, and Tier 1/2 file detection, check out:
- [**Architecture Overview**](docs/architecture.md) — A technical breakdown of the services and data flow.

## Contributing

Contributions are what make the open-source community such an amazing place to learn, inspire, and create.
Please check the [**Contributing Guidelines**](CONTRIBUTING.md) before submitting a Pull Request.

## Building

**Prerequisites:** .NET 8.0 SDK.

**Build Standalone:**
```powershell
dotnet publish src/WindowAnchor/WindowAnchor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true
```

See [build.md](build.md) for clean builds, Release tests, packaging, and checksum generation. Each
published release includes `SHA256SUMS.txt` for its executable and browser connector.

## Star History

[![Star History Chart](https://api.star-history.com/svg?repos=marvintrvl/WindowAnchor&type=Date)](https://star-history.com/#marvintrvl/WindowAnchor&Date)

##  Roadmap

### v1.5.2 (Current Release) — *Restore Control and Update Recovery* ✅
- **Explainable Matching**: Confidence classes, ambiguity choices, and optional stable learned hints replace destructive guessing.
- **Responsive Restore**: Correlated readiness signals replace fixed sleeps, expose progress, and keep every wait cancellable and bounded.
- **Adaptive Layouts**: Semantic, normalized, DPI-aware placement keeps windows visible when monitors, work areas, or orientation change.
- **Verified Placement**: WindowAnchor checks final bounds/state and performs bounded same-HWND corrections when an app rejects a move.
- **Transactional Safety**: A durable checkpoint precedes every approved mutation, and Undo Last Restore uses the same safe planner.
- **Generalized Window Policy**: Native styles, ownership, DWM cloaking, and AppUserModelID determine task windows without product-specific blacklists.
- **Safer Switching**: Destination windows are preserved, unrelated close requests are tracked precisely, and superseded switches are cancelled.
- **Faster Diagnosis**: A live progress window identifies checkpoint, resource, browser, launch, readiness, close, and verification work.
- **Update-Safe Desktop Apps**: Recognized Squirrel `app-<version>` installations are rebound to
  the newest matching executable after application updates, without accepting arbitrary wildcards.
- **Restore Control**: Routine previews and pre-restore checkpoints can be disabled independently;
  ambiguity and blockers still require review, and restore execution remains serialized.
- **Restore Modes and Policies**: Workspaces support Repair, Move Existing, Resume, Launch Fresh,
  Exact Switch, and Preview Only plus persisted per-entry reuse/launch/close overrides.
- **First-Run Help**: A one-time welcome explains the tray workflow, while the complete guide stays
  available from both the tray and Settings.

### v1.3 — *UX Improvements* ✅
- **Monitor Renaming**: Personalise monitor names ("Generic PnP" → "Left Monitor") in Settings → Monitors.
- **Switch Workspace**: Instant context switch — closes all windows and restores a different workspace.
- **Switch Default hotkey**: Ctrl+Alt+Shift+W switches to the default workspace in one keystroke.
- **Switch Slot hotkeys**: Ctrl+Alt+Shift+1/2/3 switch to workspace slots 1, 2, and 3 (close-everything-first variant of the Restore hotkeys).

### v1.2 — *Stability & Control* ✅
- Selective Window Save, Default Workspace, Keyboard Shortcuts, Workspace Ordering, Browser Session Restore.

### v1.6 (Next Release) — *Diagnostics and Topology*
- **Restore Reports**: Present structured action and entry outcomes after execution.
- **Topology Stabilization**: Avoid restoring against transient monitor states while docking.
- **Workspace Diff**: Compare saved intent with the current desktop without changing either.
- **Adapter Architecture**: Add reusable application-specific identity and launch strategies behind the shared safety boundaries.

### v1.6+ — *Recovery and Adaptation*
- **Workspace Health and Diff**: Inspect missing resources and current-vs-saved differences without mutation.
- **Recovery Timeline and Quick Captures**: Add checkpoint browsing plus temporary workspace saves
  on top of the transactional checkpoint store.

### v2.0 & Beyond
- **Portability**: Logical path aliases, workspace import/export, and cross-device monitor identity.
- **Sync and Ecosystem**: Provider-neutral folder sync, catalog metadata, desk profiles, and templates.

##  License
This project is licensed under the MIT License.
