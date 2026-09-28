using System;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Recognizes a settled display layout leaving and subsequently returning. It deliberately has no
/// timer, window, or restore side effects: callers must first pass display-topology stabilization,
/// then decide whether to offer or perform recovery.
/// </summary>
internal sealed class TemporaryDisplayRecoveryTracker
{
    private DisplayTopologySnapshot? _lastStableTopology;
    private TemporaryDisplayRecoveryCandidate? _excursion;

    internal void Seed(DisplayTopologySnapshot topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        _lastStableTopology = topology;
        _excursion = null;
    }

    internal TemporaryDisplayRecoveryObservation Observe(
        DisplayTopologySnapshot current,
        Func<string, WorkspaceSnapshot?> findWorkspaceByFingerprint)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(findWorkspaceByFingerprint);

        DisplayTopologySnapshot? previous = _lastStableTopology;
        _lastStableTopology = current;
        if (previous is null ||
            string.Equals(previous.Signature, current.Signature, StringComparison.Ordinal))
        {
            return TemporaryDisplayRecoveryObservation.None;
        }

        if (_excursion is { } excursion &&
            string.Equals(excursion.Origin.Signature, current.Signature, StringComparison.Ordinal))
        {
            _excursion = null;
            return TemporaryDisplayRecoveryObservation.Returned(excursion.Workspace);
        }

        if (_excursion is null)
        {
            WorkspaceSnapshot? workspace = findWorkspaceByFingerprint(previous.Fingerprint);
            if (workspace is not null)
                _excursion = new TemporaryDisplayRecoveryCandidate(previous, workspace);
        }

        return TemporaryDisplayRecoveryObservation.Departed;
    }
}

internal sealed record TemporaryDisplayRecoveryCandidate(
    DisplayTopologySnapshot Origin,
    WorkspaceSnapshot Workspace);

internal sealed record TemporaryDisplayRecoveryObservation(
    bool DepartureObserved,
    WorkspaceSnapshot? ReturnedWorkspace)
{
    internal static TemporaryDisplayRecoveryObservation None { get; } = new(false, null);
    internal static TemporaryDisplayRecoveryObservation Departed { get; } = new(true, null);

    internal static TemporaryDisplayRecoveryObservation Returned(WorkspaceSnapshot workspace) =>
        new(false, workspace);
}
