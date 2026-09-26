using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Automation;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Discovers Terminal's visible tabs, not their profiles or directories. Terminal currently has
/// no public live-session query for those values, so the save dialog asks the user to supply them.
/// </summary>
internal static class TerminalTabCaptureService
{
    private static readonly Regex Marker = new(@"\[WA:([0-9a-fA-F]{12})\]", RegexOptions.Compiled);

    internal static (List<TerminalTab> Tabs, int ActiveIndex) Capture(IntPtr hwnd)
    {
        try
        {
            Task<(List<TerminalTab>, int)> task = Task.Run(() => Read(hwnd));
            if (task.Wait(TimeSpan.FromMilliseconds(1500)))
                return task.Result;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("terminal.tabs_query_failed", "Could not inspect Terminal tabs", ex);
        }
        return (new List<TerminalTab>(), 0);
    }

    private static (List<TerminalTab>, int) Read(IntPtr hwnd)
    {
        var tabs = new List<TerminalTab>();
        int active = 0;
        AutomationElement root = AutomationElement.FromHandle(hwnd);
        AutomationElementCollection items = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
        foreach (AutomationElement item in items.Cast<AutomationElement>())
        {
            try
            {
                if (!item.Current.IsOffscreen &&
                    item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object pattern) &&
                    ((SelectionItemPattern)pattern).Current.IsSelected)
                    active = tabs.Count;
                string title = item.Current.Name ?? "";
                Match marker = Marker.Match(title);
                TerminalTab tab = marker.Success
                    ? TerminalSessionTracker.Read(marker.Groups[1].Value) ?? new TerminalTab()
                    : new TerminalTab();
                tab.TitleHint = title;
                tabs.Add(tab);
            }
            catch (ElementNotAvailableException) { }
        }
        return (tabs, active);
    }
}
