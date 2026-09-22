using System;
using System.Linq;

namespace WindowAnchor.Services;

/// <summary>Publishes redacted diagnostics and merges externally observed restore timing.</summary>
internal sealed class WorkspaceRestoreDiagnosticsService
{
    internal RestoreExecutionResult Publish(RestorePlan plan, RestoreExecutionResult result)
    {
        RestoreDiagnosticsReport report = RestoreDiagnosticsReportBuilder.Build(plan, result);
        RestoreDiagnosticsReportStore.Record(report);
        AppLogger.Info(
            "restore.diagnostics_report_ready",
            "Prepared a redacted restore diagnostics report",
            LogField.Public("status", result.Status),
            LogField.Public("entryCount", result.Entries.Count),
            LogField.Public("failedActionCount", report.Summary.FailedActionCount),
            LogField.Public("staleActionCount", report.Summary.StaleActionCount));
        return result;
    }

    internal RestoreExecutionResult AddCheckpointTiming(RestoreExecutionResult result)
    {
        if (result.Checkpoint?.Duration is not { } duration || duration <= TimeSpan.Zero)
            return result;
        return AddTiming(result, RestoreProgressStage.PreparingCheckpoint, duration);
    }

    internal RestoreExecutionResult AddExternalStageTiming(
        RestorePlan plan,
        RestoreExecutionResult result,
        RestoreProgressStage stage,
        TimeSpan duration) => duration <= TimeSpan.Zero
            ? result
            : Publish(plan, AddTiming(result, stage, duration));

    private static RestoreExecutionResult AddTiming(
        RestoreExecutionResult result,
        RestoreProgressStage stage,
        TimeSpan duration)
    {
        RestoreExecutionTiming timing = result.Timing;
        RestoreExecutionStageTiming[] stages = timing.Stages
            .Append(new RestoreExecutionStageTiming(stage, duration))
            .GroupBy(item => item.Stage)
            .OrderBy(group => group.Key)
            .Select(group => new RestoreExecutionStageTiming(
                group.Key,
                group.Aggregate(TimeSpan.Zero, (total, item) => total + item.Duration)))
            .ToArray();
        return result with
        {
            Timing = new RestoreExecutionTiming(timing.TotalDuration + duration, stages)
        };
    }
}
