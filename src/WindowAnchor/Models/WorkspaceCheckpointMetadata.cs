using System;

namespace WindowAnchor.Models;

/// <summary>Operation that caused an automatic recovery checkpoint to be captured.</summary>
public enum WorkspaceCheckpointTrigger
{
    ManualCapture,
    Restore,
    SelectiveRestore,
    AlignAndMinimize,
    AdaptiveRestore,
    WorkspaceSwitch,
    AutomaticDisplayRestore,
    Undo
}

/// <summary>
/// Versioned recovery-only metadata stored alongside a complete workspace-shaped capture.
/// Runtime handles, process IDs, screenshots, and document contents are deliberately absent.
/// </summary>
public sealed class WorkspaceCheckpointMetadata
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string CheckpointId { get; set; } = "";
    public WorkspaceCheckpointTrigger Trigger { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string TargetWorkspaceId { get; set; } = "";
    public string SourceMonitorFingerprint { get; set; } = "";
}
