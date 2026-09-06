using System;
using System.Collections.Generic;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Stable window bounds used as weak, non-identity matching context.</summary>
public readonly record struct WindowIdentityBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>
/// Stable identity evidence extracted from one saved workspace entry. Runtime handles and
/// process IDs are deliberately absent so this model is safe to derive from persisted data.
/// </summary>
public sealed record SavedWindowIdentity
{
    public string EntryId { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string WindowClassName { get; init; } = "";
    public string AppUserModelId { get; init; } = "";
    public string PackageFamilyName { get; init; } = "";
    public string DocumentPath { get; init; } = "";
    public string LaunchTargetPath { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public string ProjectOrWorkspacePath { get; init; } = "";
    public string BrowserFamily { get; init; } = "";
    public string BrowserProfile { get; init; } = "";
    public string BrowserUrl { get; init; } = "";
    public string BrowserSiteHost { get; init; } = "";
    public string BrowserSessionIdentity { get; init; } = "";
    public string BrowserWindowIdentity { get; init; } = "";
    public string PwaIdentity { get; init; } = "";
    public string AppAdapterIdentity { get; init; } = "";
    public string SafeLaunchArguments { get; init; } = "";
    public string Title { get; init; } = "";
    public IReadOnlyList<string> NormalizedTitleTokens { get; init; } = Array.Empty<string>();
    public string SavedMonitorId { get; init; } = "";
    public int SavedMonitorIndex { get; init; }
    public WindowIdentityBounds PreviousBounds { get; init; }
    public bool IsWebApp { get; init; }
    public bool IsDedicatedBrowserWindow { get; init; }
}

/// <summary>
/// Identity evidence extracted from one already-enumerated live window. HWND and PID are
/// runtime routing metadata only and are never copied into <see cref="SavedWindowIdentity"/>.
/// </summary>
public sealed record LiveWindowIdentity
{
    public IntPtr Hwnd { get; init; }
    public uint ProcessId { get; init; }
    public string ExecutablePath { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string WindowClassName { get; init; } = "";
    public string AppUserModelId { get; init; } = "";
    public string PackageFamilyName { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public string BrowserFamily { get; init; } = "";
    public string BrowserUrl { get; init; } = "";
    public string BrowserSiteHost { get; init; } = "";
    public string BrowserSessionIdentity { get; init; } = "";
    public string BrowserWindowIdentity { get; init; } = "";
    public string PwaIdentity { get; init; } = "";
    public IReadOnlyList<string> NormalizedTitleTokens { get; init; } = Array.Empty<string>();
    public string MonitorId { get; init; } = "";
    public int MonitorIndex { get; init; }
    public WindowIdentityBounds Bounds { get; init; }
    public int ShowCmd { get; init; } = 1;
    public string Title { get; init; } = "";
    public bool IsWebApp { get; init; }
    public bool IsDedicatedBrowserWindow { get; init; }
}

/// <summary>Machine-readable reason contributing to, or rejecting, a match candidate.</summary>
public enum WindowMatchEvidenceKind
{
    MissingSavedExecutable,
    ExecutablePathExact,
    ExecutablePathMismatch,
    ProcessNameExact,
    WindowClassExact,
    AppUserModelIdExact,
    PackageFamilyExact,
    PwaIdentityExact,
    PwaIdentityMismatch,
    PwaKindMismatch,
    DedicatedBrowserKindMismatch,
    DedicatedBrowserSiteExact,
    DedicatedBrowserSiteMismatch,
    DocumentNameInTitle,
    FolderPathExact,
    BrowserFamilyExact,
    TitlePrefixExact,
    TitleSimilarity,
    LearnedIdentityHint,
    UserSelectedCandidate,
    MonitorIdExact,
    GeometrySimilarity,
    NoSupportedFallback
}

/// <summary>One explained signal used while scoring a live-window candidate.</summary>
public sealed record WindowMatchEvidence(
    WindowMatchEvidenceKind Kind,
    bool Matched,
    double ScoreContribution,
    string Explanation);

/// <summary>Qualitative interpretation of a candidate's numeric score.</summary>
public enum WindowMatchConfidence
{
    Missing,
    Ineligible,
    Ambiguous,
    Probable,
    Strong,
    Exact
}

/// <summary>
/// Deterministic thresholds used by matching and ambiguity resolution. Values live in the service
/// layer so UI presentation cannot silently alter assignment safety.
/// </summary>
public sealed record WindowMatchPolicy
{
    public double MinimumTitleSimilarity { get; init; } = 0.45;
    public double StrongTitleSimilarity { get; init; } = 0.70;
    public double MinimumGeometrySimilarity { get; init; } = 0.50;
    public double MinimumCandidateScore { get; init; } = 3000;
    public double AmbiguityScoreMargin { get; init; } = 175;
    public double LearnedHintBonus { get; init; } = 2000;

    public static WindowMatchPolicy Default { get; } = new();
}

/// <summary>
/// One scored, explained live-window candidate for a saved identity. Candidate ordering is
/// deterministic: eligible candidates first, then descending score, then ascending HWND.
/// </summary>
public sealed record WindowMatchCandidate(
    string EntryId,
    IntPtr Hwnd,
    uint ProcessId,
    bool IsEligible,
    double Score,
    WindowMatchConfidence Confidence,
    IReadOnlyList<WindowMatchEvidence> Evidence,
    double? TitleSimilarityScore,
    string Title,
    string ProcessName,
    string WindowClassName,
    string MonitorId,
    WindowIdentityBounds Bounds,
    WindowIdentityHint IdentityHint,
    bool IsTopScoreTie = false,
    bool IsWithinAmbiguityMargin = false,
    bool IsLearnedHintMatch = false,
    int ShowCmd = 1);

/// <summary>
/// Result of applying confidence and top-vs-runner-up thresholds. Ambiguous and missing results
/// deliberately contain no selected candidate.
/// </summary>
public sealed record WindowMatchResolution(
    WindowMatchConfidence Confidence,
    WindowMatchCandidate? SelectedCandidate,
    IReadOnlyList<WindowMatchCandidate> Candidates,
    string Explanation)
{
    public bool IsAmbiguous => Confidence == WindowMatchConfidence.Ambiguous;
    public bool IsMissing => Confidence == WindowMatchConfidence.Missing;
}
