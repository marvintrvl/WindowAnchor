using System;

namespace WindowAnchor.Services;

/// <summary>
/// Produces the canonical process-name key shared by matching, planning, readiness, and execution.
/// Display text must continue to use the original process name rather than this normalized value.
/// </summary>
internal static class ProcessIdentityNormalizer
{
    internal static string Normalize(string? processName)
    {
        string value = (processName ?? "").Trim().ToLowerInvariant();
        return value.EndsWith(".exe", StringComparison.Ordinal)
            ? value[..^4]
            : value;
    }
}
