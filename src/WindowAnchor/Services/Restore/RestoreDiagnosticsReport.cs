using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Shareable, privacy-safe projection of one immutable restore plan and its execution result.
/// It never performs discovery, revalidation, mutation, or additional waiting.
/// </summary>
public sealed record RestoreDiagnosticsReport
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public DateTime GeneratedAtUtc { get; init; }
    public RestoreDiagnosticsSummary Summary { get; init; } = new();
    public RestoreDiagnosticsPlan Plan { get; init; } = new();
    public RestoreDiagnosticsCheckpoint? Checkpoint { get; init; }
    public IReadOnlyList<RestoreDiagnosticsStageTiming> StageTimings { get; init; } =
        Array.Empty<RestoreDiagnosticsStageTiming>();
    public IReadOnlyList<RestoreDiagnosticsEntry> Entries { get; init; } =
        Array.Empty<RestoreDiagnosticsEntry>();
    public IReadOnlyList<RestoreDiagnosticsAction> Actions { get; init; } =
        Array.Empty<RestoreDiagnosticsAction>();

    /// <summary>Returns only the redacted diagnostic artifact suitable for clipboard sharing.</summary>
    public string ToRedactedJson(bool writeIndented = true)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Serialize(this, options);
    }
}

public sealed record RestoreDiagnosticsSummary(
    RestoreExecutionStatus Status = RestoreExecutionStatus.Completed,
    bool WasCancelled = false,
    int EntryCount = 0,
    int EntriesNeedingAttention = 0,
    int FailedActionCount = 0,
    int StaleActionCount = 0,
    int WarningCount = 0,
    string Message = "");

public sealed record RestoreDiagnosticsPlan(
    string WorkspaceId = "",
    string WorkspaceName = "",
    DateTime SnapshotSavedAt = default,
    RestoreModeKind Mode = RestoreModeKind.Resume,
    IReadOnlyList<string>? SelectedMonitorIds = null);

public sealed record RestoreDiagnosticsCheckpoint(
    RestoreCheckpointStatus Status,
    WorkspaceCheckpointTrigger Trigger,
    string? CheckpointId,
    DateTime? CreatedAtUtc,
    TimeSpan? Duration,
    string Message);

public sealed record RestoreDiagnosticsStageTiming(
    RestoreProgressStage Stage,
    TimeSpan Duration);

public sealed record RestoreDiagnosticsIdentity(
    string EntryId,
    string ProcessName,
    string ExecutablePath,
    string WindowTitle,
    string BrowserUrl,
    string? SelectedCandidateId,
    WindowMatchConfidence? MatchConfidence);

public sealed record RestoreDiagnosticsPlacement(
    string TargetMonitorId,
    RestoreMonitorMappingKind MonitorMapping,
    RestorePlacementStrategy Strategy,
    bool WasDpiScaled,
    bool WasClamped,
    WindowPlacementVerificationState? Verification,
    int RetryCount,
    string? VerificationStrategy,
    int? TolerancePixels);

public sealed record RestoreDiagnosticsEntry(
    int EntryIndex,
    string EntryId,
    RestorePlanEntryOutcome PlannedOutcome,
    RestoreExecutionEntryStatus FinalStatus,
    RestoreDiagnosticsIdentity SelectedIdentity,
    RestoreDiagnosticsPlacement Placement,
    string LaunchResult,
    AppReadinessState? ReadinessState,
    string? ReadinessStrategy,
    IReadOnlyList<RestorePlanIssueCode> WarningCodes,
    IReadOnlyList<RestorePlanIssueCode> ErrorCodes,
    IReadOnlyList<string> DiagnosticCodes,
    string Message);

public sealed record RestoreDiagnosticsAction(
    int ActionIndex,
    int? EntryIndex,
    RestoreActionKind Kind,
    RestoreExecutionActionStatus Status,
    string Target,
    string Arguments,
    string? WindowHandleId,
    RestorePlanStaleReason? StaleReason,
    AppReadinessState? ReadinessState,
    WindowPlacementVerificationState? PlacementVerification,
    int PlacementRetryCount,
    string? PlacementVerificationStrategy,
    int? PlacementTolerancePixels,
    string Message);

/// <summary>Builds the one report format used by the UI, diagnostics export, and tests.</summary>
public static class RestoreDiagnosticsReportBuilder
{
    public static RestoreDiagnosticsReport Build(
        RestorePlan plan,
        RestoreExecutionResult result,
        DateTime? generatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(result);

        IReadOnlyDictionary<int, RestorePlanEntry> planEntries = plan.Entries
            .ToDictionary(entry => entry.EntryIndex);
        RestoreDiagnosticsEntry[] entries = result.Entries
            .OrderBy(entry => entry.EntryIndex)
            .Select(entry => BuildEntry(entry, planEntries, result.Actions))
            .ToArray();
        RestoreDiagnosticsAction[] actions = result.Actions
            .OrderBy(action => action.ActionIndex)
            .Select(action => BuildAction(action, plan))
            .ToArray();
        RestoreDiagnosticsStageTiming[] timings = BuildTimings(result);
        int failedActions = result.Actions.Count(action =>
            action.Status == RestoreExecutionActionStatus.Failed);
        int staleActions = result.Actions.Count(action =>
            action.Status == RestoreExecutionActionStatus.Stale);
        int entriesNeedingAttention = result.Entries.Count(NeedsAttention);
        int warningCount = plan.Warnings.Count + plan.Entries.Sum(entry => entry.Warnings.Count);

        return new RestoreDiagnosticsReport
        {
            GeneratedAtUtc = (generatedAtUtc ?? DateTime.UtcNow).ToUniversalTime(),
            Summary = new RestoreDiagnosticsSummary(
                result.Status,
                result.WasCancelled,
                result.Entries.Count,
                entriesNeedingAttention,
                failedActions,
                staleActions,
                warningCount,
                SummaryMessage(result, entriesNeedingAttention, failedActions, staleActions)),
            Plan = new RestoreDiagnosticsPlan(
                Identifier(plan.WorkspaceId),
                LogRedactor.RedactValue(
                    plan.WorkspaceName,
                    LogSensitivity.WorkspaceName,
                    LogRedactionMode.Redacted),
                plan.SnapshotSavedAt,
                plan.Mode,
                plan.SelectedMonitorIds.Select(Identifier).ToArray()),
            Checkpoint = BuildCheckpoint(result.Checkpoint),
            StageTimings = timings,
            Entries = entries,
            Actions = actions
        };
    }

    private static RestoreDiagnosticsEntry BuildEntry(
        RestoreExecutionEntryResult result,
        IReadOnlyDictionary<int, RestorePlanEntry> planEntries,
        IReadOnlyList<RestoreExecutionActionResult> actionResults)
    {
        planEntries.TryGetValue(result.EntryIndex, out RestorePlanEntry? planEntry);
        SavedWindowIdentity saved = planEntry?.SavedIdentity ?? new SavedWindowIdentity();
        RestorePlanCandidate? selected = planEntry?.SelectedMatch;
        RestoreTargetPlacement placement = planEntry?.TargetPlacement ?? new RestoreTargetPlacement(
            "", 0, RestoreMonitorMappingKind.Unavailable, 0, 0, 0, 0, 0, 96, 96, false,
            RestorePlacementStrategy.Unavailable);
        RestoreExecutionActionResult[] launches = actionResults
            .Where(action => action.EntryIndex == result.EntryIndex && IsLaunch(action.Kind))
            .ToArray();
        RestorePlanIssueCode[] warningCodes = planEntry?.Warnings
            .Select(issue => issue.Code)
            .Distinct()
            .ToArray() ?? Array.Empty<RestorePlanIssueCode>();
        RestorePlanIssueCode[] errorCodes = planEntry?.BlockingErrors
            .Select(issue => issue.Code)
            .Distinct()
            .ToArray() ?? Array.Empty<RestorePlanIssueCode>();

        return new RestoreDiagnosticsEntry(
            result.EntryIndex,
            Identifier(result.EntryId),
            planEntry?.Outcome ?? RestorePlanEntryOutcome.Blocked,
            result.Status,
            new RestoreDiagnosticsIdentity(
                Identifier(result.EntryId),
                LogRedactor.ScrubSecrets(saved.ProcessName),
                LogRedactor.RedactValue(
                    saved.ExecutablePath,
                    LogSensitivity.Path,
                    LogRedactionMode.Redacted),
                LogRedactor.RedactValue(
                    saved.Title,
                    LogSensitivity.Title,
                    LogRedactionMode.Redacted),
                LogRedactor.RedactUrl(saved.BrowserUrl),
                selected is null ? null : Identifier($"{selected.WindowHandle}:{selected.ProcessId}"),
                selected?.Confidence),
            new RestoreDiagnosticsPlacement(
                Identifier(placement.TargetMonitorId),
                placement.MonitorMapping,
                placement.Strategy,
                placement.WasDpiScaled,
                placement.WasClamped,
                result.PlacementVerification,
                result.PlacementRetryCount,
                result.PlacementVerificationStrategy,
                result.PlacementTolerancePixels),
            LaunchResult(launches),
            result.ReadinessState,
            RedactMessage(result.ReadinessStrategy),
            warningCodes,
            errorCodes,
            DiagnosticCodes(result, launches),
            RedactMessage(result.Explanation));
    }

    private static RestoreDiagnosticsAction BuildAction(
        RestoreExecutionActionResult result,
        RestorePlan plan)
    {
        RestoreAction? action = result.ActionIndex >= 0 && result.ActionIndex < plan.Actions.Count
            ? plan.Actions[result.ActionIndex]
            : null;
        return new RestoreDiagnosticsAction(
            result.ActionIndex,
            result.EntryIndex,
            result.Kind,
            result.Status,
            action is null
                ? ""
                : LogRedactor.RedactValue(
                    action.Target,
                    action.TargetSensitivity,
                    LogRedactionMode.Redacted),
            action is null
                ? ""
                : LogRedactor.RedactValue(
                    action.Arguments,
                    action.ArgumentsSensitivity,
                    LogRedactionMode.Redacted),
            result.WindowHandle is null ? null : Identifier(result.WindowHandle.Value.ToString()),
            result.StaleReason,
            result.ReadinessState,
            result.PlacementVerification,
            result.PlacementRetryCount,
            RedactMessage(result.PlacementVerificationStrategy),
            result.PlacementTolerancePixels,
            RedactMessage(result.Explanation));
    }

    private static RestoreDiagnosticsCheckpoint? BuildCheckpoint(
        RestoreCheckpointOutcome? checkpoint) => checkpoint is null
            ? null
            : new RestoreDiagnosticsCheckpoint(
                checkpoint.Status,
                checkpoint.Trigger,
                checkpoint.CheckpointId is null ? null : Identifier(checkpoint.CheckpointId),
                checkpoint.CreatedAtUtc,
                checkpoint.Duration,
                RedactMessage(checkpoint.Explanation));

    private static RestoreDiagnosticsStageTiming[] BuildTimings(RestoreExecutionResult result)
    {
        IEnumerable<RestoreExecutionStageTiming> timings = result.Timing.Stages;
        if (result.Checkpoint?.Duration is { } checkpointDuration &&
            !timings.Any(timing => timing.Stage == RestoreProgressStage.PreparingCheckpoint))
        {
            timings = timings.Append(new RestoreExecutionStageTiming(
                RestoreProgressStage.PreparingCheckpoint,
                checkpointDuration));
        }

        return timings
            .GroupBy(timing => timing.Stage)
            .OrderBy(group => group.Key)
            .Select(group => new RestoreDiagnosticsStageTiming(
                group.Key,
                group.Aggregate(TimeSpan.Zero, (total, item) => total + item.Duration)))
            .ToArray();
    }

    private static string LaunchResult(IReadOnlyList<RestoreExecutionActionResult> launches)
    {
        if (launches.Count == 0)
            return "not_required";
        if (launches.Any(action => action.Status == RestoreExecutionActionStatus.Failed))
            return "failed";
        if (launches.Any(action => action.Status == RestoreExecutionActionStatus.Cancelled))
            return "cancelled";
        if (launches.Any(action => action.Status == RestoreExecutionActionStatus.Stale))
            return "stale";
        if (launches.Any(action => action.Status == RestoreExecutionActionStatus.Succeeded))
            return "succeeded";
        return "skipped";
    }

    private static IReadOnlyList<string> DiagnosticCodes(
        RestoreExecutionEntryResult result,
        IReadOnlyList<RestoreExecutionActionResult> launches)
    {
        var codes = new List<string> { $"entry_status:{result.Status}" };
        if (result.PlacementVerification is not null)
            codes.Add($"placement:{result.PlacementVerification}");
        if (result.ReadinessState is not null)
            codes.Add($"readiness:{result.ReadinessState}");
        foreach (RestoreExecutionActionResult launch in launches)
        {
            codes.Add($"launch:{launch.Status}");
            if (launch.StaleReason is not null)
                codes.Add($"stale:{launch.StaleReason}");
        }
        return codes.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool NeedsAttention(RestoreExecutionEntryResult entry) => entry.Status is
        RestoreExecutionEntryStatus.Blocked or
        RestoreExecutionEntryStatus.Cancelled or
        RestoreExecutionEntryStatus.Stale or
        RestoreExecutionEntryStatus.Failed;

    private static bool IsLaunch(RestoreActionKind kind) => kind is
        RestoreActionKind.LaunchApplication or
        RestoreActionKind.OpenResource or
        RestoreActionKind.LaunchDedicatedBrowser or
        RestoreActionKind.LaunchWebApp or
        RestoreActionKind.ActivatePackagedApplication;

    private static string SummaryMessage(
        RestoreExecutionResult result,
        int entriesNeedingAttention,
        int failedActions,
        int staleActions)
    {
        if (result.Status == RestoreExecutionStatus.Completed)
            return "Restore completed without reported failures.";
        return $"Restore status {result.Status}; {entriesNeedingAttention} entr" +
               $"{(entriesNeedingAttention == 1 ? "y" : "ies")} need attention, " +
               $"with {failedActions} failed and {staleActions} stale actions.";
    }

    private static string Identifier(string value) => LogRedactor.RedactValue(
        value,
        LogSensitivity.Identifier,
        LogRedactionMode.Redacted);

    private static string RedactMessage(string? value) => LogRedactor.RedactUnstructured(value);
}

/// <summary>Keeps routine success quiet while surfacing partial or failed manual results.</summary>
public static class RestoreDiagnosticsPresentationPolicy
{
    public static bool ShouldShowFailureSummary(RestoreExecutionResult? result) => result?.Status is
        RestoreExecutionStatus.StalePlan or
        RestoreExecutionStatus.Rejected or
        RestoreExecutionStatus.CompletedWithFailures;
}

/// <summary>Retains the latest immutable report so a quiet success can still be copied on demand.</summary>
internal static class RestoreDiagnosticsReportStore
{
    private static readonly object Sync = new();
    private static RestoreDiagnosticsReport? _latest;

    internal static bool HasLatest
    {
        get
        {
            lock (Sync)
                return _latest is not null;
        }
    }

    internal static void Record(RestoreDiagnosticsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (Sync)
            _latest = report;
    }

    internal static bool TryGetLatest(out RestoreDiagnosticsReport? report)
    {
        lock (Sync)
        {
            report = _latest;
            return report is not null;
        }
    }
}
