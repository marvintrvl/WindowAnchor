using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class ProcessIdentityNormalizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("Code", "code")]
    [InlineData("  Code.EXE  ", "code")]
    [InlineData("explorer.exe.exe", "explorer.exe")]
    [InlineData("service.executable", "service.executable")]
    public void Normalize_produces_one_stable_comparison_key(string? input, string expected)
    {
        Assert.Equal(expected, ProcessIdentityNormalizer.Normalize(input));
    }
}
