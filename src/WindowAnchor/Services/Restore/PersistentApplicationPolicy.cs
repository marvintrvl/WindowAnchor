using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>Pure identity matching for the global Exact Switch preservation preference.</summary>
internal static class PersistentApplicationPolicy
{
    internal static IReadOnlySet<long> GetProtectedWindowHandles(
        IEnumerable<LiveWindowIdentity> windows,
        IEnumerable<PersistentApplicationIdentity>? applications,
        RestoreModeKind mode)
    {
        if (mode != RestoreModeKind.ExactSwitch || applications is null)
            return new HashSet<long>();

        PersistentApplicationIdentity[] identities = applications
            .Where(IsValid)
            .Select(Normalize)
            .Distinct()
            .ToArray();
        if (identities.Length == 0)
            return new HashSet<long>();

        return windows
            .Where(window => identities.Any(identity => Matches(identity, window)))
            .Select(window => window.Hwnd.ToInt64())
            .ToHashSet();
    }

    internal static PersistentApplicationIdentity FromLive(LiveWindowIdentity window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return Normalize(new PersistentApplicationIdentity
        {
            AppUserModelId = window.AppUserModelId,
            ExecutableName = Path.GetFileName(window.ExecutablePath)
        });
    }

    internal static bool IsValid(PersistentApplicationIdentity? identity) => identity is not null &&
        (!string.IsNullOrWhiteSpace(identity.AppUserModelId) ||
         !string.IsNullOrWhiteSpace(identity.ExecutableName));

    internal static PersistentApplicationIdentity Normalize(PersistentApplicationIdentity identity) => new()
    {
        AppUserModelId = (identity.AppUserModelId ?? "").Trim().ToLowerInvariant(),
        ExecutableName = Path.GetFileName((identity.ExecutableName ?? "").Trim()).ToLowerInvariant()
    };

    private static bool Matches(PersistentApplicationIdentity identity, LiveWindowIdentity window)
    {
        if (!string.IsNullOrWhiteSpace(identity.AppUserModelId) &&
            string.Equals(identity.AppUserModelId, window.AppUserModelId, StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(identity.ExecutableName) &&
            string.Equals(identity.ExecutableName, Path.GetFileName(window.ExecutablePath),
                StringComparison.OrdinalIgnoreCase);
    }
}
