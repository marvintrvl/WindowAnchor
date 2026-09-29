# Implemented Ticket Verification

**Audit date:** 2026-09-29

**Automated gate:** 371 passed, 0 failed, 0 skipped in Release

**Dependency audit:** no known vulnerable direct or transitive NuGet packages from configured sources

This page separates implemented behavior from external publication gates and deliberately partial
work. Automated tests do not replace live Windows application checks.

## Implemented and verified

| Capability | Evidence |
|---|---|
| Restore planning, matching, modes, preview, readiness, placement, checkpoints, and diagnostics | Deterministic service tests cover ambiguity, staleness, cancellation, one-HWND ownership, checkpoint durability, conditional preview/checkpoints, DPI-aware adaptation, bounded placement verification, and redaction. Undo and undo-of-undo use the same Exact Switch reconciliation path, and a failed mandatory Undo checkpoint sends no close request. |
| File Explorer tabs | Automated coverage plus an isolated Windows 11 25H2 three-tab restore, active-tab selection, and zero duplicates on rerun. |
| Windows Terminal tabs (WA-021) | Automated capture/planning coverage and a live two-tab round trip with distinct directories and active selection. The optional PowerShell integration is packaged with releases. |
| Virtual desktops (WA-022/WA-048) | Automated topology mapping plus live inactive-desktop capture, temporary desktop create/move/return/remove, and complete deleted/existing desktop-2 workspace restores with IObit and ATLauncher. |
| Temporary display recovery (WA-030B) | Synthetic stabilization, return-only triggering, duplicate suppression, fullscreen deferral, and checkpoint gates pass. A physical dock/resolution smoke remains useful release validation. |
| App adapters | Chromium PWA, dedicated browser, Explorer, Terminal, VS Code, ATLauncher, IObit placement policy, and generic fallback have focused tests. |
| Restore simulation foundation (WA-046) | Versioned, redacted planner fixtures run without native mutation and fail deterministically on expectation differences. |

## External or manual gates

- **WA-006A:** automated Undo, undo-of-undo, and failed-checkpoint coverage passes. Run and record
  the [real-Windows Restore, Exact Switch, Undo, and undo-of-undo checklist](manual-verification-wa-006a.md).
- **Firefox:** the connector is published and Mozilla-signed on
  [Firefox Add-ons](https://addons.mozilla.org/en-US/firefox/addon/windowanchor-browser-connector/);
  a live signed multi-profile round trip remains a manual gate.
- **Public issue #12:** update-safe Squirrel path rebinding is implemented and released; the issue
  remains open for reporter verification against updated Discord Canary/PTB installs.

## Partial work kept in scope

- **WA-046:** add the remaining dual/laptop topology, identical-monitor, mixed-DPI, multiple VS Code,
  corrupt-input, and path-alias fixtures.
- **WA-033:** internal exact and portable/redacted transfer is atomic and bounded, but the user-facing
  import/export flow, conflict preview, and old transfer-schema migration are incomplete.

Provider/cloud sync groundwork is not a shipped feature and is no longer an active product
commitment. See [Roadmap.md](../Roadmap.md) for the intentionally narrow active pipeline.
