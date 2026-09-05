using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class WindowIdentityTests
{
    [Fact]
    public void Saved_identity_centralizes_stable_evidence_without_runtime_handles_or_pids()
    {
        var entry = Entry(
            @"C:\Program Files\Microsoft VS Code\Code.exe",
            "Project Zephyr - Visual Studio Code",
            "Chrome_WidgetWin_1");
        entry.EntryId = "11111111-1111-4111-8111-111111111111";
        entry.AppUserModelId = "Contoso.Editor_abc!Main";
        entry.FilePath = @"C:\Users\alice\Projects\Zephyr.code-workspace";
        entry.LaunchArg = entry.FilePath;
        entry.WebAppLaunchArguments = "--profile-directory=Work --token raw-secret";
        entry.MonitorId = "monitor-primary";
        entry.MonitorIndex = 0;

        SavedWindowIdentity identity = WindowIdentityExtractor.FromSaved(entry);

        Assert.Equal(entry.EntryId, identity.EntryId);
        Assert.Equal("contoso.editor_abc", identity.PackageFamilyName);
        Assert.Equal(@"c:\users\alice\projects\zephyr.code-workspace", identity.DocumentPath);
        Assert.Equal(identity.DocumentPath, identity.ProjectOrWorkspacePath);
        Assert.Equal("work", identity.BrowserProfile);
        Assert.Contains("project", identity.NormalizedTitleTokens);
        Assert.Contains("zephyr", identity.NormalizedTitleTokens);
        Assert.Contains("<redacted>", identity.SafeLaunchArguments);
        Assert.DoesNotContain("raw-secret", identity.SafeLaunchArguments);
        Assert.DoesNotContain(
            typeof(SavedWindowIdentity).GetProperties(),
            property => property.Name is "Hwnd" or "ProcessId" or "Pid");
    }

    [Fact]
    public void Same_executable_entries_remain_distinct_and_rank_by_explained_title_evidence()
    {
        const string exe = @"C:\Apps\editor.exe";
        var first = Entry(exe, "Alpha quarterly plan", "EditorWindow");
        var second = Entry(exe, "Beta research notes", "EditorWindow");
        first.EntryId = "11111111-1111-4111-8111-111111111111";
        second.EntryId = "22222222-2222-4222-8222-222222222222";
        SavedWindowIdentity firstIdentity = WindowIdentityExtractor.FromSaved(first);
        SavedWindowIdentity secondIdentity = WindowIdentityExtractor.FromSaved(second);
        LiveWindowIdentity[] live =
        [
            Live(42, exe, "Beta research notes updated", "EditorWindow"),
            Live(41, exe, "Alpha quarterly plan updated", "EditorWindow")
        ];

        WindowMatchCandidate firstBest = WindowMatcher.FindCandidates(firstIdentity, live)[0];
        WindowMatchCandidate secondBest = WindowMatcher.FindCandidates(secondIdentity, live)[0];

        Assert.NotEqual(firstIdentity.EntryId, secondIdentity.EntryId);
        Assert.Equal(new IntPtr(41), firstBest.Hwnd);
        Assert.Equal(new IntPtr(42), secondBest.Hwnd);
        Assert.True(firstBest.Score > 0);
        Assert.Contains(firstBest.Evidence,
            evidence => evidence.Kind == WindowMatchEvidenceKind.TitleSimilarity && evidence.Matched);
    }

    [Fact]
    public void Pwa_matching_uses_exact_aumid_and_rejects_the_ordinary_browser()
    {
        const string aumid = "Chrome._crx_abcdefghijklmnopabcdefghijklmnop";
        var entry = Entry(BrowserExe, "Saved PWA", "Chrome_WidgetWin_1");
        entry.IsWebApp = true;
        entry.AppUserModelId = aumid;
        SavedWindowIdentity saved = WindowIdentityExtractor.FromSaved(entry);
        LiveWindowIdentity[] live =
        [
            Live(11, BrowserExe, "Normal browser", "Chrome_WidgetWin_1", aumid: "Brave"),
            Live(12, @"C:\Updated\chrome_proxy.exe", "Changed page", "Chrome_WidgetWin_1", aumid: aumid)
        ];

        IReadOnlyList<WindowMatchCandidate> candidates = WindowMatcher.FindCandidates(saved, live);

        Assert.Equal(new IntPtr(12), candidates[0].Hwnd);
        Assert.Equal(WindowMatchConfidence.Exact, candidates[0].Confidence);
        Assert.Contains(candidates[0].Evidence,
            evidence => evidence.Kind == WindowMatchEvidenceKind.PwaIdentityExact);
        Assert.False(candidates.Single(candidate => candidate.Hwnd == new IntPtr(11)).IsEligible);
    }

    [Fact]
    public void Dedicated_browser_matching_explains_same_site_and_rejects_other_sites()
    {
        var entry = Entry(BrowserExe, "Saved chart", "Chrome_WidgetWin_1");
        entry.IsDedicatedBrowserWindow = true;
        entry.BrowserUrl = "https://charts.example.test/workspace/one";
        SavedWindowIdentity saved = WindowIdentityExtractor.FromSaved(entry);
        LiveWindowIdentity[] live =
        [
            Live(21, BrowserExe, "Chart", "Chrome_WidgetWin_1",
                url: "https://charts.example.test/workspace/two"),
            Live(22, BrowserExe, "News", "Chrome_WidgetWin_1",
                url: "https://news.example.test/")
        ];

        IReadOnlyList<WindowMatchCandidate> candidates = WindowMatcher.FindCandidates(saved, live);

        Assert.Equal(new IntPtr(21), candidates[0].Hwnd);
        Assert.Contains(candidates[0].Evidence,
            evidence => evidence.Kind == WindowMatchEvidenceKind.DedicatedBrowserSiteExact);
        Assert.Contains(
            candidates.Single(candidate => candidate.Hwnd == new IntPtr(22)).Evidence,
            evidence => evidence.Kind == WindowMatchEvidenceKind.DedicatedBrowserSiteMismatch);
    }

    [Fact]
    public void Document_name_is_strong_evidence_and_beats_other_same_process_windows()
    {
        const string exe = @"C:\Program Files\Microsoft Office\WINWORD.EXE";
        var entry = Entry(exe, "Thesis - Word", "OpusApp");
        entry.FilePath = @"C:\Users\fixture\Documents\Thesis.docx";
        entry.LaunchArg = entry.FilePath;
        LiveWindowIdentity[] live =
        [
            Live(31, exe, "Budget - Word", "OpusApp"),
            Live(32, exe, "THESIS - Word", "OpusApp")
        ];

        WindowMatchCandidate best = WindowMatcher.FindCandidates(
            WindowIdentityExtractor.FromSaved(entry),
            live)[0];

        Assert.Equal(new IntPtr(32), best.Hwnd);
        Assert.Equal(WindowMatchConfidence.Exact, best.Confidence);
        Assert.Contains(best.Evidence,
            evidence => evidence.Kind == WindowMatchEvidenceKind.DocumentNameInTitle);
    }

    [Fact]
    public void Packaged_app_aumid_survives_an_executable_path_update()
    {
        const string aumid = "Contoso.Notes_123abc!App";
        var entry = Entry(@"C:\Program Files\WindowsApps\Contoso.Notes_1\Notes.exe", "Notes", "AppWindow");
        entry.AppUserModelId = aumid;
        LiveWindowIdentity updated = Live(
            35,
            @"C:\Program Files\WindowsApps\Contoso.Notes_2\Notes.exe",
            "Notes",
            "NewAppWindow",
            aumid: aumid);

        WindowMatchCandidate candidate = Assert.Single(WindowMatcher.FindCandidates(
            WindowIdentityExtractor.FromSaved(entry),
            [updated]));

        Assert.True(candidate.IsEligible);
        Assert.Equal(WindowMatchConfidence.Exact, candidate.Confidence);
        Assert.Contains(candidate.Evidence,
            evidence => evidence.Kind == WindowMatchEvidenceKind.AppUserModelIdExact &&
                        evidence.ScoreContribution > 0);
    }

    [Fact]
    public void Shared_packaged_identity_can_match_a_hosted_window_across_process_boundaries()
    {
        const string aumid = "Contoso.Suite_123abc!Dashboard";
        var entry = Entry(
            @"C:\Program Files\WindowsApps\Contoso.Suite_1\Host.exe",
            "Dashboard",
            "HostWindow");
        entry.AppUserModelId = aumid;
        LiveWindowIdentity hosted = Live(
            36,
            @"C:\Program Files\WindowsApps\Contoso.Runtime_1\Renderer.exe",
            "Dashboard",
            "HostedSurface",
            aumid: aumid);

        WindowMatchCandidate candidate = Assert.Single(WindowMatcher.FindCandidates(
            WindowIdentityExtractor.FromSaved(entry),
            [hosted]));

        Assert.True(candidate.IsEligible);
        Assert.Equal(WindowMatchConfidence.Exact, candidate.Confidence);
        Assert.Contains(candidate.Evidence,
            evidence => evidence.Kind == WindowMatchEvidenceKind.AppUserModelIdExact);
    }

    [Fact]
    public void Equal_scores_are_ambiguous_and_never_auto_assigned()
    {
        const string exe = @"C:\Apps\editor.exe";
        var entry = Entry(exe, "same title", "EditorWindow");
        var live = LiveWindows(
            (402, Record(exe, "same title", "EditorWindow")),
            (401, Record(exe, "same title", "EditorWindow")));
        IReadOnlyList<WindowMatchCandidate> candidates = WindowMatcher.FindCandidates(
            WindowIdentityExtractor.FromSaved(entry),
            live.Select(window => WindowIdentityExtractor.FromLive(
                window.Key,
                window.Value.Pid,
                window.Value.Record)));

        Assert.Equal(2, candidates.Count(candidate => candidate.IsTopScoreTie));
        Assert.Equal(new IntPtr(401), candidates[0].Hwnd);
        WindowMatchResolution resolution = WindowMatcher.ResolveCandidates(candidates);
        Assert.Equal(WindowMatchConfidence.Ambiguous, resolution.Confidence);
        Assert.Null(resolution.SelectedCandidate);
        Assert.Empty(WindowRestorePlanner.PlanMatches([entry], live, new HashSet<int>()));
    }

    [Fact]
    public void Close_title_scores_use_margin_and_low_similarity_is_ineligible()
    {
        const string exe = @"C:\Apps\editor.exe";
        var entry = Entry(exe, "Alpha report", "EditorWindow");
        SavedWindowIdentity saved = WindowIdentityExtractor.FromSaved(entry);
        LiveWindowIdentity north = Live(61, exe, "Alpha report north", "EditorWindow");
        LiveWindowIdentity south = Live(62, exe, "Alpha report south", "EditorWindow");
        LiveWindowIdentity unrelated = Live(63, exe, "Completely unrelated dashboard", "EditorWindow");

        WindowMatchResolution resolution = WindowMatcher.Resolve(saved, [north, south, unrelated]);

        Assert.Equal(WindowMatchConfidence.Ambiguous, resolution.Confidence);
        Assert.Null(resolution.SelectedCandidate);
        Assert.Equal([61L, 62L], resolution.Candidates
            .Where(candidate => candidate.IsWithinAmbiguityMargin)
            .Select(candidate => candidate.Hwnd.ToInt64()));
        WindowMatchCandidate rejected = resolution.Candidates.Single(candidate =>
            candidate.Hwnd == new IntPtr(63));
        Assert.False(rejected.IsEligible);
        Assert.Equal(WindowMatchConfidence.Ineligible, rejected.Confidence);
    }

    [Fact]
    public void Learned_composite_hint_breaks_future_tie_with_explained_evidence()
    {
        const string exe = @"C:\Apps\editor.exe";
        var entry = Entry(exe, "Alpha report", "EditorWindow");
        SavedWindowIdentity saved = WindowIdentityExtractor.FromSaved(entry);
        LiveWindowIdentity north = Live(71, exe, "Alpha report north", "EditorWindow");
        LiveWindowIdentity south = Live(72, exe, "Alpha report south", "EditorWindow");
        WindowIdentityHint learned = WindowIdentityExtractor.ToHint(north);

        WindowMatchResolution resolution = WindowMatcher.Resolve(saved, [south, north], learned);

        WindowMatchCandidate selected = Assert.IsType<WindowMatchCandidate>(resolution.SelectedCandidate);
        Assert.Equal(new IntPtr(71), selected.Hwnd);
        Assert.Equal(WindowMatchConfidence.Strong, resolution.Confidence);
        Assert.True(selected.IsLearnedHintMatch);
        Assert.Contains(selected.Evidence, evidence =>
            evidence.Kind == WindowMatchEvidenceKind.LearnedIdentityHint && evidence.Matched);
    }

    [Fact]
    public void Geometry_is_available_as_a_final_context_fallback()
    {
        const string exe = @"C:\Apps\editor.exe";
        var entry = Entry(exe, "old title", "OldClass");
        entry.Position.NormalLeft = 100;
        entry.Position.NormalTop = 100;
        entry.Position.NormalRight = 1100;
        entry.Position.NormalBottom = 800;
        var live = Live(
            51,
            exe,
            "completely changed",
            "NewClass",
            bounds: new WindowIdentityBounds(120, 110, 1120, 810));

        WindowMatchCandidate candidate = Assert.Single(WindowMatcher.FindCandidates(
            WindowIdentityExtractor.FromSaved(entry),
            [live]));

        Assert.True(candidate.IsEligible);
        Assert.Equal(WindowMatchConfidence.Probable, candidate.Confidence);
        Assert.Contains(candidate.Evidence,
            evidence => evidence.Kind == WindowMatchEvidenceKind.GeometrySimilarity);
    }

    [Fact]
    public void Legacy_workspace_migration_produces_identity_models_without_new_persisted_runtime_ids()
    {
        using var directory = new TestDirectory();
        directory.CopyFixture("legacy-v2.workspace.json", @"workspaces\legacy.workspace.json");
        WorkspaceSnapshot migrated = Assert.Single(new StorageService(directory.Path).LoadAllWorkspaces());

        SavedWindowIdentity[] identities = migrated.Entries
            .Select(WindowIdentityExtractor.FromSaved)
            .ToArray();

        Assert.NotEmpty(identities);
        Assert.All(identities, identity => Assert.True(Guid.TryParse(identity.EntryId, out _)));
        Assert.All(identities, identity => Assert.NotEmpty(identity.ExecutablePath));
    }

    private static WorkspaceEntry Entry(string exe, string title, string className) => new()
    {
        ExecutablePath = exe,
        ProcessName = Path.GetFileNameWithoutExtension(exe),
        WindowClassName = className,
        Position = Record(exe, title, className)
    };

    private static LiveWindowIdentity Live(
        int hwnd,
        string exe,
        string title,
        string className,
        string aumid = "",
        string url = "",
        WindowIdentityBounds? bounds = null)
    {
        WindowRecord record = Record(exe, title, className, aumid, url);
        if (bounds is { } value)
        {
            record.NormalLeft = value.Left;
            record.NormalTop = value.Top;
            record.NormalRight = value.Right;
            record.NormalBottom = value.Bottom;
        }
        return WindowIdentityExtractor.FromLive(new IntPtr(hwnd), (uint)hwnd, record);
    }

    private static WindowRecord Record(
        string exe,
        string title,
        string className,
        string aumid = "",
        string url = "") => new()
    {
        ExecutablePath = exe,
        ProcessName = Path.GetFileNameWithoutExtension(exe),
        ClassName = className,
        TitleSnippet = title,
        AppUserModelId = aumid,
        BrowserUrl = url
    };

    private static Dictionary<IntPtr, (uint Pid, WindowRecord Record)> LiveWindows(
        params (int Hwnd, WindowRecord Record)[] windows) =>
        windows.ToDictionary(
            window => new IntPtr(window.Hwnd),
            window => ((uint)window.Hwnd, window.Record));

    private const string BrowserExe =
        @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe";
}
