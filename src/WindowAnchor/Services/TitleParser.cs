using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace WindowAnchor.Services;

/// <summary>
/// Stateless Tier-1 file-path extractor. Applies per-application regular expressions
/// to a window title and returns the best-guess file path with a confidence score.
/// </summary>
public static class TitleParser
{
    private static readonly Dictionary<string, string> AppTitlePatterns = new()
    {
        ["notepad"] = @"^(?<file>.+) - Notepad$",
        ["notepad++"] = @"^(?<file>.+) - Notepad\+\+$",
        ["code"] = @"^(?<file>.+) - Visual Studio Code$",
        ["devenv"] = @"^(?<file>.+) - Microsoft Visual Studio$",
        ["winword"] = @"^(?<file>.+) - Word$",
        ["excel"] = @"^(?<file>.+) - Excel$",
        ["powerpnt"] = @"^(?<file>.+) - PowerPoint$",
        ["mspaint"] = @"^(?<file>.+) - Paint$",
        ["wordpad"] = @"^(?<file>.+) - WordPad$",
        ["sublime_text"] = @"^(?<file>.+) - Sublime Text$",
        ["obsidian"] = @"^(?<file>.+) - Obsidian$",
        ["typora"] = @"^(?<file>.+) - Typora$"
    };

    /// <summary>
    /// Attempts to extract an open-file path from <paramref name="windowTitle"/> using
    /// a known pattern for <paramref name="processName"/>.
    /// </summary>
    /// <param name="processName">The process name without extension (e.g. <c>"notepad"</c>).</param>
    /// <param name="windowTitle">The full window title string.</param>
    /// <returns>
    /// A tuple of the extracted path (or <c>null</c>) and a confidence score
    /// from 0 to 100. A score of 90 means a rooted path was verified on disk;
    /// 40 means only a bare filename was found.
    /// </returns>
    public static (string? filePath, int confidence) ExtractFilePath(string processName, string windowTitle)
    {
        string normalizedProc = processName.ToLower().Replace(".exe", "");

        if (!AppTitlePatterns.TryGetValue(normalizedProc, out string? pattern))
        {
            return (null, 0);
        }

        var match = Regex.Match(windowTitle, pattern);
        if (!match.Success) return (null, 0);

        string rawFile = match.Groups["file"].Value.Trim('*', ' ', '●', '•');

        // Check if it's a rooted path
        if (Path.IsPathRooted(rawFile) && File.Exists(rawFile))
        {
            return (rawFile, 90);
        }

        // If it's just a filename, return with lower confidence
        if (!rawFile.Contains(Path.DirectorySeparatorChar) && !rawFile.Contains(Path.AltDirectorySeparatorChar))
        {
            return (rawFile, 40);
        }

        return (null, 0);
    }
}
