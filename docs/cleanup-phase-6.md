# Cleanup Phase 6 — Equivalence Gate and Core Decomposition

**Implemented increment:** 2026-09-06
**Status:** 6.0-6.6 implementation and automated gates complete; manual release matrix pending

This increment strengthens the behavior-freezing tests and completes the capture-specific
decomposition promised by Phase 4. It changes ownership of code, not workspace schemas, persistence
destinations, browser protocol, capture ordering, or user-visible restore behavior.

## 6.0 equivalence coverage

The new Phase 6 characterization tests freeze:

- complete deterministic projections for all-window and selective captures, including monitor and
  geometry metadata, file-enabled/disabled behavior, Explorer folders, PWA fallback commands,
  dedicated browser URLs, captured browser sessions, and generated stable-ID invariants;
- the exact progress-stage sequence and its legacy current/total values;
- native snapshot construction before browser connector capture;
- cancellation after native enumeration without crossing the browser boundary;
- deterministic exhaustion of the cumulative common-folder search budget;
- a SHA-256 golden fingerprint of a complete redacted restore plan;
- executor observation → mutation → verification → delay → re-verification ordering; and
- transaction behavior for queued cancellation, shutdown during checkpoint/execution, and
  concurrent asynchronous disposal.

The established planner and executor suites remain part of the same gate. They cover exact and
missing topology, ambiguity and learned choices, browser fallback, packaged apps, selective and
align/minimize modes, unique HWND assignment, correlated readiness, stale preflight rejection,
placement retry bounds, and cancellation boundaries.

## 6.1 capture ownership

The public `WorkspaceService` entry points remain stable, but detailed work now belongs to three
internal collaborators:

- `WorkspaceSnapshotBuilder` selects monitors and windows, applies all/selective capture policy,
  owns Jump List cache lifetime, emits progress, and assembles the final `WorkspaceSnapshot`;
- `CapturedWindowEntryFactory` constructs ordinary, Explorer, PWA, and dedicated-browser entries
  without persistence; and
- `CaptureResourceResolver` performs title parsing, exact/general Jump List matching, bounded
  common-folder search, confidence/source assignment, and editor launch-argument adaptation.

`WorkspaceCaptureBuilder` remains the top-level coordinator. It invokes native snapshot
construction first, then optional browser enrichment, then finalization. `PersistCapture` is still
the only write boundary and retains its explicit incomplete-browser policy.

`WorkspaceService.cs` is now 892 lines, down from 1,889 at the reviewed Phase 5 baseline. The new
files are organized by independently changing responsibility rather than an arbitrary line limit:

| Component | Lines | Responsibility |
|---|---:|---|
| `WorkspaceSnapshotBuilder.cs` | 170 | Window/monitor selection, cache lifetime, progress, assembly |
| `CapturedWindowEntryFactory.cs` | 155 | Persisted entry variants |
| `CaptureResourceResolver.cs` | 451 | Cohesive multi-tier resource-discovery policy and bounded traversal |

## 6.2 identity ownership

The former 853-line `WindowIdentity.cs` has been replaced by responsibility-specific files while
retaining every public type name, namespace, accessibility level, and `WindowMatcher` signature:

| Component | Lines | Responsibility |
|---|---:|---|
| `WindowIdentityModels.cs` | 175 | Public saved/live identity, evidence, confidence, candidate, and result records |
| `WindowIdentityExtractor.cs` | 160 | Saved/live projection, path/title/URI normalization, package parsing |
| `WindowMatchResolver.cs` | 530 | Internal deterministic scoring, evidence, thresholds, ambiguity, learned hints |
| `WindowMatcher.cs` | 42 | Stable public matching façade |

`ProcessIdentityNormalizer` remains the sole canonical process-key implementation. Browser support,
launch, readiness, and connector capability lists remain separate policies because they answer
different questions.

## 6.3 restore-planning ownership

The public `RestorePlanner` remains the pure deterministic composer and has fallen from 1,287 to
440 lines. Its independently changing policies now belong to:

| Component | Lines | Responsibility |
|---|---:|---|
| `RestoreAssignmentPlanner.cs` | 106 | Session-wide unique HWND assignment, candidate projection, ambiguity protection |
| `RestoreApprovalProjector.cs` | 218 | Disabled-entry and explicit ambiguity-choice projection |
| `RestoreLaunchPlanner.cs` | 507 | Resource availability and packaged/PWA/browser/file/application launch decisions |
| `RestorePlacementGeometry.cs` | 203 | Deterministic topology mapping, DPI adaptation, semantic placement, clamping |

Handle consumption still occurs only after an unambiguous selection. All candidates within an
unresolved ambiguity margin remain protected, and excluded/cancelled entries do not consume live
handles. Approval still projects the immutable preview without re-observation or rescoring.

## 6.4 ordered execution phases

The 1,314-line executor implementation is now a 192-line public construction/sequencing façade.
Detailed work is owned by internal objects that share one session context and revalidator:

| Component | Lines | Responsibility |
|---|---:|---|
| `RestoreExecutionContext.cs` | 224 | Shared entry/action/assignment state, common projections, HWND revalidation |
| `RestorePreflightPhase.cs` | 157 | Candidate/resource/browser staleness before mutation |
| `RestoreBrowserAndLaunchPhase.cs` | 266 | Browser action, existing-window placement, launches, final minimization |
| `RestoreReadinessPhase.cs` | 375 | Related-activity correlation, shared observations, bounded readiness polling |
| `RestorePlacementVerificationPhase.cs` | 275 | Re-observation, tolerance, correction attempts, cancellation |
| `RestoreResultAggregator.cs` | 45 | Stable ordered public execution results |

The phase order, cancellation boundaries, browser fallback condition, native re-observation,
one-HWND assignment set, and retry limits are unchanged.

## 6.5 Settings feature ownership

`SettingsWindow.xaml` was deliberately retained. Its code-behind is now one WPF partial class split
into a 127-line root plus startup (94), hotkey (155), monitor (109), and workspace-list (375) files.
All XAML event names, generated fields, dispatcher focus behavior, owners, theme resources, and
save-workflow routing remain unchanged.

## 6.6 convergence

The superseded `WindowRestorePlanner` and legacy `RestoreSessionContext`/result projection were
removed after their useful tests moved to the active assignment, execution-context, revalidation,
and result boundaries. The synchronous capture and unused void restore wrappers, two internal
forwarders, and two declaration-only public forwarders were also removed. All retained public
service/planner/matcher entry points have production callers or an active compatibility contract.

## Verification

- Release tests: **203 passed, 0 failed, 0 skipped**.
- Analyzer-enabled Release build after 6.6: **0 warnings, 0 errors**.
- Validation executable: `publish/cleanup-phase-6-final/WindowAnchor.exe`.
- Executable size: 79,492,993 bytes.
- SHA-256: `A1561D04A936EE9C00C07ED25FEE8372E1829A7C74B858088F98DD6097DFF13E`.
- Artifact shape: ReadyToRun, compressed, self-contained, single-file, win-x64.

The artifact is the final ignored Phase 6 validation build, not a release. Real desktop-window,
monitor, Settings/tray, DPI, and installed-browser smoke testing remains a manual gate.

## Remaining Phase 6 work

Complete the physical UI/native/browser matrix. Automated tests deliberately cannot validate real
monitor disconnection, installed browser connectors, or mouse/keyboard focus at multiple Windows
scaling factors.
