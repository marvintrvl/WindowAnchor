using System;
using System.Collections.Generic;
using System.Linq;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Pure planner policies kept separate from action construction and placement math.
/// These helpers intentionally do not know about execution or native APIs.
/// </summary>
internal static class RestorePlannerPolicies
{
    internal static bool IsStoreApp(WorkspaceEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.AppUserModelId) &&
        entry.AppUserModelId.Contains('!') &&
        entry.ExecutablePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    internal static bool IsBrowserProcess(string? processName) =>
        ProcessIdentityNormalizer.Normalize(processName) is "chrome" or "msedge" or "opera" or "brave";

    internal static bool IsApplicationRunning(
        WorkspaceEntry entry,
        IEnumerable<RunningApplicationIdentity> runningApplications,
        string? effectiveExecutablePath = null)
    {
        string expectedPath = WindowIdentityExtractor.NormalizePath(
            string.IsNullOrWhiteSpace(effectiveExecutablePath)
                ? entry.ExecutablePath
                : effectiveExecutablePath);
        string expectedProcess = ProcessIdentityNormalizer.Normalize(entry.ProcessName);
        string expectedAumid = entry.AppUserModelId?.Trim() ?? "";
        return runningApplications.Any(application =>
        {
            string observedPath = WindowIdentityExtractor.NormalizePath(application.ExecutablePath);
            string observedProcess = ProcessIdentityNormalizer.Normalize(application.ProcessName);
            string observedAumid = application.AppUserModelId?.Trim() ?? "";
            if (expectedAumid.Length > 0 && observedAumid.Length > 0 &&
                string.Equals(expectedAumid, observedAumid, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (expectedPath.Length > 0 && observedPath.Length > 0)
                return string.Equals(expectedPath, observedPath, StringComparison.OrdinalIgnoreCase);

            // Process names are deliberately a fallback only when one side lacks a usable path;
            // equal filenames from two different installations are not sufficient identity.
            return expectedProcess.Length > 0 && observedProcess.Length > 0 &&
                   string.Equals(expectedProcess, observedProcess, StringComparison.OrdinalIgnoreCase);
        });
    }
}
