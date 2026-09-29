using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class IobitUninstallerAdapterTests
{
    [Theory]
    [InlineData("IObitUninstaler")]
    [InlineData("IObitUninstaller")]
    public void Adapter_claims_known_process_names_and_marks_geometry_as_non_fatal(string processName)
    {
        var adapter = new IobitUninstallerAdapter();
        var entry = new WorkspaceEntry { ProcessName = processName };
        SavedWindowIdentity identity = adapter.EnrichIdentity(
            entry,
            new SavedWindowIdentity { ProcessName = processName });

        Assert.True(adapter.CanHandle(entry));
        Assert.Equal(IobitUninstallerAdapter.AdapterName, identity.AppAdapterIdentity);

        var strategy = Assert.IsAssignableFrom<IWindowPlacementVerificationStrategy>(
            adapter.PlacementVerificationStrategy);
        Assert.True(strategy.CanHandle(identity));
        WindowPlacementVerificationPolicy policy = strategy.GetPolicy(null!);
        Assert.Equal(0, policy.MaxRetries);
        Assert.True(policy.TreatRejectionAsUnavailable);
    }
}
