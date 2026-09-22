using System;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

internal sealed class ExplorerFolderAdapter : IAppAdapter
{
    public string Name => "explorer";
    public IAppReadinessStrategy? ReadinessStrategy => null;
    public IWindowPlacementVerificationStrategy? PlacementVerificationStrategy => null;

    public bool CanHandle(WindowRecord window) =>
        window.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrEmpty(window.FolderPath);

    public bool CanHandle(WorkspaceEntry entry) =>
        entry.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrEmpty(entry.Position.FolderPath);

    public WorkspaceEntry? TryCapture(AppAdapterCaptureContext context)
    {
        WorkspaceEntry entry = AppAdapterEntryFactory.CreateBaseEntry(context.Window);
        entry.FilePath = context.SaveFiles ? context.Window.FolderPath : null;
        entry.FileConfidence = context.SaveFiles ? 95 : 0;
        entry.FileSource = context.SaveFiles ? "EXPLORER_FOLDER" : "NONE";
        entry.LaunchArg = context.SaveFiles ? context.Window.FolderPath : null;
        return entry;
    }

    public SavedWindowIdentity EnrichIdentity(WorkspaceEntry entry, SavedWindowIdentity identity) =>
        identity with { AppAdapterIdentity = Name };

    public bool TryPlanLaunch(AppAdapterLaunchContext context, out RestoreLaunchDecision decision)
    {
        decision = null!;
        return false;
    }
}
