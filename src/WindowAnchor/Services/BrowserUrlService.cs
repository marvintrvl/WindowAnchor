using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace WindowAnchor.Services;

/// <summary>
/// Reads the address-bar URL of a Chromium browser window via UI Automation.
///
/// <para><b>Why this exists.</b> Some trading tools are plain websites that cannot be installed
/// as a web app (no PWA manifest), so the user keeps them in a dedicated browser window next to
/// their normal multi-tab window. Both windows are indistinguishable to WindowAnchor: same
/// executable, same window class, same (browser-level) AppUserModelID, and both are covered by
/// <c>--restore-last-session</c>, which reopens the previous session rather than a specific
/// window. Capturing the URL lets such a window be reopened deliberately with
/// <c>--new-window &lt;url&gt;</c>.</para>
///
/// <para>Reading is opt-in via <see cref="Models.AppSettings.DedicatedBrowserUrlPatterns"/>: when
/// no patterns are configured nothing is queried, so users who do not need the feature pay no
/// cost during a snapshot.</para>
/// </summary>
public static class BrowserUrlService
{
    /// <summary>
    /// Hard limit for a single UI Automation query. The accessibility tree of a browser window
    /// can take a moment to materialise, and a snapshot must never hang on one window.
    /// </summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Returns the URL shown in the address bar of <paramref name="hWnd"/>, or an empty string
    /// when it cannot be determined (not a browser window, accessibility unavailable, timeout).
    /// Never throws.
    /// </summary>
    public static string GetWindowUrl(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "";

        try
        {
            // UI Automation can block for an unbounded time on an unresponsive window, so run it
            // on a worker and give up after QueryTimeout rather than stalling the whole snapshot.
            var task = Task.Run(() => ReadAddressBar(hWnd));
            return task.Wait(QueryTimeout) ? task.Result : "";
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[BrowserUrl] query failed for hwnd {hWnd}: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Finds the address bar inside a Chromium window and returns its value.
    /// Chromium exposes it as an Edit control supporting <c>ValuePattern</c>; the control's name
    /// is localised, so it is located by control type rather than by name.
    /// </summary>
    private static string ReadAddressBar(IntPtr hWnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hWnd);
            if (root == null) return "";

            var edits = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

            foreach (AutomationElement edit in edits)
            {
                try
                {
                    if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern))
                        continue;

                    string value = ((ValuePattern)pattern).Current.Value ?? "";
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    return NormalizeUrl(value);
                }
                catch { /* individual element vanished mid-query — try the next one */ }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[BrowserUrl] ReadAddressBar failed: {ex.Message}");
        }

        return "";
    }

    /// <summary>
    /// Turns what the address bar displays into a launchable URL. Chromium hides the scheme, so
    /// "vari.love/trade" is shown where the real URL is "https://vari.love/trade". Text that is
    /// clearly not an address (a search term the user is typing) is rejected.
    /// </summary>
    public static string NormalizeUrl(string raw)
    {
        string value = raw.Trim();
        if (value.Length == 0) return "";

        if (value.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return value;

        // Reject anything that cannot be a bare host: needs a dot and no whitespace.
        if (value.Contains(' ') || !value.Contains('.')) return "";

        return "https://" + value;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="url"/> matches any entry of
    /// <paramref name="patterns"/>. Matching is a case-insensitive substring test, so a bare
    /// domain such as <c>vari.love</c> matches every page on that site.
    /// </summary>
    public static bool MatchesAnyPattern(string url, IEnumerable<string>? patterns)
    {
        if (string.IsNullOrWhiteSpace(url) || patterns == null) return false;

        return patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Any(p => url.Contains(p.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
