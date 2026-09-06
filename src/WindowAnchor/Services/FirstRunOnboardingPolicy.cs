namespace WindowAnchor.Services;

/// <summary>Pure eligibility rules for the one-time interactive onboarding surface.</summary>
internal static class FirstRunOnboardingPolicy
{
    internal static bool ShouldShow(
        bool onboardingCompleted,
        bool isMinimizedStartup,
        bool settingsSaveBlocked,
        bool hasSavedWorkspaces) =>
        !onboardingCompleted &&
        !isMinimizedStartup &&
        !settingsSaveBlocked &&
        !hasSavedWorkspaces;
}
