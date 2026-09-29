using System.Text.Json;
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

    [Fact]
    public void Firefox_setup_uses_the_published_add_on_identity_and_listing()
    {
        Assert.Equal(
            "windowanchor-browser-connector@windowanchor.app",
            BrowserIntegrationService.FirefoxExtensionId);
        Assert.Equal(
            "https://addons.mozilla.org/en-US/firefox/addon/windowanchor-browser-connector/",
            BrowserIntegrationService.FirefoxAddOnUrl);
    }

    [Fact]
    public void Firefox_native_host_manifest_uses_allowed_extensions()
    {
        using JsonDocument manifest = JsonDocument.Parse(
            BrowserIntegrationService.CreateNativeHostManifest(
                @"C:\Program Files\WindowAnchor\WindowAnchor.exe",
                BrowserConnectorKind.Firefox));

        JsonElement root = manifest.RootElement;
        Assert.Equal(BrowserIntegrationService.HostName, root.GetProperty("name").GetString());
        Assert.Equal(
            BrowserIntegrationService.FirefoxExtensionId,
            root.GetProperty("allowed_extensions")[0].GetString());
        Assert.False(root.TryGetProperty("allowed_origins", out _));
    }

    [Fact]
    public void Chromium_native_host_manifest_keeps_exact_store_origin()
    {
        using JsonDocument manifest = JsonDocument.Parse(
            BrowserIntegrationService.CreateNativeHostManifest(
                @"C:\Program Files\WindowAnchor\WindowAnchor.exe",
                BrowserConnectorKind.Chromium));

        JsonElement root = manifest.RootElement;
        Assert.Equal(
            $"chrome-extension://{BrowserIntegrationService.ChromeExtensionId}/",
            root.GetProperty("allowed_origins")[0].GetString());
        Assert.False(root.TryGetProperty("allowed_extensions", out _));
    }

    [Fact]
    public void Native_host_launch_arguments_route_each_browser_family()
    {
        Assert.Equal(
            BrowserSessionBridge.FirefoxPipeName,
            BrowserIntegrationService.ResolveNativeMessagingPipe([
                @"C:\Users\Example\native-host-manifest-firefox.json",
                BrowserIntegrationService.FirefoxExtensionId]));
        Assert.Equal(
            BrowserSessionBridge.ChromiumPipeName,
            BrowserIntegrationService.ResolveNativeMessagingPipe([
                $"chrome-extension://{BrowserIntegrationService.ChromeExtensionId}/"]));
        Assert.Null(BrowserIntegrationService.ResolveNativeMessagingPipe(["unrelated"]));
    }
}
