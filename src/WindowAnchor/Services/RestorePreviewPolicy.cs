using System;
using System.Linq;

namespace WindowAnchor.Services;

/// <summary>Pure policy for deciding whether an interactive restore must stop for review.</summary>
public static class RestorePreviewPolicy
{
    /// <summary>
    /// Honors the user's preview preference while retaining a mandatory review when the raw plan
    /// cannot execute without disabling or resolving one or more entries.
    /// </summary>
    public static bool ShouldShow(RestorePlan plan, bool previewEnabled)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return previewEnabled || !plan.CanExecute ||
            plan.Entries.Any(entry => entry.Outcome == RestorePlanEntryOutcome.Blocked);
    }
}
