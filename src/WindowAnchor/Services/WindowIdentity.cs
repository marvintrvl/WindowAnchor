using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    bool IsLearnedHintMatch = false);

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

/// <summary>Central, side-effect-free extraction of saved and live window identity evidence.</summary>
public static partial class WindowIdentityExtractor
{
    public static SavedWindowIdentity FromSaved(WorkspaceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string launchPath = entry.LaunchArg ?? "";
        string folderPath = entry.Position?.FolderPath ?? "";
        bool projectLike = IsProjectLike(entry.ProcessName, launchPath);

        return new SavedWindowIdentity
        {
            EntryId = entry.EntryId,
            ExecutablePath = NormalizePath(entry.ExecutablePath),
            ProcessName = NormalizeProcessName(entry.ProcessName),
            WindowClassName = entry.WindowClassName ?? "",
            AppUserModelId = entry.AppUserModelId ?? "",
            PackageFamilyName = PackageFamily(entry.AppUserModelId),
            DocumentPath = NormalizePath(entry.FilePath),
            LaunchTargetPath = projectLike ? "" : NormalizePath(launchPath),
            FolderPath = NormalizePath(folderPath),
            ProjectOrWorkspacePath = projectLike ? NormalizePath(launchPath) : "",
            BrowserFamily = BrowserFamily(entry.ProcessName),
            BrowserProfile = BrowserProfile(entry.WebAppLaunchArguments),
            BrowserUrl = entry.BrowserUrl ?? "",
            BrowserSiteHost = SiteHost(entry.BrowserUrl),
            PwaIdentity = entry.IsWebApp ? entry.AppUserModelId ?? "" : "",
            SafeLaunchArguments = LogRedactor.RedactValue(
                entry.WebAppLaunchArguments,
                LogSensitivity.CommandLine,
                LogRedactionMode.Redacted),
            Title = entry.Position?.TitleSnippet ?? "",
            NormalizedTitleTokens = NormalizeTitleTokens(entry.Position?.TitleSnippet),
            SavedMonitorId = entry.MonitorId ?? "",
            SavedMonitorIndex = entry.MonitorIndex,
            PreviousBounds = Bounds(entry.Position),
            IsWebApp = entry.IsWebApp,
            IsDedicatedBrowserWindow = entry.IsDedicatedBrowserWindow
        };
    }

    public static LiveWindowIdentity FromLive(IntPtr hwnd, uint processId, WindowRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string aumid = record.AppUserModelId ?? "";
        return new LiveWindowIdentity
        {
            Hwnd = hwnd,
            ProcessId = processId,
            ExecutablePath = NormalizePath(record.ExecutablePath),
            ProcessName = NormalizeProcessName(record.ProcessName),
            WindowClassName = record.ClassName ?? "",
            AppUserModelId = aumid,
            PackageFamilyName = PackageFamily(aumid),
            FolderPath = NormalizePath(record.FolderPath),
            BrowserFamily = BrowserFamily(record.ProcessName),
            BrowserUrl = record.BrowserUrl ?? "",
            BrowserSiteHost = SiteHost(record.BrowserUrl),
            PwaIdentity = WebAppService.LooksLikeWebAppAumid(aumid) ? aumid : "",
            NormalizedTitleTokens = NormalizeTitleTokens(record.TitleSnippet),
            MonitorId = record.MonitorId ?? "",
            MonitorIndex = record.MonitorIndex,
            Bounds = Bounds(record),
            Title = record.TitleSnippet ?? "",
            IsWebApp = WebAppService.LooksLikeWebAppAumid(aumid),
            IsDedicatedBrowserWindow = !string.IsNullOrWhiteSpace(record.BrowserUrl)
        };
    }

    /// <summary>
    /// Creates a persistable composite identity from a live candidate without copying HWND/PID.
    /// </summary>
    public static WindowIdentityHint ToHint(LiveWindowIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new WindowIdentityHint
        {
            ExecutablePath = identity.ExecutablePath,
            ProcessName = identity.ProcessName,
            WindowClassName = identity.WindowClassName,
            AppUserModelId = identity.AppUserModelId,
            PackageFamilyName = identity.PackageFamilyName,
            FolderPath = identity.FolderPath,
            BrowserFamily = identity.BrowserFamily,
            BrowserSiteHost = identity.BrowserSiteHost,
            PwaIdentity = identity.PwaIdentity,
            TitleTokens = identity.NormalizedTitleTokens
                .OrderBy(token => token, StringComparer.Ordinal)
                .ToArray()
        };
    }

    public static IReadOnlyList<string> NormalizeTitleTokens(string? title) =>
        TitleTokenRegex().Matches(title ?? "")
            .Select(match => match.Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    internal static string NormalizePath(string? path) =>
        (path ?? "")
            .Trim()
            .Trim('"')
            .Replace('/', '\\')
            .TrimEnd('\\')
            .ToLowerInvariant();

    private static string NormalizeProcessName(string? processName)
    {
        string value = (processName ?? "").Trim().ToLowerInvariant();
        return value.EndsWith(".exe", StringComparison.Ordinal) ? value[..^4] : value;
    }

    internal static string PackageFamily(string? appUserModelId)
    {
        string value = appUserModelId ?? "";
        int separator = value.IndexOf('!');
        return separator > 0 ? value[..separator].ToLowerInvariant() : "";
    }

    private static string BrowserFamily(string? processName) =>
        WebAppService.IsChromiumBrowser(processName ?? "")
            ? NormalizeProcessName(processName)
            : "";

    private static string BrowserProfile(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return "";
        Match match = BrowserProfileRegex().Match(arguments);
        return match.Success ? match.Groups["profile"].Value.Trim('"', '\'').ToLowerInvariant() : "";
    }

    private static string SiteHost(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            ? parsed.IdnHost.ToLowerInvariant()
            : "";

    private static bool IsProjectLike(string? processName, string path)
    {
        string process = NormalizeProcessName(processName);
        return process is "code" or "cursor" ||
               path.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase);
    }

    private static WindowIdentityBounds Bounds(WindowRecord? record) => record == null
        ? default
        : new WindowIdentityBounds(
            record.NormalLeft,
            record.NormalTop,
            record.NormalRight,
            record.NormalBottom);

    [GeneratedRegex("[\\p{L}\\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex TitleTokenRegex();

    [GeneratedRegex("(?i)(?:--profile-directory(?:=|\\s+))(?<profile>\\\"[^\\\"]+\\\"|'[^']+'|[^\\s]+)")]
    private static partial Regex BrowserProfileRegex();
}

/// <summary>Pure scored matching over already-extracted saved and live identities.</summary>
public static class WindowMatcher
{
    public static IReadOnlyList<WindowMatchCandidate> FindCandidates(
        SavedWindowIdentity saved,
        IEnumerable<LiveWindowIdentity> liveWindows,
        WindowIdentityHint? learnedHint = null,
        WindowMatchPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(liveWindows);
        policy ??= WindowMatchPolicy.Default;

        LiveWindowIdentity[] live = liveWindows
            .OrderBy(candidate => candidate.Hwnd.ToInt64())
            .ToArray();
        int compatibleSameExecutableCount = live.Count(candidate =>
            KindCompatible(saved, candidate) && ExecutableExact(saved, candidate));

        WindowMatchCandidate[] ordered = live
            .Select(candidate => Evaluate(
                saved,
                candidate,
                compatibleSameExecutableCount,
                policy))
            .Select(candidate => ApplyMinimumScore(candidate, policy))
            .Select(candidate => ApplyLearnedHint(candidate, learnedHint, policy))
            .OrderByDescending(candidate => candidate.IsEligible)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Hwnd.ToInt64())
            .ToArray();

        WindowMatchCandidate? best = ordered.FirstOrDefault(candidate => candidate.IsEligible);
        if (best == null) return ordered;
        int topCount = ordered.Count(candidate =>
            candidate.IsEligible && Math.Abs(candidate.Score - best.Score) < 0.000001);

        return ordered
            .Select(candidate => candidate with
            {
                IsTopScoreTie = topCount > 1 && candidate.IsEligible &&
                    Math.Abs(candidate.Score - best.Score) < 0.000001,
                IsWithinAmbiguityMargin = candidate.IsEligible &&
                    best.Score - candidate.Score <= policy.AmbiguityScoreMargin
            })
            .ToArray();
    }

    /// <summary>Scores and resolves candidates using the configured deterministic thresholds.</summary>
    public static WindowMatchResolution Resolve(
        SavedWindowIdentity saved,
        IEnumerable<LiveWindowIdentity> liveWindows,
        WindowIdentityHint? learnedHint = null,
        WindowMatchPolicy? policy = null)
    {
        policy ??= WindowMatchPolicy.Default;
        return ResolveCandidates(FindCandidates(saved, liveWindows, learnedHint, policy), policy);
    }

    /// <summary>Resolves an already-scored candidate set without observing external state.</summary>
    public static WindowMatchResolution ResolveCandidates(
        IReadOnlyList<WindowMatchCandidate> candidates,
        WindowMatchPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        policy ??= WindowMatchPolicy.Default;
        WindowMatchCandidate[] eligible = candidates
            .Where(candidate => candidate.IsEligible)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Hwnd.ToInt64())
            .ToArray();
        if (eligible.Length == 0)
        {
            return new WindowMatchResolution(
                WindowMatchConfidence.Missing,
                null,
                candidates,
                "No live window met the minimum evidence threshold.");
        }

        WindowMatchCandidate best = eligible[0];
        if (eligible.Length > 1 && best.Score - eligible[1].Score <= policy.AmbiguityScoreMargin)
        {
            return new WindowMatchResolution(
                WindowMatchConfidence.Ambiguous,
                null,
                candidates,
                $"The top {eligible.Count(candidate => best.Score - candidate.Score <= policy.AmbiguityScoreMargin)} " +
                $"candidates are within the {policy.AmbiguityScoreMargin:0}-point safety margin.");
        }

        string reason = best.Evidence
            .Where(evidence => evidence.Matched)
            .OrderByDescending(evidence => evidence.ScoreContribution)
            .Select(evidence => evidence.Explanation)
            .FirstOrDefault() ?? "The candidate met the configured evidence threshold.";
        return new WindowMatchResolution(
            best.Confidence,
            best,
            candidates,
            $"{best.Confidence} confidence. {reason}");
    }

    public static bool SameSite(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(first, UriKind.Absolute, out var a) &&
               Uri.TryCreate(second, UriKind.Absolute, out var b) &&
               string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase);
    }

    public static double TitleSimilarity(string? first, string? second)
    {
        string a = (first ?? "").ToLowerInvariant();
        string b = (second ?? "").ToLowerInvariant();
        if (a.Length < 2 || b.Length < 2)
            return string.Equals(a, b, StringComparison.Ordinal) ? 1.0 : 0.0;

        var bigrams = new Dictionary<string, int>();
        for (int i = 0; i < a.Length - 1; i++)
        {
            string gram = a.Substring(i, 2);
            bigrams[gram] = bigrams.TryGetValue(gram, out int count) ? count + 1 : 1;
        }

        int overlap = 0;
        for (int i = 0; i < b.Length - 1; i++)
        {
            string gram = b.Substring(i, 2);
            if (bigrams.TryGetValue(gram, out int count) && count > 0)
            {
                bigrams[gram] = count - 1;
                overlap++;
            }
        }

        return 2.0 * overlap / ((a.Length - 1) + (b.Length - 1));
    }

    private static WindowMatchCandidate Evaluate(
        SavedWindowIdentity saved,
        LiveWindowIdentity live,
        int compatibleSameExecutableCount,
        WindowMatchPolicy policy)
    {
        var evidence = new List<WindowMatchEvidence>();
        if (string.IsNullOrWhiteSpace(saved.ExecutablePath))
            return Rejected(saved, live, evidence, WindowMatchEvidenceKind.MissingSavedExecutable,
                "The saved entry has no executable identity.");

        if (!KindCompatible(saved, live, evidence))
            return Candidate(saved, live, false, 0, WindowMatchConfidence.Ineligible, evidence, null);

        bool executableExact = ExecutableExact(saved, live);
        Add(evidence,
            executableExact ? WindowMatchEvidenceKind.ExecutablePathExact : WindowMatchEvidenceKind.ExecutablePathMismatch,
            executableExact,
            0,
            executableExact
                ? "Executable paths match."
                : "Executable paths differ.");
        if (ProcessExact(saved, live))
            Add(evidence, WindowMatchEvidenceKind.ProcessNameExact, true, 0, "Process names match.");
        if (ClassExact(saved, live))
            Add(evidence, WindowMatchEvidenceKind.WindowClassExact, true, 0, "Window classes match.");
        if (PackageFamilyExact(saved, live))
            Add(evidence, WindowMatchEvidenceKind.PackageFamilyExact, true, 0, "Package families match.");
        if (BrowserFamilyExact(saved, live))
            Add(evidence, WindowMatchEvidenceKind.BrowserFamilyExact, true, 0, "Browser families match.");
        if (FolderExact(saved, live))
            Add(evidence, WindowMatchEvidenceKind.FolderPathExact, true, 0, "Folder identities match.");
        if (MonitorExact(saved, live))
            Add(evidence, WindowMatchEvidenceKind.MonitorIdExact, true, 0, "Monitor identities match.");

        bool packagedIdentityExact = !saved.IsWebApp &&
            PackageFamilyExact(saved, live) &&
            !string.IsNullOrWhiteSpace(saved.AppUserModelId) &&
            string.Equals(
                saved.AppUserModelId,
                live.AppUserModelId,
                StringComparison.OrdinalIgnoreCase);
        if (packagedIdentityExact)
        {
            Add(evidence, WindowMatchEvidenceKind.AppUserModelIdExact, true, 9800,
                "Packaged application identities match exactly.");
            return Candidate(saved, live, true, 9800, WindowMatchConfidence.Exact, evidence, null);
        }

        if (saved.IsWebApp && !string.IsNullOrWhiteSpace(saved.PwaIdentity))
        {
            bool exact = string.Equals(
                saved.PwaIdentity,
                live.AppUserModelId,
                StringComparison.OrdinalIgnoreCase);
            if (!exact)
                return Rejected(saved, live, evidence, WindowMatchEvidenceKind.PwaIdentityMismatch,
                    "PWA AppUserModelIDs differ.");

            Add(evidence, WindowMatchEvidenceKind.PwaIdentityExact, true, 10000,
                "PWA AppUserModelIDs match exactly.");
            Add(evidence, WindowMatchEvidenceKind.AppUserModelIdExact, true, 0,
                "AppUserModelIDs match exactly.");
            return Candidate(saved, live, true, 10000, WindowMatchConfidence.Exact, evidence, null);
        }

        if (saved.IsWebApp && !string.Equals(
                saved.AppUserModelId,
                live.AppUserModelId,
                StringComparison.OrdinalIgnoreCase))
        {
            return Rejected(saved, live, evidence, WindowMatchEvidenceKind.PwaIdentityMismatch,
                "PWA AppUserModelIDs differ.");
        }

        if (saved.IsDedicatedBrowserWindow)
        {
            if (!executableExact)
                return Candidate(saved, live, false, 0, WindowMatchConfidence.Ineligible, evidence, null);
            bool sameSite = SameSite(saved.BrowserUrl, live.BrowserUrl);
            if (!sameSite)
                return Rejected(saved, live, evidence,
                    WindowMatchEvidenceKind.DedicatedBrowserSiteMismatch,
                    "Dedicated browser site hosts differ.");

            Add(evidence, WindowMatchEvidenceKind.DedicatedBrowserSiteExact, true, 9500,
                "Dedicated browser site hosts match.");
            return Candidate(saved, live, true, 9500, WindowMatchConfidence.Exact, evidence, null);
        }

        if (!executableExact)
            return Candidate(saved, live, false, 0, WindowMatchConfidence.Ineligible, evidence, null);

        string documentStem = SafeFileStem(saved.LaunchTargetPath);
        if (!string.IsNullOrWhiteSpace(documentStem) &&
            live.Title.Contains(documentStem, StringComparison.OrdinalIgnoreCase))
        {
            Add(evidence, WindowMatchEvidenceKind.DocumentNameInTitle, true, 9000,
                "The live title contains the saved document name.");
            return Candidate(saved, live, true, 9000, WindowMatchConfidence.Exact, evidence, null);
        }

        string projectStem = SafeFileStem(saved.ProjectOrWorkspacePath);
        if (!string.IsNullOrWhiteSpace(projectStem) &&
            live.Title.Contains(projectStem, StringComparison.OrdinalIgnoreCase))
        {
            Add(evidence, WindowMatchEvidenceKind.DocumentNameInTitle, true, 9000,
                "The live title contains the saved project or workspace name.");
            return Candidate(saved, live, true, 9000, WindowMatchConfidence.Exact, evidence, null);
        }

        if (compatibleSameExecutableCount > 1)
        {
            double similarity = TitleSimilarity(
                saved.Title,
                live.Title);
            double score = 6000 + (similarity * 1000);
            bool meetsMinimum = similarity >= policy.MinimumTitleSimilarity;
            Add(evidence, WindowMatchEvidenceKind.TitleSimilarity, meetsMinimum, score,
                meetsMinimum
                    ? "Multiple same-executable windows were ranked by title similarity."
                    : $"Title similarity is below the {policy.MinimumTitleSimilarity:P0} minimum.");
            return Candidate(
                saved,
                live,
                meetsMinimum,
                meetsMinimum ? score : 0,
                !meetsMinimum
                    ? WindowMatchConfidence.Ineligible
                    : similarity >= policy.StrongTitleSimilarity
                        ? WindowMatchConfidence.Strong
                        : WindowMatchConfidence.Probable,
                evidence,
                similarity);
        }

        if (ClassExact(saved, live))
        {
            Add(evidence, WindowMatchEvidenceKind.WindowClassExact, true, 5000,
                "Executable path and window class form the generic fallback.");
            return Candidate(saved, live, true, 5000, WindowMatchConfidence.Probable, evidence, null);
        }

        string titlePrefix = saved.Title.Length >= 10 ? saved.Title[..10] : saved.Title;
        if (live.Title.StartsWith(titlePrefix, StringComparison.OrdinalIgnoreCase))
        {
            Add(evidence, WindowMatchEvidenceKind.TitlePrefixExact, true, 4000,
                "Executable path and saved title prefix match.");
            return Candidate(saved, live, true, 4000, WindowMatchConfidence.Probable, evidence, null);
        }

        double? geometry = GeometrySimilarity(saved.PreviousBounds, live.Bounds);
        bool sameMonitor = MonitorExact(saved, live);
        if (sameMonitor || geometry >= policy.MinimumGeometrySimilarity)
        {
            double contribution = 3000 + ((geometry ?? 0) * 500);
            if (geometry != null)
                Add(evidence, WindowMatchEvidenceKind.GeometrySimilarity, true, contribution,
                    "Previous and live bounds provide weak geometric context.");
            else
                Add(evidence, WindowMatchEvidenceKind.MonitorIdExact, true, contribution,
                    "Saved and live monitor identities provide weak context.");
            return Candidate(saved, live, true, contribution, WindowMatchConfidence.Probable, evidence, null);
        }

        return Rejected(saved, live, evidence, WindowMatchEvidenceKind.NoSupportedFallback,
            "No supported generic fallback matched.");
    }

    private static bool KindCompatible(SavedWindowIdentity saved, LiveWindowIdentity live) =>
        KindCompatible(saved, live, null);

    private static bool KindCompatible(
        SavedWindowIdentity saved,
        LiveWindowIdentity live,
        List<WindowMatchEvidence>? evidence)
    {
        if (saved.IsDedicatedBrowserWindow != live.IsDedicatedBrowserWindow)
        {
            Add(evidence, WindowMatchEvidenceKind.DedicatedBrowserKindMismatch, false, 0,
                "Dedicated and ordinary browser windows cannot match each other.");
            return false;
        }

        if (!saved.IsWebApp && live.IsWebApp)
        {
            Add(evidence, WindowMatchEvidenceKind.PwaKindMismatch, false, 0,
                "An ordinary application entry cannot claim a PWA window.");
            return false;
        }

        return true;
    }

    private static bool ExecutableExact(SavedWindowIdentity saved, LiveWindowIdentity live) =>
        !string.IsNullOrWhiteSpace(saved.ExecutablePath) &&
        string.Equals(saved.ExecutablePath, live.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    private static bool ProcessExact(SavedWindowIdentity saved, LiveWindowIdentity live) =>
        !string.IsNullOrWhiteSpace(saved.ProcessName) &&
        string.Equals(saved.ProcessName, live.ProcessName, StringComparison.OrdinalIgnoreCase);

    private static bool ClassExact(SavedWindowIdentity saved, LiveWindowIdentity live) =>
        string.Equals(saved.WindowClassName, live.WindowClassName, StringComparison.Ordinal);

    private static bool PackageFamilyExact(SavedWindowIdentity saved, LiveWindowIdentity live) =>
        !string.IsNullOrWhiteSpace(saved.PackageFamilyName) &&
        string.Equals(saved.PackageFamilyName, live.PackageFamilyName, StringComparison.OrdinalIgnoreCase);

    private static bool BrowserFamilyExact(SavedWindowIdentity saved, LiveWindowIdentity live) =>
        !string.IsNullOrWhiteSpace(saved.BrowserFamily) &&
            string.Equals(saved.BrowserFamily, live.BrowserFamily, StringComparison.OrdinalIgnoreCase);

    private static bool FolderExact(SavedWindowIdentity saved, LiveWindowIdentity live) =>
        !string.IsNullOrWhiteSpace(saved.FolderPath) &&
        string.Equals(saved.FolderPath, live.FolderPath, StringComparison.OrdinalIgnoreCase);

    private static bool MonitorExact(SavedWindowIdentity saved, LiveWindowIdentity live) =>
        !string.IsNullOrWhiteSpace(saved.SavedMonitorId) &&
        string.Equals(saved.SavedMonitorId, live.MonitorId, StringComparison.OrdinalIgnoreCase);

    private static string SafeFileStem(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFileNameWithoutExtension(path); }
        catch { return ""; }
    }

    private static double? GeometrySimilarity(WindowIdentityBounds saved, WindowIdentityBounds live)
    {
        if (!saved.IsValid || !live.IsValid) return null;
        double width = (double)Math.Min(saved.Width, live.Width) / Math.Max(saved.Width, live.Width);
        double height = (double)Math.Min(saved.Height, live.Height) / Math.Max(saved.Height, live.Height);
        double savedCenterX = (saved.Left + saved.Right) / 2.0;
        double savedCenterY = (saved.Top + saved.Bottom) / 2.0;
        double liveCenterX = (live.Left + live.Right) / 2.0;
        double liveCenterY = (live.Top + live.Bottom) / 2.0;
        double distance = Math.Sqrt(
            Math.Pow(savedCenterX - liveCenterX, 2) +
            Math.Pow(savedCenterY - liveCenterY, 2));
        double diagonal = Math.Sqrt(Math.Pow(saved.Width, 2) + Math.Pow(saved.Height, 2));
        double position = 1 - Math.Min(1, distance / Math.Max(1, diagonal * 2));
        return (width + height + position) / 3;
    }

    private static WindowMatchCandidate Rejected(
        SavedWindowIdentity saved,
        LiveWindowIdentity live,
        List<WindowMatchEvidence> evidence,
        WindowMatchEvidenceKind kind,
        string explanation)
    {
        Add(evidence, kind, false, 0, explanation);
        return Candidate(saved, live, false, 0, WindowMatchConfidence.Ineligible, evidence, null);
    }

    private static WindowMatchCandidate Candidate(
        SavedWindowIdentity saved,
        LiveWindowIdentity live,
        bool eligible,
        double score,
        WindowMatchConfidence confidence,
        List<WindowMatchEvidence> evidence,
        double? titleSimilarity) => new(
            saved.EntryId,
            live.Hwnd,
            live.ProcessId,
            eligible,
            score,
            confidence,
            evidence.ToArray(),
            titleSimilarity,
            live.Title,
            live.ProcessName,
            live.WindowClassName,
            live.MonitorId,
            live.Bounds,
            WindowIdentityExtractor.ToHint(live));

    private static WindowMatchCandidate ApplyMinimumScore(
        WindowMatchCandidate candidate,
        WindowMatchPolicy policy)
    {
        if (!candidate.IsEligible || candidate.Score >= policy.MinimumCandidateScore)
            return candidate;
        return candidate with
        {
            IsEligible = false,
            Confidence = WindowMatchConfidence.Ineligible,
            Evidence = candidate.Evidence.Append(new WindowMatchEvidence(
                WindowMatchEvidenceKind.NoSupportedFallback,
                false,
                0,
                $"The candidate score is below the {policy.MinimumCandidateScore:0}-point minimum.")).ToArray()
        };
    }

    private static WindowMatchCandidate ApplyLearnedHint(
        WindowMatchCandidate candidate,
        WindowIdentityHint? learnedHint,
        WindowMatchPolicy policy)
    {
        if (!candidate.IsEligible || learnedHint is null ||
            !MatchesHint(learnedHint, candidate.IdentityHint))
        {
            return candidate;
        }

        WindowMatchEvidence[] evidence = candidate.Evidence.Append(new WindowMatchEvidence(
            WindowMatchEvidenceKind.LearnedIdentityHint,
            true,
            policy.LearnedHintBonus,
            "A remembered choice for this workspace entry matches this composite identity.")).ToArray();
        return candidate with
        {
            Score = candidate.Score + policy.LearnedHintBonus,
            Confidence = candidate.Confidence == WindowMatchConfidence.Exact
                ? WindowMatchConfidence.Exact
                : WindowMatchConfidence.Strong,
            Evidence = evidence,
            IsLearnedHintMatch = true
        };
    }

    /// <summary>
    /// Compares composite persisted identities. At least executable/class or a stronger application
    /// identity must anchor the comparison; title tokens can only refine that anchor.
    /// </summary>
    public static bool MatchesHint(WindowIdentityHint expected, WindowIdentityHint observed)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observed);
        bool strongerIdentity = SameNonEmpty(expected.AppUserModelId, observed.AppUserModelId) ||
            SameNonEmpty(expected.PwaIdentity, observed.PwaIdentity) ||
            SameNonEmpty(expected.PackageFamilyName, observed.PackageFamilyName) ||
            SameNonEmpty(expected.BrowserSiteHost, observed.BrowserSiteHost) ||
            SameNonEmpty(expected.FolderPath, observed.FolderPath);
        bool executableAndClass = SameNonEmpty(expected.ExecutablePath, observed.ExecutablePath) &&
            SameNonEmpty(expected.WindowClassName, observed.WindowClassName);
        if (!strongerIdentity && !executableAndClass)
            return false;

        if (!CompatibleOptional(expected.ExecutablePath, observed.ExecutablePath) ||
            !CompatibleOptional(expected.ProcessName, observed.ProcessName) ||
            !CompatibleOptional(expected.WindowClassName, observed.WindowClassName) ||
            !CompatibleOptional(expected.AppUserModelId, observed.AppUserModelId) ||
            !CompatibleOptional(expected.PackageFamilyName, observed.PackageFamilyName) ||
            !CompatibleOptional(expected.FolderPath, observed.FolderPath) ||
            !CompatibleOptional(expected.BrowserFamily, observed.BrowserFamily) ||
            !CompatibleOptional(expected.BrowserSiteHost, observed.BrowserSiteHost) ||
            !CompatibleOptional(expected.PwaIdentity, observed.PwaIdentity))
        {
            return false;
        }

        string[] expectedTokens = expected.TitleTokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToArray();
        if (expectedTokens.Length == 0) return true;
        var observedTokens = observed.TitleTokens.ToHashSet(StringComparer.Ordinal);
        int overlap = expectedTokens.Count(observedTokens.Contains);
        return overlap >= Math.Max(1, (int)Math.Ceiling(expectedTokens.Length * 0.75));
    }

    private static bool SameNonEmpty(string expected, string observed) =>
        !string.IsNullOrWhiteSpace(expected) &&
        string.Equals(expected, observed, StringComparison.OrdinalIgnoreCase);

    private static bool CompatibleOptional(string expected, string observed) =>
        string.IsNullOrWhiteSpace(expected) ||
        string.Equals(expected, observed, StringComparison.OrdinalIgnoreCase);

    private static void Add(
        List<WindowMatchEvidence>? evidence,
        WindowMatchEvidenceKind kind,
        bool matched,
        double score,
        string explanation) =>
        evidence?.Add(new WindowMatchEvidence(kind, matched, score, explanation));
}
