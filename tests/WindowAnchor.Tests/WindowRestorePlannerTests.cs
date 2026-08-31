using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class WindowRestorePlannerTests
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

        var match = Assert.Single(WindowRestorePlanner.PlanMatches([entry], live, new HashSet<int>()));

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

        Assert.Empty(WindowRestorePlanner.PlanMatches([entry], live, new HashSet<int>()));
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

        var match = Assert.Single(WindowRestorePlanner.PlanMatches([entry], live, new HashSet<int>()));

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

        Assert.Empty(WindowRestorePlanner.PlanMatches([entry], live, new HashSet<int>()));
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

        var match = Assert.Single(WindowRestorePlanner.PlanMatches([entry], live, new HashSet<int>()));

        Assert.Equal(new IntPtr(32), match.Hwnd);
        Assert.True(match.TitleMatched);
    }

    [Fact]
    public void Duplicate_same_process_windows_are_disambiguated_by_title_similarity()
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

        var matches = WindowRestorePlanner.PlanMatches(entries, live, new HashSet<int>());

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
                // Once the first HWND is consumed, the sole remaining candidate uses
                // the existing exe + class fallback and does not need another score.
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
        double expected)
    {
        Assert.Equal(expected, WindowRestorePlanner.TitleSimilarity(saved, live));
    }

    [Fact]
    public void Already_restored_entry_is_not_planned_again()
    {
        const string exe = @"C:\Apps\editor.exe";
        var live = LiveWindows((51, Record(exe, "notes", "EditorWindow")));

        Assert.Empty(WindowRestorePlanner.PlanMatches(
            [Entry(exe, "notes", "EditorWindow")],
            live,
            new HashSet<int> { 0 }));
    }

    private static WorkspaceEntry Entry(string exe, string title, string className) => new()
    {
        ExecutablePath = exe,
        ProcessName = System.IO.Path.GetFileNameWithoutExtension(exe),
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
        ProcessName = System.IO.Path.GetFileNameWithoutExtension(exe),
        ClassName = className,
        TitleSnippet = title,
        AppUserModelId = aumid,
        BrowserUrl = browserUrl
    };

    private static Dictionary<IntPtr, (uint Pid, WindowRecord Record)> LiveWindows(
        params (int Hwnd, WindowRecord Record)[] windows) =>
        windows.ToDictionary(w => new IntPtr(w.Hwnd), w => ((uint)w.Hwnd, w.Record));

    private const string BrowserExe = @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe";
}
