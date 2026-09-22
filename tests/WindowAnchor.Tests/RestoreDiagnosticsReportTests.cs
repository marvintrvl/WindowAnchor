using System.Text.Json;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class RestoreDiagnosticsReportTests
{
    [Fact]
    public void Redacted_report_preserves_typed_failure_facts_without_private_content()
    {
        const string privatePath = @"C:\Users\alice\Documents\Merger\secret-plan.xlsx";
        const string privateUrl = "https://bank.example/private/account?token=browser-secret";
        const string privateTitle = "Alice account balance";
        const string token = "top-secret-value";
        RestoreAction launch = new(
            0,
            RestoreActionKind.LaunchApplication,
            null,
            privatePath,
            $"--token {token}",
            false,
            Placement(),
            $"Could not launch {privatePath} with token={token}",
            LogSensitivity.Path,
            LogSensitivity.CommandLine);
        RestorePlanEntry entry = new(
            0,
            "entry-private",
            RestorePlanEntryOutcome.LaunchRequired,
            $"Plan for {privateTitle}",
            new SavedWindowIdentity
            {
                EntryId = "entry-private",
                ProcessName = "spreadsheet",
                ExecutablePath = privatePath,
                Title = privateTitle,
                BrowserUrl = privateUrl
            },
            [],
            null,
            Placement(),
            new RestoreLaunchRequirement(
                true,
                RestoreLaunchKind.Application,
                privatePath,
                $"--token {token}",
                false,
                RestoreResourceAvailability.Available,
                "Launch application.",
                LogSensitivity.Path,
                LogSensitivity.CommandLine),
            [launch],
            [new RestorePlanIssue(
                RestorePlanIssueCode.ResourceAvailabilityUnknown,
                RestorePlanIssueSeverity.Warning,
                $"Could not check {privatePath}")],
            [new RestorePlanIssue(
                RestorePlanIssueCode.MissingResource,
                RestorePlanIssueSeverity.BlockingError,
                $"Missing {privatePath}")]);
        var plan = new RestorePlan
        {
            WorkspaceId = "workspace-private",
            WorkspaceName = "Alice private workspace",
            SnapshotSavedAt = new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc),
            Mode = RestoreModeKind.Repair,
            SelectedMonitorIds = ["monitor-private"],
            Entries = [entry],
            Actions = [launch]
        };
        var result = new RestoreExecutionResult(
            plan.WorkspaceId,
            RestoreExecutionStatus.CompletedWithFailures,
            false,
            [new RestoreExecutionEntryResult(
                0,
                entry.EntryId,
                RestoreExecutionEntryStatus.Failed,
                null,
                $"IOException: could not open {privatePath} at {privateUrl}; token={token}",
                AppReadinessState.TimedOut,
                "polling")],
            [new RestoreExecutionActionResult(
                0,
                0,
                RestoreActionKind.LaunchApplication,
                RestoreExecutionActionStatus.Failed,
                null,
                null,
                $"IOException: {privatePath}; token={token}")],
            new HashSet<long>())
        {
            Checkpoint = new RestoreCheckpointOutcome(
                RestoreCheckpointStatus.Failed,
                WorkspaceCheckpointTrigger.Restore,
                "checkpoint-private",
                null,
                $"IOException: {privatePath}; token={token}",
                TimeSpan.FromSeconds(2)),
            Timing = new RestoreExecutionTiming(
                TimeSpan.FromSeconds(3),
                [new RestoreExecutionStageTiming(
                    RestoreProgressStage.WaitingForApplications,
                    TimeSpan.FromSeconds(1))])
        };

        RestoreDiagnosticsReport report = RestoreDiagnosticsReportBuilder.Build(
            plan,
            result,
            DateTime.UnixEpoch);
        string json = report.ToRedactedJson();

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(RestoreExecutionStatus.CompletedWithFailures, report.Summary.Status);
        Assert.Equal(1, report.Summary.EntriesNeedingAttention);
        Assert.Equal("failed", Assert.Single(report.Entries).LaunchResult);
        Assert.Contains(RestorePlanIssueCode.MissingResource, Assert.Single(report.Entries).ErrorCodes);
        Assert.Contains(report.StageTimings, timing =>
            timing.Stage == RestoreProgressStage.PreparingCheckpoint &&
            timing.Duration == TimeSpan.FromSeconds(2));
        Assert.Contains(report.StageTimings, timing =>
            timing.Stage == RestoreProgressStage.WaitingForApplications &&
            timing.Duration == TimeSpan.FromSeconds(1));
        Assert.Contains("id#", json);
        Assert.Contains("<path:redacted.xlsx>", json);
        Assert.Contains("<title:redacted>", json);
        Assert.Contains("https://bank.example/<redacted>", json);
        Assert.DoesNotContain("Alice private workspace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(privatePath, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(privateTitle, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private/account", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(token, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checkpoint-private", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Report_serialization_is_deterministic_for_a_simulated_result()
    {
        RestoreAction action = new(
            0,
            RestoreActionKind.RestoreExistingWindow,
            41,
            "",
            "",
            false,
            Placement(),
            "Restore existing window.");
        RestorePlanEntry entry = new(
            0,
            "entry-1",
            RestorePlanEntryOutcome.Matched,
            "Matched.",
            new SavedWindowIdentity { EntryId = "entry-1", ProcessName = "editor" },
            [],
            null,
            Placement(),
            RestoreLaunchRequirement.None("No launch."),
            [action],
            [],
            []);
        var plan = new RestorePlan
        {
            WorkspaceId = "workspace-1",
            Mode = RestoreModeKind.Resume,
            Entries = [entry],
            Actions = [action]
        };
        var result = new RestoreExecutionResult(
            plan.WorkspaceId,
            RestoreExecutionStatus.Completed,
            false,
            [new RestoreExecutionEntryResult(
                0,
                entry.EntryId,
                RestoreExecutionEntryStatus.Restored,
                41,
                "Restored.")],
            [new RestoreExecutionActionResult(
                0,
                0,
                RestoreActionKind.RestoreExistingWindow,
                RestoreExecutionActionStatus.Succeeded,
                null,
                41,
                "Restored.")],
            new HashSet<long> { 41 })
        {
            Timing = new RestoreExecutionTiming(
                TimeSpan.FromMilliseconds(50),
                [new RestoreExecutionStageTiming(
                    RestoreProgressStage.VerifyingPlacements,
                    TimeSpan.FromMilliseconds(10))])
        };

        string first = RestoreDiagnosticsReportBuilder.Build(plan, result, DateTime.UnixEpoch)
            .ToRedactedJson();
        string second = RestoreDiagnosticsReportBuilder.Build(plan, result, DateTime.UnixEpoch)
            .ToRedactedJson();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Presentation_keeps_success_quiet_and_surfaces_partial_results()
    {
        RestoreExecutionResult completed = Result(RestoreExecutionStatus.Completed);
        RestoreExecutionResult partial = Result(RestoreExecutionStatus.CompletedWithFailures);
        RestoreExecutionResult stale = Result(RestoreExecutionStatus.StalePlan);

        Assert.False(RestoreDiagnosticsPresentationPolicy.ShouldShowFailureSummary(completed));
        Assert.True(RestoreDiagnosticsPresentationPolicy.ShouldShowFailureSummary(partial));
        Assert.True(RestoreDiagnosticsPresentationPolicy.ShouldShowFailureSummary(stale));
    }

    private static RestoreExecutionResult Result(RestoreExecutionStatus status) => new(
        "workspace",
        status,
        false,
        [],
        [],
        new HashSet<long>());

    private static RestoreTargetPlacement Placement() => new(
        "primary",
        0,
        RestoreMonitorMappingKind.ExactStableId,
        0,
        0,
        800,
        600,
        1,
        96,
        96,
        false);
}
