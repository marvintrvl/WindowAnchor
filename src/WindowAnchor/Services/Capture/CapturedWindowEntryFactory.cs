using System;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Creates the persisted entry shape for one already-enumerated window.</summary>
internal sealed class CapturedWindowEntryFactory
{
    private readonly AppAdapterRegistry _adapters;

    internal CapturedWindowEntryFactory(AppAdapterRegistry adapters)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
    }

    internal WorkspaceEntry Create(
        WindowRecord window,
        bool saveFiles,
        CaptureResourceSearchBudget folderSearchBudget,
        IProgress<SaveProgressReport>? progress,
        int resourceProgressCurrent,
        int resourceProgressTotal,
        bool buildFullJumpListCache)
    {
        return _adapters.Capture(new AppAdapterCaptureContext(
            window,
            saveFiles,
            folderSearchBudget,
            progress,
            resourceProgressCurrent,
            resourceProgressTotal,
            buildFullJumpListCache));
    }
}
