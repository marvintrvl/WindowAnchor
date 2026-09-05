# Changelog

All notable changes to WindowAnchor will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

## [1.5.1] - 2026-09-05

### Added
- **Matching confidence and ambiguity resolver** — Deterministic service-layer thresholds now classify exact, strong, probable, ambiguous, missing, and ineligible outcomes. Candidates inside the top-vs-runner-up safety margin receive no automatic HWND assignment.
- **Interactive ambiguity resolution** — Restore Preview exposes candidate title, process/class, monitor, bounds, score, confidence, and evidence; selecting a candidate derives a new immutable plan while preserving session-wide one-HWND ownership.
- **Optional learned matches** — User-approved choices can be persisted by stable workspace/entry IDs plus composite application identity. HWND/PID and title-only hints are never stored, and all remembered choices can be cleared in Settings.
- **Application readiness engine** — Approved launches now progress through `NotStarted`, `ProcessStarted`, `WindowFound`, `Ready`, `TimedOut`, or `Failed` using process existence, safe identity matching, responsiveness, and stable title/class/bounds observations.
- **Adapter readiness strategies** — Application-specific strategies can override the generic stability rule while retaining the shared ambiguity-safe window matcher.
- **Post-restore placement verification** — Assigned windows are re-read after a short settling
  interval and compared with planned normal bounds/state using DPI-aware tolerance. Mismatches use
  bounded same-HWND retries and report `Applied`, `Rejected`, `MovedByApp`, or `WindowGone` with
  retry count and strategy.
- **Placement verification strategies** — Application adapters may override verification delay,
  tolerance, and retry count without bypassing matching or session-wide HWND ownership.
- **Semantic and normalized window layouts** — Workspace schema v4 stores source monitor bounds,
  work areas, DPI, normalized X/Y/W/H, horizontal/vertical anchors, and detected full, half, third,
  centered, or custom layouts alongside legacy exact pixels.
- **Reviewed workspace switching** — Tray, Settings, and switch hotkeys now show the same confidence,
  ambiguity, evidence, and adapted-placement preview used by manual restore.
- **Transactional pre-restore checkpoints** — Restores, adaptive/automatic restores,
  align-and-minimize, workspace switches, and undo capture the current desktop through the shared
  snapshot pipeline and atomically persist it before the first mutation. A failed checkpoint blocks
  the operation without moving, minimizing, closing, or launching anything.
- **Undo Last Restore** — The tray exposes the latest healthy recovery point. Undo restores it
  through the normal planner and first captures a new safety checkpoint, making undo itself
  recoverable.
- **Bounded checkpoint storage** — Versioned recovery metadata and a reconstructable metadata index
  support a default maximum of ten checkpoints retained for seven days. Expired/oldest checkpoints
  are pruned without scanning named workspaces, and corrupt checkpoint files are isolated.
- **Restore progress window** — Manual restore, switch, hotkey restore, and undo now identify the
  active checkpoint, resource, browser, launch, readiness, close-wait, and placement stage, with
  item counts, continuously updating elapsed/limit timing, and safe cancellation.

### Changed
- **Settings schema v3** — Existing settings migrate automatically to the learned-match-capable schema without changing prior preferences.
- **Safer reconciliation** — Legacy and post-launch matching paths now leave close candidates unresolved instead of selecting the lowest HWND.
- **Signal-driven launch reconciliation** — Fixed three-second/two-second restore sleeps were replaced by cancellable 250 ms polling with a 45-second per-entry timeout. Ready entries are positioned immediately while slower entries continue independently, and a wait starts only after a successful launch/browser action related to that entry.
- **Startup restore scheduling** — Startup restore is queued at dispatcher idle instead of imposing an unrelated two-second delay.
- **Topology-aware placement policy** — Identical monitor geometry uses exact saved pixels; changed,
  resized, rotated, taskbar-adjusted, mixed-DPI, or missing-monitor topologies use semantic/normalized
  work-area placement. Legacy rectangles remain supported and are clamped fully on-screen.
- **Destination-window preservation during switching** — Approved target HWNDs remain open and are
  reused; only unrelated close candidates receive `WM_CLOSE`.
- **Pre-cancelled restore safety** — Cancellation before the transaction durability gate now performs
  zero mutation instead of allowing an initial window move.
- **Bounded capture discovery** — Recovery checkpoints use fast title, Explorer-folder, PWA, and
  browser metadata without parsing Jump Lists or recursively scanning user folders. Comprehensive
  named saves retain Jump List discovery and share one five-second Tier-3 folder-search budget
  instead of paying an unbounded cost per window.

### Fixed
- **Slow restore and switch startup** — Pre-restore checkpoints no longer rebuild every Windows
  jump-list or recursively search Documents, Desktop, Downloads, and OneDrive. VS Code/Cursor
  workspace suffixes are no longer mistaken for part of a filename. Transactional checkpoints also
  skip per-application Jump List parsing, preventing a large or locked Chrome list from blocking a switch.
- **Readiness timeout wall clock** — The readiness limit now includes time spent probing windows
  and processes; expensive observations can no longer stretch it beyond its configured 45 seconds.
- **Changeable ambiguity choices** — Selecting a different candidate after initially resolving an
  ambiguous entry now updates the immutable plan and keeps exactly one radio button visibly selected.
- **Expected switch closures marked stale** — Candidate HWNDs intentionally closed by the active
  workspace-switch transaction may disappear without invalidating selected target assignments;
  newly appearing eligible candidates still invalidate the reviewed plan.
- **Background surfaces captured as apps** — Window selection now uses native task semantics
  (`WS_EX_TOOLWINDOW`, `WS_EX_NOACTIVATE`, `WS_EX_APPWINDOW`, ownership, and
  DWM cloaking) instead of product, process, class, or title blacklists. Existing running processes
  with no eligible task window are explained and skipped without a readiness timeout.
- **Unrelated readiness waits** — A successful launch no longer starts polling every passive wait
  in the plan. Each wait is correlated with its own launch, browser-session restore, or a successful
  launch for the same stable application identity.
- **Unsafe duplicate and hosted-window assumptions** — Captured window multiplicity is preserved,
  while session-wide HWND ownership prevents one live window from satisfying multiple entries.
  Cross-process matching requires an exact AppUserModelID within the same package family;
  title-only runtime-host exceptions were removed.
- **Endless switch waiting notifications** — The switch close phase tracks only HWNDs it actually
  asked to close instead of polling the broader transient-risk inventory. A newer switch cancels
  the previous one, execution is serialized, timeout uses wall-clock time, and waiting toasts are
  rate-limited.
- **Missing-monitor off-screen restoration** — Fallback placement is rebased into the selected
  monitor work area and cannot remain fully outside the visible desktop.
- **Target-DPI double scaling** — Planner-produced final coordinates are no longer rescaled using
  the live window's pre-move monitor DPI.
- **Store/MSIX paths invalidated by app updates** — Stale versioned `WindowsApps` executables are
  rebound to the currently registered package and activated through the stable
  `PackageFamilyName!ApplicationId`; the missing old path is now a warning instead of a blocker.

## [1.5.0] - 2026-09-01

### Added
- **Restore plan preview and per-entry controls** — Manual tray and Settings restores now show exact, adapted, ambiguous, skipped, and missing outcomes before changing the desktop. Individual entries can be disabled without mutating or recomputing the original plan.
- **Stale-preview protection** — Approved HWNDs, candidate inventories, browser capability, executables, files, folders, and URLs are revalidated before execution. A changed preview is rejected with a structured explanation instead of silently replanning or applying stale actions.
- **Pure restore planning and structured execution** — Restore intent is now an immutable, privacy-redactable plan consumed by an executor with per-entry and per-action results.
- **Stable workspace and entry identities** — Versioned workspace/settings schemas migrate legacy data while preserving ID-based references.
- **Privacy-safe diagnostics foundation** — Structured logging classifies and redacts sensitive paths, URLs, titles, identifiers, workspace names, and command lines.

### Changed
- **Safer persistence** — Named workspaces, checkpoints, and temporary captures use isolated typed repositories and atomic file replacement.
- **Testable restore boundaries** — Window inventory, mutation, process launching, browser restoration, resources, and clocks are independently injectable.
- **Manual versus automatic restore flow** — Manual restore commands use preview and approval; startup, display-change, and configured hotkey restores retain the existing one-click behavior.

### Fixed
- **Session-wide HWND ownership** prevents two saved entries from claiming the same live window across restore phases.
- **Browser and web-app identity separation** prevents ordinary browser windows, dedicated-site windows, and installed PWAs from consuming one another's restore slots.
- **Disabled align/minimize entries** remain untouched and protected from the terminal minimize action.
- **Preview button labels** no longer show literal access-key underscore prefixes; Tab, Enter, Escape, and automation behavior remain intact.
- **Verifiable release assets** now include a CI-generated `SHA256SUMS.txt` matching the uploaded executable and browser connector.

## [1.4.2] - 2026-08-29

### Fixed
- **Tray workspace submenu stays open while moving the pointer into it** — The tray-only template uses a stable, slightly overlapping popup together with a 250 ms close grace period. Entering either the header or submenu cancels the pending close, so the left-opening workspace commands remain accessible at normal pointer speeds.

## [1.4.1] - 2026-08-29

### Added
- **Browser extension review submission** — The WindowAnchor browser connector has been submitted for review and is being evaluated for Chrome Web Store distribution.
- **Manual installation flow documented** — Local testing and manual install instructions are now included for the browser connector and the native host registration process.
- **Complete release assets** — GitHub releases now include both the self-contained Windows executable and a browser connector ZIP with the local registration script and instructions.

### Fixed
- **Browser setup is now user-controlled and quiet when incomplete** — Settings detects installed supported browsers and opens the selected browser's extension page for setup. The connector now handles a missing native host without repeated unchecked `Specified native messaging host not found` errors.
- **Browser Extension MVP** — Added a Chromium Manifest V3 connector that captures and restores selected browser tabs, pinned state, tab groups, active tabs, and browser window bounds through the documented native-messaging protocol. Incognito tabs and unsupported/internal URLs are excluded.
- **WindowAnchor notifications can be disabled** — Settings now includes a persisted toggle for the app's system-tray progress and completion messages. This does not change notifications for Windows or other applications.
- **Installed web apps (PWAs) are no longer treated as plain browser windows** — Web apps installed from Chrome, Brave, Edge or another Chromium browser (e.g. Insilico Terminal, aggr.trade) run inside `chrome.exe`/`brave.exe` and use the same window class as a normal browser window. WindowAnchor identified them only by executable + class name, so restoring a layout opened a fresh browser window instead of the app. Every window's `AppUserModelID` is now captured; web-app windows are matched against the Start-Menu shortcut the browser created on install and relaunched through it. Window matching requires an exact `AppUserModelID` match, so a web-app entry can no longer claim a plain browser window (or vice versa).
- **Release workflow permissions and runtime compatibility** — The release job now has narrowly scoped write access, uses current Node 24-compatible GitHub actions, and uploads assets through GitHub CLI instead of the archived upload action.
- **Consistent application version** — Product, assembly, and file versions now report `1.4.1`.
- **OpenMcdf denial-of-service advisories** — Updated OpenMcdf from 3.1.0 to 3.1.4, which includes cycle detection fixes for crafted Compound File Binary inputs.

## [1.4.0] - 2026-08-28

### Added
- **WindowAnchor Browser Connector review submission** — The extension is now submitted for review and the project includes manual installation guidance for local use while the review is pending.
- **Local/manual setup instructions** — Steps for loading the extension locally in Chrome/Edge Developer Mode, registering the native host, and using the desktop app for testing.

### Changed
- **Release messaging updated for review state** — The release notes now explicitly state that the extension is under review and that local/manual installation remains available for users who want to test it before store publication.

### Fixed
- **Manual installation path validated** — Unpacked browser extension setup and native host registration have been tested for a local workflow without requiring the Chrome Web Store.

## [1.3.0] - 2026-03-01

### Added
- **Monitor Renaming** — Assign custom names to monitors in Settings → Monitors. Aliases replace hardware names (e.g. “Generic PnP Monitor”) throughout all dialogs: Save Workspace, Selective Restore, View & Edit Windows. Aliases are keyed by stable EDID-based MonitorId and persist across reboots.
- **Switch Workspace** — Instant context switching: closes all open windows, then restores the target workspace. Available from the workspace context menu (“Switch to Workspace”), the system tray menu (“Switch to: …”), and via the new **Ctrl+Alt+Shift+W** hotkey (switches to the default workspace).
- **Switch to Default hotkey** — New configurable `SwitchDefault` keyboard shortcut (default: Ctrl+Alt+Shift+W) for one-key context switching to the default workspace.
- **Switch Slot hotkeys** — Three new `SwitchSlot1/2/3` keyboard shortcuts (default: Ctrl+Alt+Shift+1/2/3) for one-key context switching to workspace slots 1, 2, and 3 respectively (mirrors the Restore Ctrl+Alt+1/2/3 hotkeys but closes all open windows first).

### Changed
- Settings window now includes a “Monitors” section listing all connected displays with editable alias text fields.
- System tray “Workspaces” submenu now shows both “Restore:” and “Switch to:” entries for each saved workspace.

## [1.2.0] - 2026-03-01

### Added
- **Default Workspace Setting** — Choose what happens when WindowAnchor starts: restore a specific workspace, restore the most recently saved one, ask via a picker dialog, or do nothing. Configured under Settings → Startup.
- **Selective Window Save** — The save dialog now shows a per-window checkbox list grouped by monitor. Password managers (KeePass, 1Password, Bitwarden, etc.) and incognito/private browser windows are unchecked by default.
- **Global Keyboard Shortcuts** — Six built-in hotkeys for quick save, restore default, restore workspace #1/#2/#3, and open settings (default: Ctrl+Alt+S/R/1/2/3/W). Shortcuts are fully customisable in Settings → Keyboard Shortcuts.
- **Workspace Ordering** — Reorder workspaces via Move Up/Move Down in the context menu. The first three workspaces map to the Ctrl+Alt+1/2/3 hotkeys. Slot badges (#1, #2, #3) and a ★ default indicator are displayed in the workspace list.
- **Set as Default from Context Menu** — Right-click any workspace row → "Set as Default" to mark it as the startup workspace, or "Remove as Default" to clear it.
- **Browser Session Restore** — Chromium-based browsers (Chrome, Edge, Opera, Brave) launched without a specific URL now receive `--restore-last-session` to reopen previous tabs.
- **Settings Persistence** — All new settings (startup behaviour, hotkey customisations, workspace order) are saved to `%AppData%\WindowAnchor\settings.json`.

### Changed
- Save Workspace dialog redesigned from per-monitor checkboxes to a per-window checkbox list with smart exclusions.
- Settings window expanded with Startup Behavior, Keyboard Shortcuts, and workspace ordering sections.
- Tray menu and hotkey-based workspace restore now honour the user's custom display order.

## [1.1.1] - 2026-02-20

### Fixed
- Improved JumpList detection for Office Click-to-Run (MS 365) by bypassing `AppVLP.exe` redirection via process-name indexing.
- Added CRC-64/Jones hash lookup for direct per-app Jump List resolution (reliable for non-default handlers).
- Implemented "Tier 1.5" exact-filename matching against a larger (50) Jump List pool to catch files outside the top 10.
- Resolved Phase 2 restore collision where bare exe launches would "steal" DDE slots from pending document launches.
- Fixed Cursor AI editor support (added as Electron candidate for workspace folder promotion).
- Added missing title patterns for Adobe Acrobat, Notepad3, and Atom.
- Added detailed `[FileDetect]` debug diagnostics to `app.log` for troubleshooting file-path extraction.

## [1.1.0] - 2026-02-19

### Added
- Per-monitor workspace save/restore
- File detection via title parsing and jump lists
- Selective restore dialog (choose which monitors to restore)
- Progress window during workspace save

### Changed
- Unified "Monitor Profiles" and "Workspaces" into single feature

## [1.0.0] - 2026-02-18

### Added
- Initial release
- Basic window position save/restore
- Monitor fingerprinting
- System tray integration
