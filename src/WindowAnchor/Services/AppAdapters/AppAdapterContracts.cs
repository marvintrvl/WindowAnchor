using System;
using System.Collections.Generic;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Capture inputs supplied to one application-family adapter.</summary>
internal sealed record AppAdapterCaptureContext(
    WindowRecord Window,
    bool SaveFiles,
    CaptureResourceSearchBudget FolderSearchBudget,
    IProgress<SaveProgressReport>? Progress,
    int ResourceProgressCurrent,
    int ResourceProgressTotal,
    bool BuildFullJumpListCache);

/// <summary>Facts available to an optional adapter launch-plan override.</summary>
internal sealed record AppAdapterLaunchContext(
    int EntryIndex,
    WorkspaceEntry Entry,
    bool HasSelectedMatch,
    bool CorrectResourceMatched,
    bool BrowserSessionScheduled,
    IReadOnlyList<RunningApplicationIdentity> RunningApplications,
    IReadOnlySet<string> PendingDocumentExecutables,
    IReadOnlyDictionary<(int EntryIndex, RestoreResourceKind Kind), RestoreResourceObservation> Resources,
    RestoreTargetPlacement Placement);

/// <summary>
/// Stable compiled-in extension contract for an application family. Adapters may decline an
/// operation, allowing the generic Windows policy to remain the conservative fallback.
/// </summary>
internal interface IAppAdapter
{
    string Name { get; }
    bool CanHandle(WindowRecord window);
    bool CanHandle(WorkspaceEntry entry);
    WorkspaceEntry? TryCapture(AppAdapterCaptureContext context);
    SavedWindowIdentity EnrichIdentity(WorkspaceEntry entry, SavedWindowIdentity identity);
    bool TryPlanLaunch(AppAdapterLaunchContext context, out RestoreLaunchDecision decision);
    IAppReadinessStrategy? ReadinessStrategy { get; }
    IWindowPlacementVerificationStrategy? PlacementVerificationStrategy { get; }
}
