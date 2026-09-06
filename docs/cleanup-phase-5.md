# Cleanup Phase 5 — Lifecycle and Interop Hardening

**Status:** Complete
**Date:** 2026-09-06

Phase 5 completed the audit's lifecycle and native-boundary work while preserving restore and
browser protocol ordering.

## Completed changes

- Restore observation records privacy-safe duration and bounded inventory counts. Defensive
  re-observation remains in place; no cross-phase HWND or monitor cache was introduced.
- `LayoutCoordinator`, `WorkspaceSwitchEngine`, and the transaction coordinator now have explicit
  composition-owned cancellation/disposal paths. Application exit cancels active work before
  releasing shared synchronization primitives.
- Native messaging opens process-owned stdin/stdout once, reuses those streams for the session, and
  keeps framed responses flushed. Framing tests cover multiple messages, truncation, and size limits.
- Selected analyzer rules are checked in through `.editorconfig`; intentional P/Invoke, serialized
  DTO, and UI patterns are documented in `docs/analyzer-baseline.md`.
- Win32 probe results, AppUserModelId buffer probing, PROPVARIANT cleanup, and COM release guards
  are explicit without changing the accepted native behavior.
- Browser capture/restore calls retain their existing order and now emit duration/status metrics
  without titles, URLs, paths, or session contents. The extension manifest version `0.1.0` is an
  independent connector protocol version and is not synchronized automatically with desktop
  releases.

## Verification

- Release tests after correctness review: **194 passed, 0 failed, 0 skipped**.
- Analyzer-enabled Release build: **0 warnings, 0 errors** for the focused warning-level policy.
- ReadyToRun, single-file, self-contained win-x64 validation executable: 79,562,415 bytes.
- SHA-256: `A6F7279C07C243DBEF346FA8C9D1366D30E86CAAE90F281EB57347C7E4AA8AF5`.
- Validation path: `publish/cleanup-phase-5-reviewed/WindowAnchor.exe`.
- `git diff --check`: clean apart from standard Git line-ending normalization warnings.

## Correctness-review corrections

The post-phase review found and corrected issues in the initial implementation:

- transaction disposal now cancels and drains admitted restore operations before disposing its
  semaphore, including concurrent disposal callers;
- display-change and switch cancellation no longer runs callbacks while holding ownership locks,
  and replaced display token sources are disposed by their owning invocation;
- repeated asynchronous disposal callers await the same completion rather than returning early;
- caller cancellation from browser restore is propagated to the executor;
- parsed native-messaging request documents are disposed and concurrent framed writes are
  serialized inside the framing boundary;
- the focused analyzer rules are warnings rather than IDE-only suggestions, with explicit
  test/native-boundary exceptions; and
- an ineffective SDK publish exclusion added during initial verification was removed.

The manual UI/release gate remains separate because automated tests do not manipulate real desktop
windows, monitor topology, tray icons, or installed browser extensions.
