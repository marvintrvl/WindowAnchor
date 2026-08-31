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
    Ineligible,
    Weak,
    Probable,
    Strong,
    Exact
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
    bool IsTopScoreTie = false);

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

    private static string PackageFamily(string? appUserModelId)
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
        IEnumerable<LiveWindowIdentity> liveWindows)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(liveWindows);

        LiveWindowIdentity[] live = liveWindows
            .OrderBy(candidate => candidate.Hwnd.ToInt64())
            .ToArray();
        int compatibleSameExecutableCount = live.Count(candidate =>
            KindCompatible(saved, candidate) && ExecutableExact(saved, candidate));

        WindowMatchCandidate[] ordered = live
            .Select(candidate => Evaluate(saved, candidate, compatibleSameExecutableCount))
            .OrderByDescending(candidate => candidate.IsEligible)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Hwnd.ToInt64())
            .ToArray();

        WindowMatchCandidate? best = ordered.FirstOrDefault(candidate => candidate.IsEligible);
        if (best == null) return ordered;

        int topCount = ordered.Count(candidate =>
            candidate.IsEligible && Math.Abs(candidate.Score - best.Score) < 0.000001);
        if (topCount < 2) return ordered;

        return ordered
            .Select(candidate => candidate.IsEligible &&
                                 Math.Abs(candidate.Score - best.Score) < 0.000001
                ? candidate with { IsTopScoreTie = true }
                : candidate)
            .ToArray();
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
        int compatibleSameExecutableCount)
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
            Add(evidence, WindowMatchEvidenceKind.TitleSimilarity, true, score,
                "Multiple same-executable windows were ranked by title similarity.");
            return Candidate(
                saved,
                live,
                true,
                score,
                similarity >= 0.65 ? WindowMatchConfidence.Strong : WindowMatchConfidence.Weak,
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
            return Candidate(saved, live, true, 4000, WindowMatchConfidence.Weak, evidence, null);
        }

        double? geometry = GeometrySimilarity(saved.PreviousBounds, live.Bounds);
        bool sameMonitor = MonitorExact(saved, live);
        if (sameMonitor || geometry >= 0.5)
        {
            double contribution = 3000 + ((geometry ?? 0) * 500);
            if (geometry != null)
                Add(evidence, WindowMatchEvidenceKind.GeometrySimilarity, true, contribution,
                    "Previous and live bounds provide weak geometric context.");
            else
                Add(evidence, WindowMatchEvidenceKind.MonitorIdExact, true, contribution,
                    "Saved and live monitor identities provide weak context.");
            return Candidate(saved, live, true, contribution, WindowMatchConfidence.Weak, evidence, null);
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
            titleSimilarity);

    private static void Add(
        List<WindowMatchEvidence>? evidence,
        WindowMatchEvidenceKind kind,
        bool matched,
        double score,
        string explanation) =>
        evidence?.Add(new WindowMatchEvidence(kind, matched, score, explanation));
}
