using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Immutable restore-mode input. Selective restores retain excluded entries in the plan so every
/// saved entry has an explicit, inspectable outcome.
/// </summary>
public sealed record RestoreMode
{
    public RestoreModeKind Kind { get; init; } = RestoreModeKind.Resume;
    public IReadOnlyList<string> SelectedMonitorIds { get; init; } = Array.Empty<string>();
    public bool CancellationRequested { get; init; }

    public static RestoreMode Resume { get; } = new();

    public static RestoreMode Standard => Resume;

    public static RestoreMode Repair { get; } = new()
    {
        Kind = RestoreModeKind.Repair
    };

    public static RestoreMode MoveExisting { get; } = new()
    {
        Kind = RestoreModeKind.MoveExisting
    };

    public static RestoreMode LaunchFresh { get; } = new()
    {
        Kind = RestoreModeKind.LaunchFresh
    };

    public static RestoreMode ExactSwitch { get; } = new()
    {
        Kind = RestoreModeKind.ExactSwitch
    };

    public static RestoreMode PreviewOnly { get; } = new()
    {
        Kind = RestoreModeKind.PreviewOnly
    };

    public static RestoreMode AlignAndMinimize { get; } = new()
    {
        Kind = RestoreModeKind.AlignAndMinimize
    };

    public static RestoreMode Selective(params string[] monitorIds) => new()
    {
        Kind = RestoreModeKind.Selective,
        SelectedMonitorIds = (monitorIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray()
    };

    /// <summary>Creates a runtime mode from a persisted workspace default.</summary>
    public static RestoreMode FromWorkspace(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.DefaultRestoreMode switch
        {
            RestoreModeKind.Repair => Repair,
            RestoreModeKind.MoveExisting => MoveExisting,
            RestoreModeKind.LaunchFresh => LaunchFresh,
            RestoreModeKind.ExactSwitch => ExactSwitch,
            RestoreModeKind.PreviewOnly => PreviewOnly,
            _ => Resume
        };
    }
}

/// <summary>Availability of an external target observed before pure planning begins.</summary>
public enum RestoreResourceAvailability
{
    Unknown,
    Available,
    Missing,
    Stale
}

/// <summary>Kind of launch resource whose availability was observed.</summary>
public enum RestoreResourceKind
{
    Executable,
    LaunchTarget,
    WebAppShortcut,
    PackagedApplication
}

/// <summary>
/// One pre-observed resource fact. The planner never probes the file system or resolves shortcuts.
/// </summary>
public sealed record RestoreResourceObservation(
    int EntryIndex,
    RestoreResourceKind Kind,
    RestoreResourceAvailability Availability,
    string ResolvedTarget = "");

/// <summary>
/// Process identity observed independently of top-level window eligibility. This lets the pure
/// planner distinguish an app that has not started from a background-only or tray-only process.
/// </summary>
public sealed record RunningApplicationIdentity(
    string ExecutablePath,
    string ProcessName,
    string AppUserModelId = "");

/// <summary>Availability of the optional browser-session restore boundary.</summary>
public enum BrowserSessionRestoreAvailability
{
    NotAvailable,
    Available,
    Unavailable
}

/// <summary>
/// Purpose-built, side-effect-free input containing facts already observed about live windows and
/// external resources. Constructing this value is the boundary between discovery and planning.
/// </summary>
public sealed record RestoreLiveInventory
{
    public IReadOnlyList<LiveWindowIdentity> Windows { get; init; } =
        Array.Empty<LiveWindowIdentity>();

    public IReadOnlyList<RestoreResourceObservation> Resources { get; init; } =
        Array.Empty<RestoreResourceObservation>();

    public IReadOnlyList<RunningApplicationIdentity> RunningApplications { get; init; } =
        Array.Empty<RunningApplicationIdentity>();

    public BrowserSessionRestoreAvailability BrowserSessionRestore { get; init; } =
        BrowserSessionRestoreAvailability.NotAvailable;

    public IReadOnlyList<WindowMatchHint> MatchHints { get; init; } =
        Array.Empty<WindowMatchHint>();
}

/// <summary>Current monitor facts required to compute a target placement without native calls.</summary>
public sealed record RestoreMonitor(
    string MonitorId,
    int MonitorIndex,
    int Left,
    int Top,
    int Right,
    int Bottom,
    uint Dpi = 96,
    bool IsPrimary = false,
    int? WorkAreaLeft = null,
    int? WorkAreaTop = null,
    int? WorkAreaRight = null,
    int? WorkAreaBottom = null)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public int EffectiveWorkAreaLeft => WorkAreaLeft ?? Left;
    public int EffectiveWorkAreaTop => WorkAreaTop ?? Top;
    public int EffectiveWorkAreaRight => WorkAreaRight ?? Right;
    public int EffectiveWorkAreaBottom => WorkAreaBottom ?? Bottom;
    public int WorkAreaWidth => EffectiveWorkAreaRight - EffectiveWorkAreaLeft;
    public int WorkAreaHeight => EffectiveWorkAreaBottom - EffectiveWorkAreaTop;
}

/// <summary>Already-observed current monitor topology supplied to the pure planner.</summary>
public sealed record RestoreMonitorTopology
{
    public IReadOnlyList<RestoreMonitor> Monitors { get; init; } = Array.Empty<RestoreMonitor>();

    /// <summary>
    /// True only when stable IDs, virtual bounds, work areas, and DPI match the saved topology.
    /// Exact pixels are used only in this case; all other topologies use adaptation.
    /// </summary>
    public bool IsExactMatch { get; init; }
}

/// <summary>Immutable browser-tab payload carried by an approved restore plan.</summary>
public sealed record RestoreBrowserTab(
    string Url,
    string Title,
    int Index,
    bool Active,
    bool Pinned,
    int GroupIndex);

/// <summary>Immutable browser tab-group payload carried by an approved restore plan.</summary>
public sealed record RestoreBrowserTabGroup(
    int Index,
    string Title,
    string Color,
    bool Collapsed);

/// <summary>Immutable browser-window payload used by the browser-session action.</summary>
public sealed record RestoreBrowserSession(
    string Browser,
    string ActiveTitle,
    int WindowIndex,
    int Left,
    int Top,
    int Width,
    int Height,
    string State,
    IReadOnlyList<RestoreBrowserTab> Tabs,
    IReadOnlyList<RestoreBrowserTabGroup> Groups);

/// <summary>How a saved monitor assignment maps to the current topology.</summary>
public enum RestoreMonitorMappingKind
{
    Unavailable,
    ExactStableId,
    SavedIndexFallback,
    PrimaryFallback
}

/// <summary>Geometry strategy selected by the pure planner.</summary>
public enum RestorePlacementStrategy
{
    ExactPixels,
    Semantic,
    Normalized,
    LegacyDpiScaledAndClamped,
    Unavailable
}

/// <summary>DPI-aware placement calculated for a saved entry.</summary>
public sealed record RestoreTargetPlacement(
    string TargetMonitorId,
    int TargetMonitorIndex,
    RestoreMonitorMappingKind MonitorMapping,
    int Left,
    int Top,
    int Right,
    int Bottom,
    int ShowCmd,
    uint SavedDpi,
    uint TargetDpi,
    bool WasDpiScaled,
    RestorePlacementStrategy Strategy = RestorePlacementStrategy.ExactPixels,
    WindowLayoutKind SemanticKind = WindowLayoutKind.Custom,
    bool WasClamped = false);

/// <summary>Machine-readable plan issue codes suitable for UI and automation.</summary>
public enum RestorePlanIssueCode
{
    CancellationRequested,
    AmbiguousMatch,
    DuplicateEntryId,
    MonitorTopologyUnavailable,
    SavedMonitorUnavailable,
    InvalidSavedPlacement,
    PlacementClamped,
    BrowserSessionUnavailable,
    ResourceAvailabilityUnknown,
    MissingResource,
    StaleResource,
    MissingExecutable,
    UpdatedExecutablePath,
    MissingWebAppLaunchTarget,
    MissingBrowserUrl,
    RunningApplicationHasNoRestorableWindow,
    UnsupportedAlwaysLaunchNew
}

/// <summary>Severity of an explained restore-plan issue.</summary>
public enum RestorePlanIssueSeverity
{
    Warning,
    BlockingError
}

/// <summary>An explained warning or blocking error attached to a plan or one entry.</summary>
public sealed record RestorePlanIssue(
    RestorePlanIssueCode Code,
    RestorePlanIssueSeverity Severity,
    string Explanation);

/// <summary>Candidate projection that remains straightforward to serialize.</summary>
public sealed record RestorePlanCandidate(
    long WindowHandle,
    uint ProcessId,
    bool IsEligible,
    double Score,
    WindowMatchConfidence Confidence,
    IReadOnlyList<WindowMatchEvidence> Evidence,
    double? TitleSimilarityScore,
    bool IsTopScoreTie,
    string Title = "",
    string ProcessName = "",
    string WindowClassName = "",
    string MonitorId = "",
    WindowIdentityBounds Bounds = default,
    WindowIdentityHint? IdentityHint = null,
    bool IsWithinAmbiguityMargin = false,
    bool IsLearnedHintMatch = false,
    bool IsUserSelected = false,
    bool CanRememberChoice = false,
    int ShowCmd = 1);

/// <summary>Kind of launch the executor would need to perform.</summary>
public enum RestoreLaunchKind
{
    None,
    Application,
    Resource,
    DedicatedBrowser,
    WebApp,
    PackagedApplication,
    BrowserSession
}

/// <summary>An explained launch requirement; it describes work and never starts a process.</summary>
public sealed record RestoreLaunchRequirement(
    bool IsRequired,
    RestoreLaunchKind Kind,
    string Target,
    string Arguments,
    bool UseShellExecute,
    RestoreResourceAvailability Availability,
    string Explanation,
    LogSensitivity TargetSensitivity = LogSensitivity.Public,
    LogSensitivity ArgumentsSensitivity = LogSensitivity.CommandLine)
{
    public static RestoreLaunchRequirement None(string explanation) => new(
        false,
        RestoreLaunchKind.None,
        "",
        "",
        false,
        RestoreResourceAvailability.Unknown,
        explanation);
}

/// <summary>Kind of side effect described by a plan for a future executor.</summary>
public enum RestoreActionKind
{
    RestoreExistingWindow,
    LaunchApplication,
    OpenResource,
    LaunchDedicatedBrowser,
    LaunchWebApp,
    ActivatePackagedApplication,
    RestoreBrowserSession,
    AwaitWindowAppearance,
    MinimizeOtherWindows
}

/// <summary>Condition under which an approved action is eligible for execution.</summary>
public enum RestoreActionCondition
{
    Always,
    BrowserSessionUnavailable
}

/// <summary>
/// One immutable action description. It is data only and cannot mutate a process, browser, or
/// native window by itself.
/// </summary>
public sealed record RestoreAction(
    int? EntryIndex,
    RestoreActionKind Kind,
    long? WindowHandle,
    string Target,
    string Arguments,
    bool UseShellExecute,
    RestoreTargetPlacement? TargetPlacement,
    string Explanation,
    LogSensitivity TargetSensitivity = LogSensitivity.Public,
    LogSensitivity ArgumentsSensitivity = LogSensitivity.CommandLine,
    RestoreActionCondition Condition = RestoreActionCondition.Always);

/// <summary>Deterministic outcome assigned to one saved workspace entry.</summary>
public enum RestorePlanEntryOutcome
{
    Cancelled,
    Excluded,
    Matched,
    MatchedAndLaunchRequired,
    LaunchRequired,
    AwaitingBrowserSession,
    AwaitingRunningApplication,
    Blocked
}

/// <summary>Immutable, explained planning result for one saved workspace entry.</summary>
public sealed record RestorePlanEntry(
    int EntryIndex,
    string EntryId,
    RestorePlanEntryOutcome Outcome,
    string Explanation,
    SavedWindowIdentity SavedIdentity,
    IReadOnlyList<RestorePlanCandidate> Candidates,
    RestorePlanCandidate? SelectedMatch,
    RestoreTargetPlacement TargetPlacement,
    RestoreLaunchRequirement LaunchRequirement,
    IReadOnlyList<RestoreAction> Actions,
    IReadOnlyList<RestorePlanIssue> Warnings,
    IReadOnlyList<RestorePlanIssue> BlockingErrors)
{
    /// <summary>Resolved workspace/entry policy that produced this entry's actions.</summary>
    public ResolvedEntryRestorePolicy RestorePolicy { get; init; } =
        ResolvedEntryRestorePolicy.ResumeDefault;

    /// <summary>
    /// Pre-existing candidates that a fresh-launch readiness wait must not claim as the new
    /// instance.
    /// </summary>
    public IReadOnlySet<long> ReadinessExcludedWindowHandles { get; init; } =
        new HashSet<long>();
}

/// <summary>
/// Serializable, immutable restore intent. It contains no clocks or random IDs, so identical
/// inputs produce identical plans and JSON snapshots.
/// </summary>
public sealed record RestorePlan
{
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string WorkspaceId { get; init; } = "";
    public string WorkspaceName { get; init; } = "";
    public DateTime SnapshotSavedAt { get; init; }
    public RestoreModeKind Mode { get; init; }
    public IReadOnlyList<string> SelectedMonitorIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<RestoreBrowserSession> BrowserSessions { get; init; } =
        Array.Empty<RestoreBrowserSession>();
    public IReadOnlySet<int> DisabledEntryIndexes { get; init; } = new HashSet<int>();
    public IReadOnlySet<long> ProtectedWindowHandles { get; init; } = new HashSet<long>();
    public bool WasCancelled { get; init; }
    public IReadOnlyList<RestorePlanEntry> Entries { get; init; } = Array.Empty<RestorePlanEntry>();
    public IReadOnlyList<RestoreAction> Actions { get; init; } = Array.Empty<RestoreAction>();
    public IReadOnlyList<RestorePlanIssue> Warnings { get; init; } = Array.Empty<RestorePlanIssue>();
    public IReadOnlyList<RestorePlanIssue> BlockingErrors { get; init; } = Array.Empty<RestorePlanIssue>();

    [JsonIgnore]
    public bool CanExecute => !WasCancelled &&
        Mode != RestoreModeKind.PreviewOnly &&
        BlockingErrors.Count == 0;

    /// <summary>Returns a deep privacy-safe projection suitable for diagnostic serialization.</summary>
    public RestorePlan Redact() => this with
    {
        WorkspaceId = RedactIdentifier(WorkspaceId),
        WorkspaceName = LogRedactor.RedactValue(
            WorkspaceName,
            LogSensitivity.WorkspaceName,
            LogRedactionMode.Redacted),
        SelectedMonitorIds = SelectedMonitorIds.Select(RedactIdentifier).ToArray(),
        BrowserSessions = BrowserSessions.Select(RedactBrowserSession).ToArray(),
        Entries = Entries.Select(RedactEntry).ToArray(),
        Actions = Actions.Select(RedactAction).ToArray()
    };

    /// <summary>Serializes only the privacy-safe projection of this plan.</summary>
    public string ToRedactedJson(bool writeIndented = false)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Serialize(Redact(), options);
    }

    private static RestorePlanEntry RedactEntry(RestorePlanEntry entry) => entry with
    {
        EntryId = RedactIdentifier(entry.EntryId),
        SavedIdentity = RedactIdentity(entry.SavedIdentity),
        Candidates = entry.Candidates.Select(RedactCandidate).ToArray(),
        SelectedMatch = entry.SelectedMatch is null ? null : RedactCandidate(entry.SelectedMatch),
        TargetPlacement = RedactPlacement(entry.TargetPlacement),
        LaunchRequirement = RedactLaunch(entry.LaunchRequirement),
        Actions = entry.Actions.Select(RedactAction).ToArray()
    };

    private static RestorePlanCandidate RedactCandidate(RestorePlanCandidate candidate) => candidate with
    {
        Title = LogRedactor.RedactValue(
            candidate.Title,
            LogSensitivity.Title,
            LogRedactionMode.Redacted),
        MonitorId = RedactIdentifier(candidate.MonitorId),
        IdentityHint = candidate.IdentityHint is null
            ? null
            : candidate.IdentityHint with
            {
                ExecutablePath = RedactPath(candidate.IdentityHint.ExecutablePath),
                AppUserModelId = RedactIdentifier(candidate.IdentityHint.AppUserModelId),
                PackageFamilyName = RedactIdentifier(candidate.IdentityHint.PackageFamilyName),
                FolderPath = RedactPath(candidate.IdentityHint.FolderPath),
                BrowserSiteHost = RedactIdentifier(candidate.IdentityHint.BrowserSiteHost),
                PwaIdentity = RedactIdentifier(candidate.IdentityHint.PwaIdentity),
                TitleTokens = Array.Empty<string>()
            }
    };

    private static SavedWindowIdentity RedactIdentity(SavedWindowIdentity identity) => identity with
    {
        EntryId = RedactIdentifier(identity.EntryId),
        ExecutablePath = RedactPath(identity.ExecutablePath),
        AppUserModelId = RedactIdentifier(identity.AppUserModelId),
        PackageFamilyName = RedactIdentifier(identity.PackageFamilyName),
        DocumentPath = RedactPath(identity.DocumentPath),
        LaunchTargetPath = RedactPath(identity.LaunchTargetPath),
        FolderPath = RedactPath(identity.FolderPath),
        ProjectOrWorkspacePath = RedactPath(identity.ProjectOrWorkspacePath),
        BrowserProfile = RedactIdentifier(identity.BrowserProfile),
        BrowserUrl = LogRedactor.RedactUrl(identity.BrowserUrl),
        BrowserSiteHost = RedactIdentifier(identity.BrowserSiteHost),
        BrowserSessionIdentity = RedactIdentifier(identity.BrowserSessionIdentity),
        BrowserWindowIdentity = RedactIdentifier(identity.BrowserWindowIdentity),
        PwaIdentity = RedactIdentifier(identity.PwaIdentity),
        AppAdapterIdentity = RedactIdentifier(identity.AppAdapterIdentity),
        SafeLaunchArguments = LogRedactor.RedactValue(
            identity.SafeLaunchArguments,
            LogSensitivity.CommandLine,
            LogRedactionMode.Redacted),
        Title = LogRedactor.RedactValue(
            identity.Title,
            LogSensitivity.Title,
            LogRedactionMode.Redacted),
        NormalizedTitleTokens = Array.Empty<string>(),
        SavedMonitorId = RedactIdentifier(identity.SavedMonitorId)
    };

    private static RestoreTargetPlacement RedactPlacement(RestoreTargetPlacement placement) =>
        placement with { TargetMonitorId = RedactIdentifier(placement.TargetMonitorId) };

    private static RestoreLaunchRequirement RedactLaunch(RestoreLaunchRequirement launch) =>
        launch with
        {
            Target = LogRedactor.RedactValue(
                launch.Target,
                launch.TargetSensitivity,
                LogRedactionMode.Redacted),
            Arguments = LogRedactor.RedactValue(
                launch.Arguments,
                launch.ArgumentsSensitivity,
                LogRedactionMode.Redacted)
        };

    private static RestoreAction RedactAction(RestoreAction action) => action with
    {
        Target = LogRedactor.RedactValue(
            action.Target,
            action.TargetSensitivity,
            LogRedactionMode.Redacted),
        Arguments = LogRedactor.RedactValue(
            action.Arguments,
            action.ArgumentsSensitivity,
            LogRedactionMode.Redacted),
        TargetPlacement = action.TargetPlacement is null
            ? null
            : RedactPlacement(action.TargetPlacement)
    };

    private static RestoreBrowserSession RedactBrowserSession(RestoreBrowserSession session) =>
        session with
        {
            ActiveTitle = LogRedactor.RedactValue(
                session.ActiveTitle,
                LogSensitivity.Title,
                LogRedactionMode.Redacted),
            Tabs = session.Tabs.Select(tab => tab with
            {
                Url = LogRedactor.RedactUrl(tab.Url),
                Title = LogRedactor.RedactValue(
                    tab.Title,
                    LogSensitivity.Title,
                    LogRedactionMode.Redacted)
            }).ToArray(),
            Groups = session.Groups.Select(group => group with
            {
                Title = LogRedactor.RedactValue(
                    group.Title,
                    LogSensitivity.Title,
                    LogRedactionMode.Redacted)
            }).ToArray()
        };

    private static string RedactPath(string value) =>
        LogRedactor.RedactValue(value, LogSensitivity.Path, LogRedactionMode.Redacted);

    private static string RedactIdentifier(string value) =>
        LogRedactor.RedactValue(value, LogSensitivity.Identifier, LogRedactionMode.Redacted);
}
