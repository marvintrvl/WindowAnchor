using System;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

internal sealed class GenericWindowsAppAdapter : IAppAdapter
{
    private readonly CaptureResourceResolver _resourceResolver;

    internal GenericWindowsAppAdapter(CaptureResourceResolver resourceResolver) =>
        _resourceResolver = resourceResolver ?? throw new ArgumentNullException(nameof(resourceResolver));

    public string Name => "generic-win32";
    public IAppReadinessStrategy? ReadinessStrategy => null;
    public IWindowPlacementVerificationStrategy? PlacementVerificationStrategy => null;
    public bool CanHandle(WindowRecord window) => true;
    public bool CanHandle(WorkspaceEntry entry) => true;

    public WorkspaceEntry? TryCapture(AppAdapterCaptureContext context)
    {
        CapturedResource resource = _resourceResolver.Resolve(
            context.Window, context.SaveFiles, context.FolderSearchBudget, context.Progress,
            context.ResourceProgressCurrent, context.ResourceProgressTotal, context.BuildFullJumpListCache);
        WorkspaceEntry entry = AppAdapterEntryFactory.CreateBaseEntry(context.Window);
        entry.FilePath = resource.FilePath;
        entry.FileConfidence = resource.Confidence;
        entry.FileSource = resource.Source;
        entry.LaunchArg = resource.LaunchArgument;
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

internal sealed class GenericPlanningAppAdapter : IAppAdapter
{
    public string Name => "generic-win32";
    public IAppReadinessStrategy? ReadinessStrategy => null;
    public IWindowPlacementVerificationStrategy? PlacementVerificationStrategy => null;
    public bool CanHandle(WindowRecord window) => true;
    public bool CanHandle(WorkspaceEntry entry) => true;
    public WorkspaceEntry? TryCapture(AppAdapterCaptureContext context) => null;
    public SavedWindowIdentity EnrichIdentity(WorkspaceEntry entry, SavedWindowIdentity identity) => identity;

    public bool TryPlanLaunch(AppAdapterLaunchContext context, out RestoreLaunchDecision decision)
    {
        decision = null!;
        return false;
    }
}
