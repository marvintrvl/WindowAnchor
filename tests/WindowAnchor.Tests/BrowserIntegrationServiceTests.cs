using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class BrowserIntegrationServiceTests
{
    [Fact]
    public void Chrome_store_setup_uses_the_published_connector_identity()
    {
        Assert.Equal("liiklnjpifhhmjncifbjjfgplonkkinh", BrowserIntegrationService.ChromeExtensionId);
        Assert.Contains(BrowserIntegrationService.ChromeExtensionId, BrowserIntegrationService.ChromeWebStoreUrl);
        Assert.StartsWith("https://chromewebstore.google.com/", BrowserIntegrationService.ChromeWebStoreUrl);
    }
}
