using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class FirstRunOnboardingPolicyTests
{
    [Fact]
    public void Fresh_interactive_install_shows_onboarding()
    {
        Assert.True(FirstRunOnboardingPolicy.ShouldShow(
            onboardingCompleted: false,
            isMinimizedStartup: false,
            settingsSaveBlocked: false,
            hasSavedWorkspaces: false));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void Established_or_noninteractive_startup_does_not_show_onboarding(
        bool completed,
        bool minimized,
        bool saveBlocked,
        bool hasWorkspaces)
    {
        Assert.False(FirstRunOnboardingPolicy.ShouldShow(
            completed,
            minimized,
            saveBlocked,
            hasWorkspaces));
    }
}
