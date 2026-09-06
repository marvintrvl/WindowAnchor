namespace WindowAnchor.Models;

/// <summary>Explicit workspace-level behavior used when a restore plan is built.</summary>
public enum RestoreModeKind
{
    /// <summary>Reuse suitable windows and launch entries that are missing.</summary>
    Resume = 0,

    /// <summary>Compatibility name for the pre-WA-007 restore behavior.</summary>
    Standard = Resume,

    /// <summary>Only correct placement differences on windows that already exist.</summary>
    Repair = 1,

    /// <summary>Move matching live windows, but never launch missing applications.</summary>
    MoveExisting = 2,

    /// <summary>Prefer a distinct new instance when the entry has a supported launch contract.</summary>
    LaunchFresh = 3,

    /// <summary>Close unrelated context safely, then resume the target workspace.</summary>
    ExactSwitch = 4,

    /// <summary>Build and display the plan without executing it.</summary>
    PreviewOnly = 5,

    /// <summary>Compatibility mode that limits Resume behavior to selected monitors.</summary>
    Selective = 6,

    /// <summary>Compatibility mode that resumes the workspace and minimizes other windows.</summary>
    AlignAndMinimize = 7
}

/// <summary>
/// Optional override for one saved workspace entry. Policies are deliberately mutually exclusive;
/// workspace-wide composition remains owned by the selected <see cref="RestoreModeKind"/>.
/// </summary>
public enum EntryRestorePolicy
{
    /// <summary>Use the behavior selected by the workspace restore mode.</summary>
    WorkspaceDefault = 0,

    /// <summary>Prefer an existing matching window; missing-entry behavior remains mode-defined.</summary>
    ReuseExisting = 1,

    /// <summary>Reuse a match and launch the entry when no match exists.</summary>
    LaunchIfMissing = 2,

    /// <summary>Prefer a distinct new instance when the entry supports that contract.</summary>
    AlwaysLaunchNew = 3,

    /// <summary>Use an existing matching window and never launch this entry.</summary>
    NeverLaunch = 4,

    /// <summary>Apply the mode normally, but preserve matching windows during an exact switch.</summary>
    NeverClose = 5,

    /// <summary>Do not restore or close this entry during an exact switch.</summary>
    IgnoreDuringSwitch = 6
}
