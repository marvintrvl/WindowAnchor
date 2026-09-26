using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Routes browser-session operations through the connected browser-family native hosts.</summary>
public sealed class BrowserSessionBridge : IBrowserSessionConnector
{
    public const string ChromiumPipeName = "WindowAnchor.BrowserBridge";
    public const string FirefoxPipeName = "WindowAnchor.BrowserBridge.Firefox";
    public const string PipeName = ChromiumPipeName;
    public const int ProtocolVersion = 2;
    private const int ConnectionTimeoutMs = 350;
    // Native hosts apply their own 5-second extension timeout. Leave room for that response.
    private const int ResponseTimeoutMs = 6000;
    private readonly Func<int, BrowserTabRestorePolicy?> _resolveTabConflict;

    public BrowserSessionBridge(Func<int, BrowserTabRestorePolicy?>? resolveTabConflict = null) =>
        _resolveTabConflict = resolveTabConflict ?? ResolveTabConflict;

    public async Task<BrowserCaptureResult> CaptureAsync(
        string workspaceName,
        IEnumerable<BrowserCaptureTarget> selectedBrowserWindows,
        CancellationToken ct = default)
    {
        BrowserCaptureTarget[] targets = selectedBrowserWindows.ToArray();
        string[] pipes = targets
            .Select(target => PipeNameForBrowser(target.Browser))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (pipes.Length == 0)
            return BrowserCaptureResult.Empty(BrowserCaptureStatus.Skipped, "No supported browser windows were selected.");

        Stopwatch stopwatch = Stopwatch.StartNew();
        Task<BrowserCaptureResult>[] captures = pipes.Select(pipe => CaptureFromPipeAsync(
            pipe,
            workspaceName,
            targets.Where(target => PipeNameForBrowser(target.Browser) == pipe)
                .Select(target => target.Title)
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Distinct(StringComparer.OrdinalIgnoreCase),
            ct)).ToArray();
        BrowserCaptureResult result = AggregateCaptureResults(await Task.WhenAll(captures).ConfigureAwait(false));
        LogCaptureCompleted(workspaceName, result, stopwatch);
        return result;
    }

    public async Task<bool> RestoreAsync(
        string workspaceName,
        List<BrowserSession> sessions,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Stopwatch stopwatch = Stopwatch.StartNew();
        bool success = true;
        foreach (IGrouping<string, BrowserSession> route in sessions.GroupBy(
            session => PipeNameForBrowser(session.Browser),
            StringComparer.Ordinal))
        {
            success = await RestoreThroughPipeAsync(
                route.Key,
                workspaceName,
                route.ToList(),
                ct).ConfigureAwait(false) && success;
        }
        LogRestoreCompleted(workspaceName, sessions.Count, success, stopwatch);
        return success;
    }

    internal static string PipeNameForBrowser(string? browser) =>
        ProcessIdentityNormalizer.Normalize(browser) == "firefox" ? FirefoxPipeName : ChromiumPipeName;

    internal static BrowserCaptureResult AggregateCaptureResults(
        IEnumerable<BrowserCaptureResult> routeResults)
    {
        BrowserCaptureResult[] results = routeResults.ToArray();
        List<BrowserSession> sessions = results.SelectMany(result => result.Sessions).ToList();
        if (results.Length > 0 && results.All(result => result.Status == BrowserCaptureStatus.Captured))
            return BrowserCaptureResult.Captured(sessions);

        BrowserCaptureStatus status = results.Any(result => result.Status == BrowserCaptureStatus.Failed)
            ? BrowserCaptureStatus.Failed
            : results.Any(result => result.Status == BrowserCaptureStatus.TimedOut)
                ? BrowserCaptureStatus.TimedOut
                : BrowserCaptureStatus.Unavailable;
        string detail = string.Join(
            " ",
            results.Where(result => !string.IsNullOrWhiteSpace(result.Detail))
                .Select(result => result.Detail)
                .Distinct(StringComparer.Ordinal));
        return new BrowserCaptureResult(status, sessions, detail);
    }

    private static async Task<BrowserCaptureResult> CaptureFromPipeAsync(
        string pipeName,
        string workspaceName,
        IEnumerable<string> selectedBrowserTitles,
        CancellationToken ct)
    {
        try
        {
            using JsonDocument response = await SendAsync(pipeName, new
            {
                type = "capture",
                protocolVersion = ProtocolVersion,
                workspaceName,
                selectedBrowserTitles,
                requestId = Guid.NewGuid().ToString("N")
            }, ct).ConfigureAwait(false);
            return ParseCaptureResponse(response.RootElement);
        }
        catch (TimeoutException ex)
        {
            AppLogger.Debug(
                "browser_session.capture_timed_out",
                "Browser extension capture timed out",
                ex,
                LogField.Workspace("workspaceName", workspaceName),
                LogField.Public("connector", ConnectorName(pipeName)),
                LogField.Public("errorCategory", "browser_capture_timeout"));
            return BrowserCaptureResult.Empty(BrowserCaptureStatus.TimedOut, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Debug(
                "browser_session.capture_unavailable",
                "Browser extension capture was unavailable",
                ex,
                LogField.Workspace("workspaceName", workspaceName),
                LogField.Public("connector", ConnectorName(pipeName)),
                LogField.Public("errorCategory", "browser_capture_unavailable"));
            return BrowserCaptureResult.Empty(BrowserCaptureStatus.Unavailable, ex.Message);
        }
        catch (JsonException ex)
        {
            AppLogger.Debug(
                "browser_session.capture_invalid",
                "Browser extension returned invalid capture data",
                ex,
                LogField.Workspace("workspaceName", workspaceName),
                LogField.Public("connector", ConnectorName(pipeName)),
                LogField.Public("errorCategory", "browser_capture_invalid"));
            return BrowserCaptureResult.Empty(BrowserCaptureStatus.Failed, ex.Message);
        }
    }

    private async Task<bool> RestoreThroughPipeAsync(
        string pipeName,
        string workspaceName,
        List<BrowserSession> sessions,
        CancellationToken ct)
    {
        try
        {
            using JsonDocument response = await SendRestoreRequestAsync(
                pipeName,
                workspaceName,
                sessions,
                ct).ConfigureAwait(false);
            bool success = ParseRestoreResponse(response.RootElement);
            string[] conflictedSessionIds = GetConflictSessionIds(response.RootElement);
            if (success && conflictedSessionIds.Length > 0)
            {
                BrowserTabRestorePolicy? resolution = _resolveTabConflict(conflictedSessionIds.Length);
                BrowserSession[] conflictedSessions = sessions.Where(session =>
                    conflictedSessionIds.Contains(session.BrowserSessionId, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                if (resolution is not null && conflictedSessions.Length > 0)
                {
                    foreach (BrowserSession session in conflictedSessions)
                        session.RestorePolicy = resolution.Value;
                    using JsonDocument retry = await SendRestoreRequestAsync(
                        pipeName,
                        workspaceName,
                        conflictedSessions.ToList(),
                        ct).ConfigureAwait(false);
                    success = ParseRestoreResponse(retry.RootElement) &&
                        GetConflictSessionIds(retry.RootElement).Length == 0;
                }
            }
            return success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            AppLogger.Debug(
                "browser_session.restore_unavailable",
                "Browser extension restore was unavailable",
                ex,
                LogField.Workspace("workspaceName", workspaceName),
                LogField.Public("connector", ConnectorName(pipeName)),
                LogField.Public("errorCategory", "browser_restore_unavailable"));
            return false;
        }
    }

    private static string ConnectorName(string pipeName) =>
        pipeName == FirefoxPipeName ? "firefox" : "chromium";

    private static void LogCaptureCompleted(
        string workspaceName, BrowserCaptureResult result, Stopwatch stopwatch) =>
        AppLogger.Debug(
            "browser_session.capture_completed",
            "Completed browser session capture",
            LogField.Workspace("workspaceName", workspaceName),
            LogField.Public("status", result.Status.ToString()),
            LogField.Public("sessionCount", result.Sessions.Count),
            LogField.Public("durationMs", stopwatch.Elapsed.TotalMilliseconds));

    private static void LogRestoreCompleted(
        string workspaceName, int sessionCount, bool success, Stopwatch stopwatch) =>
        AppLogger.Debug(
            "browser_session.restore_completed",
            "Completed browser session restore request",
            LogField.Workspace("workspaceName", workspaceName),
            LogField.Public("sessionCount", sessionCount),
            LogField.Public("success", success),
            LogField.Public("durationMs", stopwatch.Elapsed.TotalMilliseconds));

    internal static BrowserCaptureResult ParseCaptureResponse(JsonElement root)
    {
        if (root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.False)
        {
            string detail = root.TryGetProperty("error", out JsonElement error)
                ? error.GetString() ?? "Browser session capture failed."
                : "Browser session capture failed.";
            return BrowserCaptureResult.Empty(
                detail.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                    ? BrowserCaptureStatus.TimedOut
                    : BrowserCaptureStatus.Failed,
                detail);
        }

        if (!HasCompatibleProtocolVersion(root))
        {
            return BrowserCaptureResult.Empty(
                BrowserCaptureStatus.Failed,
                "Browser extension protocol version is incompatible.");
        }
        if (!root.TryGetProperty("sessions", out JsonElement sessions))
            return BrowserCaptureResult.Captured([]);
        return BrowserCaptureResult.Captured(
            JsonSerializer.Deserialize<List<BrowserSession>>(sessions.GetRawText()) ?? []);
    }

    internal static bool ParseRestoreResponse(JsonElement root) =>
        HasCompatibleProtocolVersion(root) &&
        root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True;

    internal static string[] GetConflictSessionIds(JsonElement root)
    {
        if (!HasCompatibleProtocolVersion(root) ||
            !root.TryGetProperty("results", out JsonElement results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return results.EnumerateArray()
            .Where(result => result.TryGetProperty("status", out JsonElement status) &&
                status.ValueKind == JsonValueKind.String &&
                status.GetString()?.Equals("conflict", StringComparison.OrdinalIgnoreCase) == true)
            .Select(result => result.TryGetProperty("sessionId", out JsonElement id) ? id.GetString() ?? "" : "")
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HasCompatibleProtocolVersion(JsonElement root) =>
        root.TryGetProperty("protocolVersion", out JsonElement version) &&
        version.ValueKind == JsonValueKind.Number &&
        version.TryGetInt32(out int value) && value == ProtocolVersion;

    private static Task<JsonDocument> SendRestoreRequestAsync(
        string pipeName,
        string workspaceName,
        List<BrowserSession> sessions,
        CancellationToken ct) => SendAsync(pipeName, new
        {
            type = "restore",
            protocolVersion = ProtocolVersion,
            workspaceName,
            sessions,
            requestId = Guid.NewGuid().ToString("N")
        }, ct);

    private static BrowserTabRestorePolicy? ResolveTabConflict(int conflictCount)
    {
        if (Application.Current?.Dispatcher is null)
            return null;

        return Application.Current.Dispatcher.Invoke<BrowserTabRestorePolicy?>(() =>
        {
            string subject = conflictCount == 1 ? "A matching browser tab is" :
                $"{conflictCount} matching browser tabs are";
            MessageBoxResult result = MessageBox.Show(
                $"{subject} already open in the saved browser profile.\n\n" +
                "Yes reuses the existing tab. No opens a duplicate. Cancel leaves it unchanged.",
                "Matching Browser Tabs",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            return result switch
            {
                MessageBoxResult.Yes => BrowserTabRestorePolicy.ReuseMatchingTab,
                MessageBoxResult.No => BrowserTabRestorePolicy.OpenDuplicate,
                _ => null
            };
        });
    }

    private static async Task<JsonDocument> SendAsync(string pipeName, object request, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(request);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(ConnectionTimeoutMs);
        try
        {
            await client.ConnectAsync(Timeout.Infinite, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"The {ConnectorName(pipeName)} browser connector is not available.", ex);
        }

        using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        responseTimeout.CancelAfter(ResponseTimeoutMs);
        try
        {
            using var writer = new StreamWriter(client) { AutoFlush = true };
            using var reader = new StreamReader(client);
            await writer.WriteLineAsync(json).ConfigureAwait(false);
            string? line = await reader.ReadLineAsync(responseTimeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) throw new IOException("Browser host returned no response.");
            return JsonDocument.Parse(line);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Browser host did not respond within {ResponseTimeoutMs} ms.",
                ex);
        }
    }
}
