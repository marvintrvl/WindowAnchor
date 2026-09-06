using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindowAnchor.Services;

/// <summary>
/// Resolves a missing executable from a Squirrel-style app-version directory to the newest
/// installed sibling. Resolution is deliberately constrained to a recognized install root,
/// immediate <c>app-&lt;version&gt;</c> children, and the exact saved executable file name.
/// </summary>
internal static class VersionedExecutableResolver
{
    internal static bool TryResolve(string savedExecutablePath, out string resolvedPath)
    {
        resolvedPath = "";
        if (string.IsNullOrWhiteSpace(savedExecutablePath)) return false;

        try
        {
            string savedFullPath = Path.GetFullPath(savedExecutablePath);
            string? versionDirectory = Path.GetDirectoryName(savedFullPath);
            string? installRoot = versionDirectory is null
                ? null
                : Path.GetDirectoryName(versionDirectory);
            string executableName = Path.GetFileName(savedFullPath);
            if (versionDirectory is null || installRoot is null || executableName.Length == 0 ||
                !TryParseVersionDirectory(Path.GetFileName(versionDirectory), out _, out _) ||
                !File.Exists(Path.Combine(installRoot, "Update.exe")))
            {
                return false;
            }

            string rootFullPath = Path.GetFullPath(installRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string rootPrefix = rootFullPath + Path.DirectorySeparatorChar;
            var candidates = new List<Candidate>();
            foreach (string directory in Directory.EnumerateDirectories(
                         rootFullPath,
                         "app-*",
                         SearchOption.TopDirectoryOnly))
            {
                string fullDirectory = Path.GetFullPath(directory);
                if (!fullDirectory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !TryParseVersionDirectory(
                        Path.GetFileName(fullDirectory),
                        out Version? version,
                        out bool stable))
                {
                    continue;
                }

                string candidatePath = Path.GetFullPath(Path.Combine(fullDirectory, executableName));
                if (candidatePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(candidatePath))
                {
                    candidates.Add(new Candidate(candidatePath, version!, stable));
                }
            }

            Candidate? selected = candidates
                .OrderByDescending(candidate => candidate.Version)
                .ThenByDescending(candidate => candidate.IsStable)
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (selected is null) return false;

            resolvedPath = selected.Path;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryParseVersionDirectory(
        string? directoryName,
        out Version? version,
        out bool stable)
    {
        version = null;
        stable = false;
        if (string.IsNullOrWhiteSpace(directoryName) ||
            !directoryName.StartsWith("app-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string versionText = directoryName[4..];
        int metadataIndex = versionText.IndexOf('+');
        if (metadataIndex >= 0)
            versionText = versionText[..metadataIndex];
        int prereleaseIndex = versionText.IndexOf('-');
        stable = prereleaseIndex < 0;
        string numericText = stable ? versionText : versionText[..prereleaseIndex];
        string[] components = numericText.Split('.', StringSplitOptions.None);
        if (components.Length is < 2 or > 4) return false;

        var values = new int[4];
        for (int index = 0; index < components.Length; index++)
        {
            if (!int.TryParse(components[index], out values[index]) || values[index] < 0)
                return false;
        }

        version = new Version(values[0], values[1], values[2], values[3]);
        return true;
    }

    private sealed record Candidate(string Path, Version Version, bool IsStable);
}
