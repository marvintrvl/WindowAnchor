using System;
using System.Collections.Generic;
using System.Linq;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Deterministically selects compiled-in adapters and their optional strategy hooks.</summary>
internal sealed class AppAdapterRegistry
{
    private readonly IReadOnlyList<IAppAdapter> _adapters;
    private readonly IAppAdapter _fallback;

    internal AppAdapterRegistry(IEnumerable<IAppAdapter> adapters, IAppAdapter fallback)
    {
        _adapters = (adapters ?? throw new ArgumentNullException(nameof(adapters))).ToArray();
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    internal static AppAdapterRegistry CreateCaptureDefault(
        WebAppService webAppService,
        CaptureResourceResolver resourceResolver) => new(
        [new ChromiumWebAppAdapter(webAppService), new DedicatedBrowserWindowAdapter(), new ExplorerFolderAdapter()],
        new GenericWindowsAppAdapter(resourceResolver));

    internal static AppAdapterRegistry CreatePlanningDefault() => new(
        [new ChromiumWebAppAdapter(new WebAppService()), new DedicatedBrowserWindowAdapter(), new ExplorerFolderAdapter()],
        new GenericPlanningAppAdapter());

    internal WorkspaceEntry Capture(AppAdapterCaptureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (IAppAdapter adapter in _adapters.Where(adapter => adapter.CanHandle(context.Window)))
        {
            WorkspaceEntry? entry = adapter.TryCapture(context);
            if (entry != null)
                return entry;
        }
        return _fallback.TryCapture(context)
            ?? throw new InvalidOperationException("The generic Windows adapter must create an entry.");
    }

    internal SavedWindowIdentity EnrichIdentity(WorkspaceEntry entry, SavedWindowIdentity identity)
    {
        IAppAdapter adapter = _adapters.FirstOrDefault(candidate => candidate.CanHandle(entry)) ?? _fallback;
        return adapter.EnrichIdentity(entry, identity);
    }

    internal bool TryPlanLaunch(AppAdapterLaunchContext context, out RestoreLaunchDecision decision)
    {
        foreach (IAppAdapter adapter in _adapters.Where(adapter => adapter.CanHandle(context.Entry)))
        {
            if (adapter.TryPlanLaunch(context, out decision))
                return true;
        }
        return _fallback.TryPlanLaunch(context, out decision);
    }

    internal IReadOnlyList<IAppReadinessStrategy> ReadinessStrategies =>
        _adapters.Append(_fallback).Select(adapter => adapter.ReadinessStrategy)
            .Where(strategy => strategy != null).Cast<IAppReadinessStrategy>().ToArray();

    internal IReadOnlyList<IWindowPlacementVerificationStrategy> PlacementVerificationStrategies =>
        _adapters.Append(_fallback).Select(adapter => adapter.PlacementVerificationStrategy)
            .Where(strategy => strategy != null).Cast<IWindowPlacementVerificationStrategy>().ToArray();
}
