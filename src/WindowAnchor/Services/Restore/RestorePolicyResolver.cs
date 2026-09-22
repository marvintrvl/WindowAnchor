using System;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Fully resolved behavior for one entry after applying its workspace-mode default.</summary>
public sealed record ResolvedEntryRestorePolicy(
    EntryRestorePolicy Source,
    bool ReuseExisting,
    bool LaunchIfMissing,
    bool PreferFreshInstance,
    bool RepairOnly,
    bool NeverClose,
    bool IgnoreDuringSwitch)
{
    public static ResolvedEntryRestorePolicy ResumeDefault { get; } = new(
        EntryRestorePolicy.WorkspaceDefault,
        ReuseExisting: true,
        LaunchIfMissing: true,
        PreferFreshInstance: false,
        RepairOnly: false,
        NeverClose: false,
        IgnoreDuringSwitch: false);
}

/// <summary>Pure composition of workspace restore mode and per-entry override.</summary>
internal static class RestorePolicyResolver
{
    internal static ResolvedEntryRestorePolicy Resolve(
        RestoreModeKind mode,
        EntryRestorePolicy entryPolicy)
    {
        ResolvedEntryRestorePolicy workspace = mode switch
        {
            RestoreModeKind.Repair => ResumeDefault(
                launchIfMissing: false,
                repairOnly: true),
            RestoreModeKind.MoveExisting => ResumeDefault(launchIfMissing: false),
            RestoreModeKind.LaunchFresh => ResumeDefault(
                reuseExisting: false,
                preferFresh: true),
            _ => ResolvedEntryRestorePolicy.ResumeDefault
        };

        return entryPolicy switch
        {
            EntryRestorePolicy.ReuseExisting => workspace with
            {
                Source = entryPolicy,
                ReuseExisting = true,
                PreferFreshInstance = false
            },
            EntryRestorePolicy.LaunchIfMissing => workspace with
            {
                Source = entryPolicy,
                ReuseExisting = true,
                LaunchIfMissing = true,
                PreferFreshInstance = false,
                RepairOnly = false
            },
            EntryRestorePolicy.AlwaysLaunchNew => workspace with
            {
                Source = entryPolicy,
                ReuseExisting = false,
                LaunchIfMissing = true,
                PreferFreshInstance = true,
                RepairOnly = false
            },
            EntryRestorePolicy.NeverLaunch => workspace with
            {
                Source = entryPolicy,
                ReuseExisting = true,
                LaunchIfMissing = false,
                PreferFreshInstance = false
            },
            EntryRestorePolicy.NeverClose => workspace with
            {
                Source = entryPolicy,
                NeverClose = true
            },
            EntryRestorePolicy.IgnoreDuringSwitch => workspace with
            {
                Source = entryPolicy,
                NeverClose = true,
                IgnoreDuringSwitch = true
            },
            _ => workspace
        };
    }

    internal static string Label(ResolvedEntryRestorePolicy policy) => policy.Source switch
    {
        EntryRestorePolicy.WorkspaceDefault => "Workspace mode default",
        EntryRestorePolicy.ReuseExisting => "Reuse existing",
        EntryRestorePolicy.LaunchIfMissing => "Launch if missing",
        EntryRestorePolicy.AlwaysLaunchNew => "Always launch new",
        EntryRestorePolicy.NeverLaunch => "Never launch",
        EntryRestorePolicy.NeverClose => "Never close",
        EntryRestorePolicy.IgnoreDuringSwitch => "Ignore during switch",
        _ => policy.Source.ToString()
    };

    internal static bool IsSwitch(RestoreModeKind mode) => mode == RestoreModeKind.ExactSwitch;

    private static ResolvedEntryRestorePolicy ResumeDefault(
        bool reuseExisting = true,
        bool launchIfMissing = true,
        bool preferFresh = false,
        bool repairOnly = false) => new(
            EntryRestorePolicy.WorkspaceDefault,
            reuseExisting,
            launchIfMissing,
            preferFresh,
            repairOnly,
            NeverClose: false,
            IgnoreDuringSwitch: false);
}
