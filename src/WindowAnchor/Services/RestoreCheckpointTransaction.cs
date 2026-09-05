using System;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Outcome of the durability gate that precedes a restore mutation.</summary>
public enum RestoreCheckpointStatus
{
    Created,
    Failed,
    Cancelled
}

/// <summary>Structured checkpoint outcome attached to a restore execution result.</summary>
public sealed record RestoreCheckpointOutcome(
    RestoreCheckpointStatus Status,
    WorkspaceCheckpointTrigger Trigger,
    string? CheckpointId,
    DateTime? CreatedAtUtc,
    string Explanation)
{
    public bool IsCreated => Status == RestoreCheckpointStatus.Created;
}

/// <summary>Internal result of running one operation behind the checkpoint transaction gate.</summary>
internal sealed record CheckpointedOperationResult<T>(
    RestoreCheckpointOutcome Checkpoint,
    bool OperationStarted,
    T? Value);
