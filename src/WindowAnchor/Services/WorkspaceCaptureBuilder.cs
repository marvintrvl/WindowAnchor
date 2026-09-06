using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Immutable options for one snapshot-construction pass.</summary>
internal sealed record WorkspaceCaptureRequest(
    string Name,
    bool SaveFiles,
    HashSet<string>? MonitorIds,
    IProgress<SaveProgressReport>? Progress,
    List<WindowRecord>? SelectedWindows,
    bool CaptureBrowserSessions,
    bool SearchCommonFolders,
    TimeSpan? CommonFolderSearchBudget,
    CancellationToken CancellationToken,
    bool BuildFullJumpListCache);

/// <summary>
/// Coordinates snapshot construction and optional browser enrichment without persisting data.
/// The snapshot delegate keeps this coordinator independent of monitor, entry, resource, and
/// persistence policy; production supplies the focused <see cref="WorkspaceSnapshotBuilder"/>.
/// </summary>
internal sealed class WorkspaceCaptureBuilder
{
    private readonly IBrowserSessionConnector? _browserSessionConnector;

    internal WorkspaceCaptureBuilder(IBrowserSessionConnector? browserSessionConnector) =>
        _browserSessionConnector = browserSessionConnector;

    internal async Task<WorkspaceCaptureResult> CaptureAsync(
        WorkspaceCaptureRequest request,
        Func<WorkspaceCaptureRequest, WorkspaceSnapshot> buildSnapshot)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(buildSnapshot);

        WorkspaceSnapshot snapshot = await Task.Run(
            () => buildSnapshot(request),
            request.CancellationToken).ConfigureAwait(false);
        request.CancellationToken.ThrowIfCancellationRequested();

        BrowserCaptureResult browserCapture = request.CaptureBrowserSessions
            ? await CaptureBrowserSessionAsync(request, snapshot).ConfigureAwait(false)
            : BrowserCaptureResult.Empty(
                BrowserCaptureStatus.Skipped,
                "Browser capture was disabled by the caller.");
        snapshot.BrowserSessions = browserCapture.Sessions.ToList();
        request.Progress?.Report(new SaveProgressReport(
            snapshot.Entries.Count,
            snapshot.Entries.Count,
            "Finalizing workspace…",
            "",
            WorkspaceCaptureProgressStage.Finalizing));
        return new WorkspaceCaptureResult(snapshot, browserCapture);
    }

    private async Task<BrowserCaptureResult> CaptureBrowserSessionAsync(
        WorkspaceCaptureRequest request,
        WorkspaceSnapshot snapshot)
    {
        List<string> browserTitles = snapshot.Entries
            .Where(entry => IsBrowserProcess(entry.ProcessName))
            .Select(entry => entry.Position.TitleSnippet)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (browserTitles.Count == 0)
        {
            return BrowserCaptureResult.Empty(
                BrowserCaptureStatus.Skipped,
                "No selected browser windows required session capture.");
        }
        if (_browserSessionConnector == null)
        {
            return BrowserCaptureResult.Empty(
                BrowserCaptureStatus.Unavailable,
                "No browser session connector is configured.");
        }

        try
        {
            request.Progress?.Report(new SaveProgressReport(
                snapshot.Entries.Count,
                snapshot.Entries.Count,
                "Capturing browser session…",
                $"{browserTitles.Count} browser window{(browserTitles.Count == 1 ? "" : "s")}",
                WorkspaceCaptureProgressStage.CapturingBrowserSession));
            return await _browserSessionConnector.CaptureAsync(
                request.Name,
                browserTitles,
                request.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "workspace.browser_capture_failed",
                "Browser session capture failed",
                ex,
                LogField.Workspace("workspaceName", request.Name),
                LogField.Public("errorCategory", "browser_capture"));
            return BrowserCaptureResult.Empty(
                BrowserCaptureStatus.Failed,
                ex.Message);
        }
    }

    private static bool IsBrowserProcess(string processName) =>
        ProcessIdentityNormalizer.Normalize(processName) is "chrome" or "msedge" or "opera" or "brave";
}
