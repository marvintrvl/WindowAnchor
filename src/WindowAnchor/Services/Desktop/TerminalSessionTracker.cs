using System;
using System.IO;
using System.Text.Json;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Reads opt-in PowerShell prompt reports keyed by WT_SESSION.</summary>
internal static class TerminalSessionTracker
{
    private sealed class Report
    {
        public string? SessionId { get; set; }
        public string? ProfileId { get; set; }
        public string? Directory { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    internal static TerminalTab? Read(string sessionPrefix, string? reportDirectory = null)
    {
        if (sessionPrefix.Length != 12 ||
            !System.Text.RegularExpressions.Regex.IsMatch(sessionPrefix, "^[0-9a-fA-F]{12}$"))
            return null;
        string root = reportDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowAnchor", "TerminalSessions");
        try
        {
            string[] candidates = Directory.Exists(root)
                ? Directory.GetFiles(root, sessionPrefix + "*.json")
                : Array.Empty<string>();
            if (candidates.Length != 1) return null;
            string path = candidates[0];
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out Guid id)) return null;
            var report = JsonSerializer.Deserialize<Report>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (report is null || !Guid.TryParse(report.SessionId, out Guid reportedId) ||
                reportedId != id || string.IsNullOrWhiteSpace(report.Directory) ||
                !Guid.TryParse(report.ProfileId, out Guid profileId))
                return null;
            return new TerminalTab
            {
                Profile = "{" + profileId.ToString("D") + "}",
                StartingDirectory = report.Directory
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLogger.Debug("terminal.session_report_invalid", "Could not read a Terminal session report", ex);
            return null;
        }
    }
}
