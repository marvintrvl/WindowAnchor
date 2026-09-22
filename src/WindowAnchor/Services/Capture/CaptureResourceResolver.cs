using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Result of resource discovery for one ordinary captured window.</summary>
internal sealed record CapturedResource(
    string? FilePath,
    int Confidence,
    string Source,
    string? LaunchArgument);

/// <summary>
/// Resolves title-derived files, Jump List candidates, and bounded common-folder matches.
/// The resolver owns no capture or persistence policy; one search budget is supplied by the
/// snapshot builder and shared by every window in that capture.
/// </summary>
internal sealed class CaptureResourceResolver
{
    private readonly JumpListService _jumpListService;

    internal CaptureResourceResolver(JumpListService jumpListService) =>
        _jumpListService = jumpListService ?? throw new ArgumentNullException(nameof(jumpListService));

    internal CaptureResourceSearchBudget CreateSearchBudget(
        bool enabled,
        TimeSpan limit,
        CancellationToken cancellationToken) =>
        new(enabled, limit, cancellationToken);

    internal void BuildSnapshotCache() => _jumpListService.BuildSnapshotCache();

    internal void ClearSnapshotCache() => _jumpListService.ClearSnapshotCache();

    internal CapturedResource Resolve(
        WindowRecord window,
        bool saveFiles,
        CaptureResourceSearchBudget folderSearchBudget,
        IProgress<SaveProgressReport>? progress,
        int progressCurrent,
        int progressTotal,
        bool buildFullJumpListCache)
    {
        if (!saveFiles)
            return new CapturedResource(null, 0, "NONE", null);

        Stopwatch detectionTimer = Stopwatch.StartNew();
        AppLogger.Debug(
            "file_detection.started",
            "Started file detection for a window",
            LogField.Public("processName", window.ProcessName),
            LogField.Title("windowTitle", window.TitleSnippet),
            LogField.Path("executablePath", window.ExecutablePath));

        var (filePath, titleConfidence) =
            TitleParser.ExtractFilePath(window.ProcessName, window.TitleSnippet);
        int confidence = titleConfidence;
        string source = titleConfidence > 0 ? "TITLE_PARSE" : "NONE";

        if (titleConfidence > 0)
        {
            AppLogger.Debug(
                "file_detection.title_match",
                "Matched a file from the window title",
                LogField.Public("confidence", titleConfidence),
                LogField.Path("filePath", filePath));
        }
        else
        {
            AppLogger.Debug(
                "file_detection.title_no_match",
                "Window title did not produce a file match");
        }

        if (buildFullJumpListCache &&
            confidence == 40 &&
            !string.IsNullOrEmpty(filePath) &&
            !Path.IsPathRooted(filePath))
        {
            try
            {
                List<string> candidates = GetRecentFiles(window.ExecutablePath, maxFiles: 50);
                AppLogger.Debug(
                    "file_detection.jump_list_exact_started",
                    "Searching the jump list for an exact filename",
                    LogField.Public("candidateCount", candidates.Count));
                string? exact = candidates.FirstOrDefault(candidate =>
                    Path.GetFileName(candidate).Equals(filePath, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    filePath = exact;
                    confidence = 90;
                    source = "JUMPLIST_EXACT";
                    AppLogger.Debug(
                        "file_detection.jump_list_exact_match",
                        "Matched an exact filename in the jump list",
                        LogField.Path("filePath", exact));
                }
                else
                {
                    AppLogger.Debug(
                        "file_detection.jump_list_exact_no_match",
                        "No exact filename match was found in the jump list");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "file_detection.jump_list_exact_failed",
                    "Exact jump-list matching failed",
                    ex,
                    LogField.Public("errorCategory", "jump_list_exact"));
            }
        }

        if (buildFullJumpListCache &&
            confidence < 80 &&
            !string.IsNullOrEmpty(window.ExecutablePath))
        {
            try
            {
                List<string> candidates = GetRecentFiles(window.ExecutablePath, maxFiles: 30);
                AppLogger.Debug(
                    "file_detection.jump_list_loaded",
                    "Loaded jump-list candidates",
                    LogField.Public("candidateCount", candidates.Count));
                foreach (string candidate in candidates)
                {
                    AppLogger.Debug(
                        "file_detection.jump_list_candidate",
                        "Found a jump-list candidate",
                        LogField.Path("filePath", candidate));
                }

                if (candidates.Count > 0)
                {
                    string titleLower = window.TitleSnippet.ToLowerInvariant();
                    string? best = candidates
                        .Where(candidate =>
                        {
                            string name = Path.GetFileName(candidate);
                            string stem = Path.GetFileNameWithoutExtension(candidate);
                            if (stem.Length < 3) return false;
                            return titleLower.Contains(name.ToLowerInvariant()) ||
                                   titleLower.Contains(stem.ToLowerInvariant());
                        })
                        .OrderByDescending(candidate => Path.GetFileNameWithoutExtension(candidate).Length)
                        .FirstOrDefault();

                    if (best != null)
                    {
                        filePath = best;
                        confidence = 80;
                        source = "JUMPLIST";
                        AppLogger.Debug(
                            "file_detection.jump_list_match",
                            "Matched a jump-list candidate",
                            LogField.Path("filePath", best));
                    }
                    else
                    {
                        AppLogger.Debug(
                            "file_detection.jump_list_no_match",
                            "No jump-list candidate matched the window title",
                            LogField.Title("windowTitle", window.TitleSnippet));
                        foreach (string candidate in candidates)
                        {
                            string stem = Path.GetFileNameWithoutExtension(candidate);
                            AppLogger.Debug(
                                "file_detection.jump_list_no_match_detail",
                                "Recorded jump-list comparison detail",
                                LogField.Title("candidateStem", stem),
                                LogField.Public(
                                    "titleContainsCandidate",
                                    titleLower.Contains(stem.ToLowerInvariant())));
                        }
                    }
                }
                else
                {
                    AppLogger.Debug(
                        "file_detection.jump_list_empty",
                        "No jump-list candidates were available");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "file_detection.jump_list_failed",
                    "Jump-list file detection failed",
                    ex,
                    LogField.Public("errorCategory", "jump_list"));
            }
        }

        if (confidence < 80 &&
            IsPlausibleBareFileName(filePath) &&
            folderSearchBudget.CanSearch)
        {
            progress?.Report(new SaveProgressReport(
                progressCurrent,
                progressTotal,
                $"Searching files for {window.ProcessName}…",
                filePath!,
                WorkspaceCaptureProgressStage.SearchingCommonFolders));
            AppLogger.Debug(
                "file_detection.folder_search_started",
                "Searching common folders for a filename",
                LogField.Path("fileName", filePath));
            try
            {
                Stopwatch searchTimer = Stopwatch.StartNew();
                string? found = SearchFileInCommonLocations(
                    filePath!,
                    folderSearchBudget,
                    out bool timedOut);
                searchTimer.Stop();
                if (found != null)
                {
                    filePath = found;
                    confidence = 85;
                    source = "FILE_SEARCH";
                    AppLogger.Debug(
                        "file_detection.folder_search_match",
                        "Found a matching file in a common folder",
                        LogField.Path("filePath", found),
                        LogField.Public("durationMs", searchTimer.Elapsed.TotalMilliseconds));
                }
                else if (timedOut)
                {
                    AppLogger.Warn(
                        "file_detection.folder_search_timed_out",
                        "Common-folder search reached the global capture budget",
                        LogField.Public("durationMs", searchTimer.Elapsed.TotalMilliseconds),
                        LogField.Public("budgetMs", folderSearchBudget.Limit.TotalMilliseconds));
                }
                else
                {
                    AppLogger.Debug(
                        "file_detection.folder_search_no_match",
                        "Common-folder search found zero or ambiguous matches",
                        LogField.Public("durationMs", searchTimer.Elapsed.TotalMilliseconds));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "file_detection.folder_search_failed",
                    "Common-folder file detection failed",
                    ex,
                    LogField.Public("errorCategory", "folder_search"));
            }
        }

        string? launchArgument = confidence >= 80 ? filePath : null;
        bool isVsCodeLike = window.ProcessName.Equals("Code", StringComparison.OrdinalIgnoreCase) ||
                            window.ProcessName.Equals("Cursor", StringComparison.OrdinalIgnoreCase);
        if (isVsCodeLike && launchArgument != null)
        {
            if (File.Exists(launchArgument) &&
                !launchArgument.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase))
            {
                launchArgument = Path.GetDirectoryName(launchArgument);
            }
        }

        detectionTimer.Stop();
        AppLogger.Debug(
            "file_detection.completed",
            "Completed file detection for a window",
            LogField.Public("source", source),
            LogField.Public("confidence", confidence),
            LogField.Path("launchArgument", launchArgument),
            LogField.Public("durationMs", detectionTimer.Elapsed.TotalMilliseconds));

        return new CapturedResource(filePath, confidence, source, launchArgument);
    }

    private List<string> GetRecentFiles(string executablePath, int maxFiles) =>
        _jumpListService.GetRecentFilesForApp(executablePath, maxFiles);

    /// <summary>
    /// Searches common user-accessible locations. Exactly one match is required; multiple matches
    /// are ambiguous and deliberately produce no resource.
    /// </summary>
    internal static string? SearchFileInCommonLocations(
        string filename,
        CaptureResourceSearchBudget budget,
        out bool timedOut)
    {
        var searchRoots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };

        foreach (string environmentVariable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            string value = Environment.GetEnvironmentVariable(environmentVariable) ?? "";
            if (!string.IsNullOrEmpty(value) && Directory.Exists(value))
                searchRoots.Add(value);
        }

        return SearchFileInCommonLocations(filename, searchRoots, budget, out timedOut);
    }

    /// <summary>Deterministic search-root overload used by service-level equivalence tests.</summary>
    internal static string? SearchFileInCommonLocations(
        string filename,
        IEnumerable<string> searchRoots,
        CaptureResourceSearchBudget budget,
        out bool timedOut)
    {
        ArgumentNullException.ThrowIfNull(searchRoots);
        timedOut = false;

        var matches = new List<string>();
        budget.StartMeasuring();
        try
        {
            foreach (string root in searchRoots
                         .Where(Directory.Exists)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                SearchDirectoryRecursive(root, filename, matches, budget, ref timedOut);
                if (matches.Count > 1 || timedOut) return null;
            }
        }
        finally
        {
            budget.StopMeasuring();
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private static void SearchDirectoryRecursive(
        string directory,
        string filename,
        List<string> matches,
        CaptureResourceSearchBudget budget,
        ref bool timedOut)
    {
        if (!budget.CanContinue)
        {
            timedOut = true;
            return;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, filename))
            {
                if (!matches.Contains(file, StringComparer.OrdinalIgnoreCase))
                    matches.Add(file);
                if (matches.Count > 1) return;
            }
        }
        catch
        {
            // Cloud placeholders and inaccessible folders are isolated to this directory.
        }

        if (matches.Count > 1) return;

        IEnumerable<string> subdirectories;
        try
        {
            subdirectories = Directory.EnumerateDirectories(directory).ToList();
        }
        catch
        {
            return;
        }

        foreach (string subdirectory in subdirectories)
        {
            SearchDirectoryRecursive(subdirectory, filename, matches, budget, ref timedOut);
            if (matches.Count > 1 || timedOut) return;
        }
    }

    private static bool IsPlausibleBareFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            return false;
        if (!string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal))
            return false;
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        string extension = Path.GetExtension(value);
        return extension.Length is >= 2 and <= 20 &&
               extension.Skip(1).All(character =>
                   char.IsLetterOrDigit(character) || character is '-' or '_');
    }
}

/// <summary>One cumulative stopwatch budget shared by all common-folder searches in a capture.</summary>
internal sealed class CaptureResourceSearchBudget
{
    private readonly bool _enabled;
    private readonly CancellationToken _cancellationToken;
    private readonly Stopwatch _stopwatch = new();

    internal CaptureResourceSearchBudget(
        bool enabled,
        TimeSpan limit,
        CancellationToken cancellationToken)
    {
        _enabled = enabled;
        Limit = limit > TimeSpan.Zero ? limit : TimeSpan.Zero;
        _cancellationToken = cancellationToken;
    }

    internal TimeSpan Limit { get; }

    internal bool CanSearch => CanContinue;

    internal bool CanContinue
    {
        get
        {
            _cancellationToken.ThrowIfCancellationRequested();
            return _enabled && _stopwatch.Elapsed < Limit;
        }
    }

    internal void StartMeasuring()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_enabled && !_stopwatch.IsRunning)
            _stopwatch.Start();
    }

    internal void StopMeasuring()
    {
        if (_stopwatch.IsRunning)
            _stopwatch.Stop();
    }
}
