using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class RestorePreviewPolicyTests
{
    [Fact]
    public void Enabled_preference_always_shows_preview()
    {
        Assert.True(RestorePreviewPolicy.ShouldShow(ExecutablePlan(), previewEnabled: true));
    }

    [Fact]
    public void Disabled_preference_skips_routine_executable_preview()
    {
        Assert.False(RestorePreviewPolicy.ShouldShow(ExecutablePlan(), previewEnabled: false));
    }

    [Fact]
    public void Disabled_preference_still_shows_non_executable_plan_for_user_resolution()
    {
        RestorePlan plan = ExecutablePlan() with
        {
            BlockingErrors =
            [
                new RestorePlanIssue(
                    RestorePlanIssueCode.AmbiguousMatch,
                    RestorePlanIssueSeverity.BlockingError,
                    "A user choice is required.")
            ]
        };

        Assert.True(RestorePreviewPolicy.ShouldShow(plan, previewEnabled: false));
    }

    [Fact]
    public void Disabled_preference_still_shows_ambiguity_blocked_entry_without_global_error()
    {
        RestorePlan plan = ExecutablePlan() with
        {
            Entries =
            [
                new RestorePlanEntry(
                    0,
                    "entry",
                    RestorePlanEntryOutcome.Blocked,
                    "An ambiguous candidate requires selection.",
                    new SavedWindowIdentity(),
                    Array.Empty<RestorePlanCandidate>(),
                    null,
                    new RestoreTargetPlacement(
                        "monitor",
                        0,
                        RestoreMonitorMappingKind.ExactStableId,
                        0,
                        0,
                        800,
                        600,
                        1,
                        96,
                        96,
                        false),
                    RestoreLaunchRequirement.None("Awaiting a choice."),
                    Array.Empty<RestoreAction>(),
                    Array.Empty<RestorePlanIssue>(),
                    Array.Empty<RestorePlanIssue>())
            ]
        };

        Assert.True(RestorePreviewPolicy.ShouldShow(plan, previewEnabled: false));
    }

    private static RestorePlan ExecutablePlan() => new()
    {
        WorkspaceId = "workspace",
        Actions =
        [
            new RestoreAction(
                0,
                RestoreActionKind.LaunchApplication,
                null,
                @"C:\Apps\editor.exe",
                "",
                false,
                null,
                "Launch")
        ]
    };
}
