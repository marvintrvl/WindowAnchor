using System;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// IObit Uninstaller normally runs elevated and rejects geometry writes from WindowAnchor.
/// Launching and virtual-desktop association remain independently verifiable, so its geometry
/// limitation is recorded as a non-fatal unavailable capability instead of a failed restore.
/// </summary>
internal sealed class IobitUninstallerAdapter : IAppAdapter, IWindowPlacementVerificationStrategy
{
    internal const string AdapterName = "iobit-uninstaller";

    public string Name => AdapterName;
    public IAppReadinessStrategy? ReadinessStrategy => null;
    public IWindowPlacementVerificationStrategy PlacementVerificationStrategy => this;

    public bool CanHandle(WindowRecord window) => IsIobitUninstaller(window.ProcessName);
    public bool CanHandle(WorkspaceEntry entry) => IsIobitUninstaller(entry.ProcessName);

    public WorkspaceEntry? TryCapture(AppAdapterCaptureContext context) => null;

    public SavedWindowIdentity EnrichIdentity(WorkspaceEntry entry, SavedWindowIdentity identity) =>
        identity with { AppAdapterIdentity = AdapterName };

    public bool TryPlanLaunch(AppAdapterLaunchContext context, out RestoreLaunchDecision decision)
    {
        decision = null!;
        return false;
    }

    bool IWindowPlacementVerificationStrategy.CanHandle(SavedWindowIdentity identity) =>
        IsIobitUninstaller(identity.ProcessName) ||
        identity.AppAdapterIdentity.Equals(AdapterName, StringComparison.OrdinalIgnoreCase);

    public WindowPlacementVerificationPolicy GetPolicy(RestorePlanEntry entry) => new()
    {
        InitialDelay = TimeSpan.Zero,
        RetryDelay = TimeSpan.Zero,
        MaxRetries = 0,
        BaseTolerancePixels = WindowPlacementVerificationPolicy.Default.BaseTolerancePixels,
        TreatRejectionAsUnavailable = true
    };

    private static bool IsIobitUninstaller(string processName) =>
        processName.Equals("IObitUninstaler", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("IObitUninstaller", StringComparison.OrdinalIgnoreCase);
}
