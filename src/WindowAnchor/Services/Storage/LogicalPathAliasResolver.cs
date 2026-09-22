using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WindowAnchor.Services;

/// <summary>Pure conversion between local absolute paths and portable logical-root paths.</summary>
internal static partial class LogicalPathAliasResolver
{
    internal static string? ToLogicalPath(string? absolutePath, IReadOnlyDictionary<string, string>? aliases)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || aliases is null) return null;
        string path = NormalizePath(absolutePath);
        return aliases
            .Where(pair => IsValidAlias(pair.Key) && IsAbsoluteRoot(pair.Value))
            .Select(pair => (Alias: NormalizeAlias(pair.Key), Root: NormalizePath(pair.Value)))
            .Where(pair => IsUnderRoot(path, pair.Root))
            .OrderByDescending(pair => pair.Root.Length)
            .ThenBy(pair => pair.Alias, StringComparer.Ordinal)
            .Select(pair => $"${{{pair.Alias}}}{path[pair.Root.Length..]}")
            .FirstOrDefault();
    }

    internal static string? Resolve(string? logicalPath, IReadOnlyDictionary<string, string>? aliases)
    {
        if (string.IsNullOrWhiteSpace(logicalPath) || aliases is null) return null;
        Match match = LogicalPathRegex().Match(logicalPath);
        if (!match.Success) return null;
        string alias = NormalizeAlias(match.Groups["alias"].Value);
        KeyValuePair<string, string>? mapping = aliases
            .Where(pair => string.Equals(NormalizeAlias(pair.Key), alias, StringComparison.Ordinal))
            .Select(pair => (KeyValuePair<string, string>?)pair)
            .FirstOrDefault();
        if (mapping is null || !IsAbsoluteRoot(mapping.Value.Value)) return null;
        string remainder = match.Groups["remainder"].Value.TrimStart('\\', '/');
        return remainder.Length == 0
            ? NormalizePath(mapping.Value.Value)
            : $"{NormalizePath(mapping.Value.Value)}\\{remainder}";
    }

    internal static bool IsValidAlias(string? alias) => !string.IsNullOrWhiteSpace(alias) &&
        AliasNameRegex().IsMatch(alias.Trim());

    internal static string NormalizeAlias(string alias) => alias.Trim().ToUpperInvariant();

    private static bool IsAbsoluteRoot(string? path) => !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathRooted(path);

    private static string NormalizePath(string path) => path.Trim().TrimEnd('\\', '/');

    private static bool IsUnderRoot(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^\\$\\{(?<alias>[A-Za-z][A-Za-z0-9_]*)\\}(?<remainder>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LogicalPathRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex AliasNameRegex();
}
