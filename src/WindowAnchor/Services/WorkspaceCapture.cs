using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Outcome of the optional browser-session portion of a workspace capture.</summary>
public enum BrowserCaptureStatus
{
    Captured,
    Unavailable,
    TimedOut,
    Skipped,
    Failed,
}

/// <summary>Browser metadata and its explicitly recorded capture outcome.</summary>
public sealed record BrowserCaptureResult(
    BrowserCaptureStatus Status,
    IReadOnlyList<BrowserSession> Sessions,
    string? Detail = null)
{
    public bool IsComplete => Status is BrowserCaptureStatus.Captured or BrowserCaptureStatus.Skipped;

    public static BrowserCaptureResult Captured(IReadOnlyList<BrowserSession> sessions) =>
        new(BrowserCaptureStatus.Captured, sessions);

    public static BrowserCaptureResult Empty(BrowserCaptureStatus status, string? detail = null) =>
        new(status, new List<BrowserSession>(), detail);
}

/// <summary>
/// Complete side-effect-free capture result. Persistence is a separate, explicit operation.
/// </summary>
public sealed record WorkspaceCaptureResult(
    WorkspaceSnapshot Snapshot,
    BrowserCaptureResult BrowserCapture);

/// <summary>Policy applied when optional browser capture did not complete.</summary>
public enum IncompleteBrowserCapturePolicy
{
    SavePartialWorkspace,
    RequireCompleteBrowserCapture,
}

/// <summary>Boundary used by capture orchestration and browser-session restore.</summary>
public interface IBrowserSessionConnector
{
    Task<BrowserCaptureResult> CaptureAsync(
        string workspaceName,
        IEnumerable<string> selectedBrowserTitles,
        CancellationToken cancellationToken = default);

    Task<bool> RestoreAsync(
        string workspaceName,
        List<BrowserSession> sessions,
        CancellationToken cancellationToken = default);
}
