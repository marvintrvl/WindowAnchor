using System;
using System.Collections.Generic;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Bounded checkpoint-history policy.</summary>
public sealed record CheckpointRetentionPolicy
{
    public int MaximumCount { get; init; } = 10;
    public TimeSpan MaximumAge { get; init; } = TimeSpan.FromDays(7);
}

/// <summary>Clock boundary used by deterministic retention and transaction tests.</summary>
public interface ICheckpointClock
{
    DateTime UtcNow { get; }
}

internal sealed class SystemCheckpointClock : ICheckpointClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Durable identity returned after a checkpoint document is committed.</summary>
public sealed record CheckpointSaveReceipt(
    string CheckpointId,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    WorkspaceCheckpointTrigger Trigger,
    string TargetWorkspaceId);

internal sealed record CheckpointIndexEntry(
    string CheckpointId,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    WorkspaceCheckpointTrigger Trigger,
    string TargetWorkspaceId,
    string SourceMonitorFingerprint,
    int EntryCount);

internal sealed record CheckpointIndexDocument
{
    internal const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public DateTime UpdatedAtUtc { get; init; }
    public IReadOnlyList<CheckpointIndexEntry> Checkpoints { get; init; } =
        Array.Empty<CheckpointIndexEntry>();
    public int IsolatedFailureCount { get; init; }
}
