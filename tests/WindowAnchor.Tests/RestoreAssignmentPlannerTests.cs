using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class RestoreAssignmentPlannerTests
{
    [Fact]
    public void Pwa_matches_by_aumid_instead_of_shared_browser_executable()
    {
        const string aumid = "Chrome._crx_abcdefghijklmnopabcdefghijklmnop";
        var entry = Entry(BrowserExe, "Saved PWA title", "Chrome_WidgetWin_1");
        entry.IsWebApp = true;
        entry.AppUserModelId = aumid;
        var live = LiveWindows(
            (11, Record(BrowserExe, "Normal Brave", "Chrome_WidgetWin_1", aumid: "Brave")),
            (12, Record(BrowserExe, "Changed page title", "Chrome_WidgetWin_1", aumid: aumid)));

        PlannedMatch match = Assert.Single(PlanMatches([entry], live));

        Assert.Equal(new IntPtr(12), match.Hwnd);
        Assert.True(match.TitleMatched);
    }

    [Fact]
    public void Ordinary_browser_entry_does_not_claim_a_pwa_window_from_an_old_snapshot()
    {
        var entry = Entry(BrowserExe, "Normal Brave", "Chrome_WidgetWin_1");
        var live = LiveWindows((11, Record(
            BrowserExe,
            "Installed app",
            "Chrome_WidgetWin_1",
            aumid: "Chrome._crx_abcdefghijklmnopabcdefghijklmnop")));

        Assert.Empty(PlanMatches([entry], live));
    }

    [Fact]
    public void Dedicated_browser_window_matches_the_same_host_after_navigation()
    {
        var entry = Entry(BrowserExe, "Saved chart title", "Chrome_WidgetWin_1");
        entry.IsDedicatedBrowserWindow = true;
        entry.BrowserUrl = "https://charts.example.test/workspace/one";
        var live = LiveWindows(
            (21, Record(BrowserExe, "Normal Brave", "Chrome_WidgetWin_1")),
            (22, Record(
                BrowserExe,
                "A different chart title",
                "Chrome_WidgetWin_1",
                browserUrl: "https://charts.example.test/workspace/two")));

        PlannedMatch match = Assert.Single(PlanMatches([entry], live));

        Assert.Equal(new IntPtr(22), match.Hwnd);
        Assert.True(match.TitleMatched);
    }

    [Fact]
    public void Dedicated_browser_window_does_not_match_a_different_host()
    {
        var entry = Entry(BrowserExe, "Saved chart title", "Chrome_WidgetWin_1");
        entry.IsDedicatedBrowserWindow = true;
        entry.BrowserUrl = "https://charts.example.test/workspace";
        var live = LiveWindows((21, Record(
            BrowserExe,
            "Other site",
            "Chrome_WidgetWin_1",
            browserUrl: "https://news.example.test/")));

        Assert.Empty(PlanMatches([entry], live));
    }

    [Fact]
    public void Document_matches_the_window_containing_its_filename()
    {
        const string wordExe = @"C:\Program Files\Microsoft Office\WINWORD.EXE";
        var entry = Entry(wordExe, "Thesis - Word", "OpusApp");
        entry.LaunchArg = @"C:\Users\fixture\Documents\Thesis.docx";
        var live = LiveWindows(
            (31, Record(wordExe, "Budget - Word", "OpusApp")),
            (32, Record(wordExe, "THESIS - Word", "OpusApp")));

        PlannedMatch match = Assert.Single(PlanMatches([entry], live));

        Assert.Equal(new IntPtr(32), match.Hwnd);
        Assert.True(match.TitleMatched);
    }

    [Fact]
    public void Session_wide_assignment_disambiguates_duplicate_process_windows_without_reuse()
    {
        const string exe = @"C:\Program Files\TradingView\TradingView.exe";
        var entries = new[]
        {
            Entry(exe, "BTCUSD 65000 / LTF Luis", "Chrome_WidgetWin_1"),
            Entry(exe, "ETHUSD 3500 / Outlook", "Chrome_WidgetWin_1")
        };
        var live = LiveWindows(
            (41, Record(exe, "ETHUSD 3512 / Outlook", "Chrome_WidgetWin_1")),
            (42, Record(exe, "BTCUSD 65120 / LTF Luis", "Chrome_WidgetWin_1")));

        IReadOnlyList<PlannedMatch> matches = PlanMatches(entries, live);

        Assert.Collection(
            matches,
            match =>
            {
                Assert.Equal(0, match.EntryIndex);
                Assert.Equal(new IntPtr(42), match.Hwnd);
                Assert.NotNull(match.TitleSimilarityScore);
            },
            match =>
            {
                Assert.Equal(1, match.EntryIndex);
                Assert.Equal(new IntPtr(41), match.Hwnd);
                Assert.Null(match.TitleSimilarityScore);
            });
    }

    [Theory]
    [InlineData("Same Title", "same title", 1.0)]
    [InlineData("x", "x", 1.0)]
    [InlineData("x", "y", 0.0)]
    [InlineData("", "", 1.0)]
    public void Title_similarity_characterizes_case_and_short_titles(
        string saved,
        string live,
        double expected) =>
        Assert.Equal(expected, WindowMatcher.TitleSimilarity(saved, live));

    [Fact]
    public void Already_restored_entry_is_not_planned_again()
    {
        const string exe = @"C:\Apps\editor.exe";
        var live = LiveWindows((51, Record(exe, "notes", "EditorWindow")));

        Assert.Empty(PlanMatches(
            [Entry(exe, "notes", "EditorWindow")],
            live,
            new HashSet<int> { 0 }));
    }

    private static IReadOnlyList<PlannedMatch> PlanMatches(
        IReadOnlyList<WorkspaceEntry> entries,
        IReadOnlyDictionary<IntPtr, (uint Pid, WindowRecord Record)> liveWindows,
        IReadOnlySet<int>? restoredEntries = null)
    {
        LiveWindowIdentity[] identities = liveWindows
            .Select(window => WindowIdentityExtractor.FromLive(
                window.Key,
                window.Value.Pid,
                window.Value.Record))
            .ToArray();
        var planner = new RestoreAssignmentPlanner("test-workspace", identities, []);
        var matches = new List<PlannedMatch>();
        for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
        {
            if (restoredEntries?.Contains(entryIndex) == true)
                continue;
            RestoreAssignmentResult assignment = planner.Resolve(
                entries[entryIndex],
                WindowIdentityExtractor.FromSaved(entries[entryIndex]));
            if (assignment.SelectedMatch is not { } selected)
                continue;
            bool titleMatched = selected.Evidence.Any(evidence =>
                evidence.Matched && evidence.Kind is
                    WindowMatchEvidenceKind.PwaIdentityExact or
                    WindowMatchEvidenceKind.DedicatedBrowserSiteExact or
                    WindowMatchEvidenceKind.DocumentNameInTitle);
            matches.Add(new PlannedMatch(
                entryIndex,
                selected.Hwnd,
                titleMatched,
                selected.TitleSimilarityScore));
        }
        return matches;
    }

    private static WorkspaceEntry Entry(string exe, string title, string className) => new()
    {
        ExecutablePath = exe,
        ProcessName = Path.GetFileNameWithoutExtension(exe),
        WindowClassName = className,
        Position = Record(exe, title, className)
    };

    private static WindowRecord Record(
        string exe,
        string title,
        string className,
        string aumid = "",
        string browserUrl = "") => new()
    {
        ExecutablePath = exe,
        ProcessName = Path.GetFileNameWithoutExtension(exe),
        ClassName = className,
        TitleSnippet = title,
        AppUserModelId = aumid,
        BrowserUrl = browserUrl
    };

    private static Dictionary<IntPtr, (uint Pid, WindowRecord Record)> LiveWindows(
        params (int Hwnd, WindowRecord Record)[] windows) =>
        windows.ToDictionary(window => new IntPtr(window.Hwnd),
            window => ((uint)window.Hwnd, window.Record));

    private sealed record PlannedMatch(
        int EntryIndex,
        IntPtr Hwnd,
        bool TitleMatched,
        double? TitleSimilarityScore);

    private const string BrowserExe =
        @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe";
}
