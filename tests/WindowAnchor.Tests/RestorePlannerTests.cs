using System.Text.Json;
using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class RestorePlannerTests
{
    [Fact]
    public void Planning_exact_match_is_side_effect_free_and_explained()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Quarterly notes", "primary");
        var snapshot = Snapshot(entry);
        string snapshotBefore = JsonSerializer.Serialize(snapshot);
        LiveWindowIdentity live = Live(
            41,
            @"C:\Apps\editor.exe",
            "Quarterly notes",
            "EditorWindow",
            monitorId: "primary");
        var windows = new[] { live };

        RestorePlan plan = RestorePlanner.Build(
            snapshot,
            new RestoreLiveInventory { Windows = windows },
            Topology(Monitor("primary", 0, 96, primary: true)),
            RestoreMode.Standard);

        RestorePlanEntry result = Assert.Single(plan.Entries);
        Assert.Equal(RestorePlanEntryOutcome.Matched, result.Outcome);
        Assert.Equal(41, Assert.IsType<RestorePlanCandidate>(result.SelectedMatch).WindowHandle);
        Assert.Equal(RestoreActionKind.RestoreExistingWindow, Assert.Single(result.Actions).Kind);
        Assert.NotEmpty(result.SelectedMatch.Evidence);
        Assert.Equal(snapshotBefore, JsonSerializer.Serialize(snapshot));
        Assert.False(entry.WasRestored);
        Assert.Same(live, Assert.Single(windows));
    }

    [Fact]
    public void Ambiguous_duplicate_windows_have_no_assignment_or_actions_and_stable_json()
    {
        WorkspaceEntry first = Entry(@"C:\Apps\editor.exe", "Untitled", "primary");
        WorkspaceEntry second = Entry(@"C:\Apps\editor.exe", "Untitled", "primary");
        var snapshot = Snapshot(first, second);
        LiveWindowIdentity lower = Live(10, first.ExecutablePath, "Untitled", "EditorWindow");
        LiveWindowIdentity higher = Live(20, first.ExecutablePath, "Untitled", "EditorWindow");
        var topology = Topology(Monitor("primary", 0, 96, primary: true));

        RestorePlan forward = RestorePlanner.Build(
            snapshot,
            new RestoreLiveInventory { Windows = [higher, lower] },
            topology,
            RestoreMode.Standard);
        RestorePlan reverse = RestorePlanner.Build(
            snapshot,
            new RestoreLiveInventory { Windows = [lower, higher] },
            topology,
            RestoreMode.Standard);

        Assert.All(forward.Entries, entry =>
        {
            Assert.Null(entry.SelectedMatch);
            Assert.Equal(RestorePlanEntryOutcome.Blocked, entry.Outcome);
            Assert.Empty(entry.Actions);
            Assert.Contains(entry.Warnings,
                warning => warning.Code == RestorePlanIssueCode.AmbiguousMatch);
            Assert.Equal([10L, 20L], entry.Candidates
                .Where(candidate => candidate.IsWithinAmbiguityMargin)
                .Select(candidate => candidate.WindowHandle));
        });
        Assert.Empty(forward.Actions);
        Assert.Equal([10L, 20L], forward.ProtectedWindowHandles.Order());
        Assert.Equal(forward.ToRedactedJson(), reverse.ToRedactedJson());
    }

    [Fact]
    public void User_selection_derives_assignment_without_mutating_preview_or_reusing_hwnd()
    {
        WorkspaceEntry first = Entry(@"C:\Apps\editor.exe", "Alpha report", "primary");
        WorkspaceEntry second = Entry(@"C:\Apps\editor.exe", "Alpha report", "primary");
        var snapshot = Snapshot(first, second);
        LiveWindowIdentity north = Live(10, first.ExecutablePath, "Alpha report north", "EditorWindow");
        LiveWindowIdentity south = Live(20, first.ExecutablePath, "Alpha report south", "EditorWindow");
        RestorePlan preview = Build(snapshot, [south, north]);

        RestorePlan resolved = RestorePlanner.ResolveAmbiguousMatch(preview, 0, 10);

        Assert.Null(preview.Entries[0].SelectedMatch);
        RestorePlanEntry selectedEntry = resolved.Entries[0];
        Assert.Equal(10, selectedEntry.SelectedMatch?.WindowHandle);
        Assert.True(selectedEntry.SelectedMatch?.IsUserSelected);
        Assert.Equal(RestorePlanEntryOutcome.Matched, selectedEntry.Outcome);
        Assert.Equal(RestoreActionKind.RestoreExistingWindow, Assert.Single(selectedEntry.Actions).Kind);
        Assert.DoesNotContain(selectedEntry.Warnings,
            warning => warning.Code == RestorePlanIssueCode.AmbiguousMatch);
        RestorePlan changedSelection = RestorePlanner.ResolveAmbiguousMatch(resolved, 0, 20);
        Assert.Equal(20, changedSelection.Entries[0].SelectedMatch?.WindowHandle);
        Assert.True(changedSelection.Entries[0].SelectedMatch?.IsUserSelected);
        Assert.Equal(
            20,
            Assert.Single(changedSelection.Entries[0].Actions,
                action => action.Kind == RestoreActionKind.RestoreExistingWindow).WindowHandle);
        Assert.DoesNotContain(
            changedSelection.Actions,
            action => action.EntryIndex == 0 && action.WindowHandle == 10);
        Assert.Throws<InvalidOperationException>(() =>
            RestorePlanner.ResolveAmbiguousMatch(resolved, 1, 10));
    }

    [Fact]
    public void Running_process_without_an_eligible_task_window_is_excluded_without_wait_actions()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\background-helper.exe", "Helper", "primary");

        RestorePlan plan = Build(
            Snapshot(entry),
            runningApplications:
            [
                new RunningApplicationIdentity(entry.ExecutablePath, entry.ProcessName)
            ]);

        RestorePlanEntry excluded = Assert.Single(plan.Entries);
        Assert.Equal(RestorePlanEntryOutcome.Excluded, excluded.Outcome);
        Assert.Empty(excluded.Actions);
        Assert.Contains(excluded.Warnings,
            warning => warning.Code == RestorePlanIssueCode.RunningApplicationHasNoRestorableWindow);
    }

    [Fact]
    public void Same_process_name_at_a_different_known_path_does_not_suppress_launch()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\One\editor.exe", "Editor", "primary");

        RestorePlan plan = Build(
            Snapshot(entry),
            runningApplications:
            [
                new RunningApplicationIdentity(@"D:\Apps\Two\editor.exe", "editor")
            ]);

        RestorePlanEntry planned = Assert.Single(plan.Entries);
        Assert.Equal(RestorePlanEntryOutcome.LaunchRequired, planned.Outcome);
        Assert.Contains(planned.Actions,
            action => action.Kind == RestoreActionKind.LaunchApplication);
    }

    [Fact]
    public void Stable_packaged_identity_detects_a_running_updated_executable()
    {
        const string aumid = "Contoso.Suite_abc!App";
        WorkspaceEntry entry = Entry(
            @"C:\Program Files\WindowsApps\Contoso.Suite_1\Host.exe",
            "Suite",
            "primary");
        entry.AppUserModelId = aumid;

        RestorePlan plan = Build(
            Snapshot(entry),
            runningApplications:
            [
                new RunningApplicationIdentity(
                    @"C:\Program Files\WindowsApps\Contoso.Suite_2\Host.exe",
                    "Host",
                    aumid)
            ]);

        RestorePlanEntry planned = Assert.Single(plan.Entries);
        Assert.Equal(RestorePlanEntryOutcome.Excluded, planned.Outcome);
        Assert.Empty(planned.Actions);
    }

    [Fact]
    public void Duplicate_entries_preserve_multiplicity_but_do_not_reuse_one_live_hwnd()
    {
        WorkspaceEntry first = Entry(@"C:\Apps\editor.exe", "Notes", "primary");
        WorkspaceEntry duplicate = Entry(@"C:\Apps\editor.exe", "Notes", "primary");
        LiveWindowIdentity live = Live(33, first.ExecutablePath, "Notes", "EditorWindow");

        RestorePlan plan = Build(Snapshot(first, duplicate), [live]);

        Assert.Equal(RestorePlanEntryOutcome.Matched, plan.Entries[0].Outcome);
        Assert.Equal(RestorePlanEntryOutcome.Excluded, plan.Entries[1].Outcome);
        Assert.Empty(plan.Entries[1].Actions);
        Assert.Contains(plan.Entries[1].Warnings,
            warning => warning.Code == RestorePlanIssueCode.RunningApplicationHasNoRestorableWindow);
    }

    [Fact]
    public void Cross_process_title_match_is_rejected_without_shared_platform_identity()
    {
        WorkspaceEntry entry = Entry(
            @"C:\Program Files\WindowsApps\Contoso.Host_2.0_x64__abc\Host.exe",
            "Dashboard",
            "primary");
        entry.ProcessName = "Host";
        entry.WindowClassName = "WinUIDesktopWin32WindowClass";
        var live = Live(
            34,
            @"C:\Runtime\renderer-helper.exe",
            "Dashboard",
            "RuntimeSurface");
        RestorePlan plan = RestorePlanner.Build(
            Snapshot(entry),
            new RestoreLiveInventory
            {
                Windows = [live],
                Resources =
                [
                    new RestoreResourceObservation(
                        0,
                        RestoreResourceKind.PackagedApplication,
                        RestoreResourceAvailability.Available,
                        "Contoso.Host_abc!App")
                ]
            },
            Topology(Monitor("primary", 0, 96, primary: true)),
            RestoreMode.Standard);

        RestorePlanEntry planned = Assert.Single(plan.Entries);
        Assert.Null(planned.SelectedMatch);
        Assert.DoesNotContain(planned.Candidates, candidate => candidate.IsEligible);
    }

    [Fact]
    public void Learned_hint_selects_previous_choice_without_runtime_ids()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Alpha report", "primary");
        var snapshot = Snapshot(entry);
        LiveWindowIdentity north = Live(31, entry.ExecutablePath, "Alpha report north", "EditorWindow");
        LiveWindowIdentity south = Live(32, entry.ExecutablePath, "Alpha report south", "EditorWindow");
        var hint = new WindowMatchHint
        {
            WorkspaceId = snapshot.WorkspaceId,
            EntryId = entry.EntryId,
            Identity = WindowIdentityExtractor.ToHint(north)
        };

        RestorePlan plan = RestorePlanner.Build(
            snapshot,
            new RestoreLiveInventory { Windows = [south, north], MatchHints = [hint] },
            Topology(Monitor("primary", 0, 96, primary: true)),
            RestoreMode.Standard);

        RestorePlanCandidate selected = Assert.IsType<RestorePlanCandidate>(
            Assert.Single(plan.Entries).SelectedMatch);
        Assert.Equal(31, selected.WindowHandle);
        Assert.True(selected.IsLearnedHintMatch);
        Assert.DoesNotContain(selected.IdentityHint!.GetType().GetProperties(), property =>
            property.Name is "Hwnd" or "WindowHandle" or "Pid" or "ProcessId");
    }

    [Fact]
    public void Missing_and_stale_resources_are_blocked_outcomes_instead_of_exceptions()
    {
        WorkspaceEntry document = Entry(@"C:\Apps\writer.exe", "Thesis", "primary");
        document.LaunchArg = @"Z:\missing\thesis.docx";
        WorkspaceEntry application = Entry(@"C:\Old\retired.exe", "Retired", "primary");
        var snapshot = Snapshot(document, application);

        RestorePlan plan = RestorePlanner.Build(
            snapshot,
            new RestoreLiveInventory
            {
                Resources =
                [
                    new(0, RestoreResourceKind.LaunchTarget, RestoreResourceAvailability.Missing),
                    new(1, RestoreResourceKind.Executable, RestoreResourceAvailability.Stale)
                ]
            },
            Topology(Monitor("primary", 0, 96, primary: true)),
            RestoreMode.Standard);

        Assert.All(plan.Entries, entry => Assert.Equal(RestorePlanEntryOutcome.Blocked, entry.Outcome));
        Assert.Contains(
            plan.Entries[0].BlockingErrors,
            error => error.Code == RestorePlanIssueCode.MissingResource);
        Assert.Contains(
            plan.Entries[1].BlockingErrors,
            error => error.Code == RestorePlanIssueCode.StaleResource);
        Assert.Empty(plan.Actions);
        Assert.False(plan.CanExecute);
    }

    [Fact]
    public void Pwa_plan_selects_exact_aumid_and_rejects_ordinary_browser_window()
    {
        const string browser = @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe";
        const string pwaAumid = "Brave.MPAabcdefghijklmnopabcdefghijklmnop";
        WorkspaceEntry entry = Entry(browser, "Trading terminal", "primary");
        entry.ProcessName = "brave";
        entry.IsWebApp = true;
        entry.AppUserModelId = pwaAumid;

        RestorePlan plan = Build(
            Snapshot(entry),
            [
                Live(11, browser, "Trading terminal", "Chrome_WidgetWin_1"),
                Live(12, browser, "Trading terminal", "Chrome_WidgetWin_1", pwaAumid)
            ]);

        RestorePlanEntry result = Assert.Single(plan.Entries);
        Assert.Equal(12, result.SelectedMatch?.WindowHandle);
        Assert.Equal(RestorePlanEntryOutcome.Matched, result.Outcome);
        Assert.Contains(
            result.SelectedMatch!.Evidence,
            evidence => evidence.Kind == WindowMatchEvidenceKind.PwaIdentityExact && evidence.Matched);
        Assert.Contains(result.Candidates, candidate => candidate.WindowHandle == 11 && !candidate.IsEligible);
    }

    [Fact]
    public void Dedicated_browser_plan_preserves_same_site_separation()
    {
        const string browser = @"C:\Apps\brave.exe";
        WorkspaceEntry entry = Entry(browser, "Charts", "primary");
        entry.ProcessName = "brave";
        entry.IsDedicatedBrowserWindow = true;
        entry.BrowserUrl = "https://charts.example.test/workspace/one";

        RestorePlan plan = Build(
            Snapshot(entry),
            [
                Live(21, browser, "Other", "Chrome_WidgetWin_1", browserUrl: "https://news.example.test/"),
                Live(22, browser, "Chart", "Chrome_WidgetWin_1", browserUrl: "https://charts.example.test/workspace/two")
            ]);

        RestorePlanEntry result = Assert.Single(plan.Entries);
        Assert.Equal(22, result.SelectedMatch?.WindowHandle);
        Assert.Contains(
            result.SelectedMatch!.Evidence,
            evidence => evidence.Kind == WindowMatchEvidenceKind.DedicatedBrowserSiteExact && evidence.Matched);
    }

    [Fact]
    public void Dedicated_browser_without_a_match_plans_an_explicit_new_window_launch()
    {
        const string browser = @"C:\Apps\brave.exe";
        WorkspaceEntry entry = Entry(browser, "Charts", "primary");
        entry.ProcessName = "brave";
        entry.IsDedicatedBrowserWindow = true;
        entry.BrowserUrl = "https://charts.example.test/workspace/one";

        RestorePlan plan = RestorePlanner.Build(
            Snapshot(entry),
            new RestoreLiveInventory
            {
                Resources =
                [
                    new RestoreResourceObservation(
                        0,
                        RestoreResourceKind.Executable,
                        RestoreResourceAvailability.Available,
                        browser)
                ]
            },
            Topology(Monitor("primary", 0, 96, primary: true)),
            RestoreMode.Standard);

        RestorePlanEntry result = Assert.Single(plan.Entries);
        Assert.Equal(RestorePlanEntryOutcome.LaunchRequired, result.Outcome);
        RestoreAction launch = Assert.Single(
            result.Actions,
            action => action.Kind == RestoreActionKind.LaunchDedicatedBrowser);
        Assert.Equal(browser, launch.Target);
        Assert.Equal($"--new-window \"{entry.BrowserUrl}\"", launch.Arguments);
        Assert.False(launch.UseShellExecute);
        Assert.Contains(result.Actions, action => action.Kind == RestoreActionKind.AwaitWindowAppearance);
    }

    [Fact]
    public void Document_plan_uses_filename_evidence_without_reopening_correct_document()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\writer.exe", "Thesis - Writer", "primary");
        entry.LaunchArg = @"C:\Users\fixture\Documents\Thesis.docx";

        RestorePlan plan = Build(
            Snapshot(entry),
            [
                Live(31, entry.ExecutablePath, "Budget - Writer", "EditorWindow"),
                Live(32, entry.ExecutablePath, "THESIS - Writer", "EditorWindow")
            ]);

        RestorePlanEntry result = Assert.Single(plan.Entries);
        Assert.Equal(32, result.SelectedMatch?.WindowHandle);
        Assert.False(result.LaunchRequirement.IsRequired);
        Assert.Single(result.Actions);
        Assert.Contains(
            result.SelectedMatch!.Evidence,
            evidence => evidence.Kind == WindowMatchEvidenceKind.DocumentNameInTitle && evidence.Matched);
    }

    [Fact]
    public void Packaged_application_plan_uses_store_activation_identity()
    {
        WorkspaceEntry entry = Entry(
            @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_1.0.0.0_x64__8wekyb3d8bbwe\Notepad.exe",
            "Notepad",
            "primary");
        entry.ProcessName = "Notepad";
        entry.AppUserModelId = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App";

        RestorePlanEntry result = Assert.Single(Build(Snapshot(entry)).Entries);

        Assert.Equal(RestorePlanEntryOutcome.LaunchRequired, result.Outcome);
        RestoreAction action = Assert.Single(
            result.Actions,
            action => action.Kind == RestoreActionKind.ActivatePackagedApplication);
        Assert.Equal(RestoreActionKind.ActivatePackagedApplication, action.Kind);
        Assert.Equal("explorer.exe", action.Target);
        Assert.Equal($"shell:AppsFolder\\{entry.AppUserModelId}", action.Arguments);
        Assert.True(action.UseShellExecute);
    }

    [Fact]
    public void Stale_versioned_package_path_uses_resolved_stable_identity_instead_of_blocking()
    {
        WorkspaceEntry entry = Entry(
            @"C:\Program Files\WindowsApps\Contoso.Suite_1.0_x64__abc\Host.exe",
            "Contoso Suite",
            "primary");
        entry.ProcessName = "Host";
        const string aumid = "Contoso.Suite_abc!App";

        RestorePlan plan = RestorePlanner.Build(
            Snapshot(entry),
            new RestoreLiveInventory
            {
                Resources =
                [
                    new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Missing),
                    new(0, RestoreResourceKind.PackagedApplication,
                        RestoreResourceAvailability.Available, aumid)
                ]
            },
            Topology(Monitor("primary", 0, 96, primary: true)),
            RestoreMode.Standard);

        RestorePlanEntry result = Assert.Single(plan.Entries);
        Assert.Equal(RestorePlanEntryOutcome.LaunchRequired, result.Outcome);
        Assert.Empty(result.BlockingErrors);
        Assert.Contains(result.Warnings, warning => warning.Code == RestorePlanIssueCode.StaleResource);
        RestoreAction action = Assert.Single(result.Actions,
            action => action.Kind == RestoreActionKind.ActivatePackagedApplication);
        Assert.Equal("explorer.exe", action.Target);
        Assert.Equal($"shell:AppsFolder\\{aumid}", action.Arguments);
    }

    [Fact]
    public void Resolved_squirrel_path_matches_an_already_running_updated_window()
    {
        const string savedExecutable =
            @"C:\Users\Person\AppData\Local\ChatCanary\app-1.0.1133\ChatCanary.exe";
        const string currentExecutable =
            @"C:\Users\Person\AppData\Local\ChatCanary\app-1.0.1148\ChatCanary.exe";
        WorkspaceEntry entry = Entry(savedExecutable, "Friends", "primary");

        RestorePlan plan = RestorePlanner.Build(
            Snapshot(entry),
            new RestoreLiveInventory
            {
                Windows = [Live(44, currentExecutable, "Friends", "EditorWindow")],
                Resources =
                [
                    new RestoreResourceObservation(
                        0,
                        RestoreResourceKind.Executable,
                        RestoreResourceAvailability.Available,
                        currentExecutable)
                ]
            },
            Topology(Monitor("primary", 0, 96, primary: true)),
            RestoreMode.Standard);

        RestorePlanEntry result = Assert.Single(plan.Entries);
        Assert.Equal(RestorePlanEntryOutcome.Matched, result.Outcome);
        Assert.Equal(44, result.SelectedMatch?.WindowHandle);
        Assert.Contains(
            result.Warnings,
            warning => warning.Code == RestorePlanIssueCode.UpdatedExecutablePath);
        Assert.DoesNotContain(
            result.Actions,
            action => action.Kind == RestoreActionKind.LaunchApplication);
    }

    [Fact]
    public void WindowsApps_path_parser_preserves_package_full_name_and_relative_executable()
    {
        bool parsed = PackagedAppResolver.TrySplitPackagePath(
            @"C:\Program Files\WindowsApps\Contoso.App_2.1.0.0_x64__abc\bin\App.exe",
            out string fullName,
            out string relativeExecutable);

        Assert.True(parsed);
        Assert.Equal("Contoso.App_2.1.0.0_x64__abc", fullName);
        Assert.Equal(@"bin\App.exe", relativeExecutable);
    }

    [Fact]
    public void Target_placement_is_scaled_from_saved_to_current_dpi()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes", "primary", dpi: 96);
        entry.Position.NormalLeft = 100;
        entry.Position.NormalTop = 80;
        entry.Position.NormalRight = 900;
        entry.Position.NormalBottom = 680;

        RestorePlan plan = RestorePlanner.Build(
            Snapshot(entry),
            new RestoreLiveInventory
            {
                Resources =
                [
                    new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available)
                ]
            },
            Topology(Monitor("primary", 0, 144, primary: true)),
            RestoreMode.Standard);

        RestoreTargetPlacement target = Assert.Single(plan.Entries).TargetPlacement;
        Assert.Equal((150, 120, 1350, 1020), (target.Left, target.Top, target.Right, target.Bottom));
        Assert.Equal((uint)96, target.SavedDpi);
        Assert.Equal((uint)144, target.TargetDpi);
        Assert.True(target.WasDpiScaled);
    }

    [Fact]
    public void Align_and_minimize_plan_places_workspace_windows_before_terminal_minimize_action()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\editor.exe", "Notes", "primary");

        RestorePlan plan = RestorePlanner.Build(
            Snapshot(entry),
            new RestoreLiveInventory
            {
                Windows = [Live(45, entry.ExecutablePath, "Notes", "EditorWindow")]
            },
            Topology(Monitor("primary", 0, 96, primary: true)),
            RestoreMode.AlignAndMinimize);

        Assert.Equal(
            [RestoreActionKind.RestoreExistingWindow, RestoreActionKind.MinimizeOtherWindows],
            plan.Actions.Select(action => action.Kind));
        Assert.Null(plan.Actions[^1].EntryIndex);
    }

    [Fact]
    public void Selective_and_cancelled_plans_retain_an_outcome_for_every_saved_entry()
    {
        WorkspaceEntry first = Entry(@"C:\Apps\one.exe", "One", "left");
        WorkspaceEntry second = Entry(@"C:\Apps\two.exe", "Two", "right");
        var snapshot = Snapshot(first, second);
        var inventory = new RestoreLiveInventory
        {
            Windows =
            [
                Live(51, first.ExecutablePath, "One", "EditorWindow", monitorId: "left"),
                Live(52, second.ExecutablePath, "Two", "EditorWindow", monitorId: "right")
            ]
        };
        var topology = Topology(
            Monitor("left", 0, 96, primary: true),
            Monitor("right", 1, 96));

        RestorePlan selective = RestorePlanner.Build(
            snapshot,
            inventory,
            topology,
            RestoreMode.Selective("left"));
        RestorePlan cancelled = RestorePlanner.Build(
            snapshot,
            inventory,
            topology,
            new RestoreMode { CancellationRequested = true });

        Assert.Collection(
            selective.Entries,
            entry => Assert.Equal(RestorePlanEntryOutcome.Matched, entry.Outcome),
            entry => Assert.Equal(RestorePlanEntryOutcome.Excluded, entry.Outcome));
        Assert.Single(selective.Actions);
        Assert.All(cancelled.Entries, entry => Assert.Equal(RestorePlanEntryOutcome.Cancelled, entry.Outcome));
        Assert.Empty(cancelled.Actions);
        Assert.True(cancelled.WasCancelled);
        Assert.False(cancelled.CanExecute);
    }

    [Fact]
    public void Browser_session_availability_is_explicit_and_degrades_to_direct_launch()
    {
        WorkspaceEntry entry = Entry(@"C:\Apps\brave.exe", "Browser", "primary");
        entry.ProcessName = "brave";
        WorkspaceSnapshot snapshot = Snapshot(entry);
        snapshot.BrowserSessions.Add(new BrowserSession { Browser = "brave", ActiveTitle = "Browser" });
        var topology = Topology(Monitor("primary", 0, 96, primary: true));

        RestorePlan available = RestorePlanner.Build(
            snapshot,
            new RestoreLiveInventory
            {
                BrowserSessionRestore = BrowserSessionRestoreAvailability.Available
            },
            topology,
            RestoreMode.Standard);
        RestorePlan unavailable = RestorePlanner.Build(
            snapshot,
            new RestoreLiveInventory
            {
                BrowserSessionRestore = BrowserSessionRestoreAvailability.Unavailable,
                Resources =
                [
                    new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available)
                ]
            },
            topology,
            RestoreMode.Standard);

        Assert.Equal(RestorePlanEntryOutcome.AwaitingBrowserSession, available.Entries[0].Outcome);
        Assert.Contains(available.Actions, action => action.Kind == RestoreActionKind.RestoreBrowserSession);
        Assert.Equal(RestorePlanEntryOutcome.LaunchRequired, unavailable.Entries[0].Outcome);
        RestoreAction fallback = Assert.Single(
            unavailable.Entries[0].Actions,
            action => action.Kind == RestoreActionKind.LaunchApplication);
        Assert.Equal(RestoreActionKind.LaunchApplication, fallback.Kind);
        Assert.Equal("--restore-last-session", fallback.Arguments);
        Assert.Contains(
            unavailable.Warnings,
            warning => warning.Code == RestorePlanIssueCode.BrowserSessionUnavailable);
    }

    [Fact]
    public void Redacted_plan_json_removes_workspace_paths_titles_identifiers_and_secrets()
    {
        WorkspaceEntry entry = Entry(
            @"C:\Users\Alice\Apps\browser.exe",
            "Sensitive customer dashboard",
            "primary");
        entry.ProcessName = "brave";
        entry.IsWebApp = true;
        entry.AppUserModelId = "Brave.SecretApplicationIdentity";
        entry.WebAppLaunchTarget = entry.ExecutablePath;
        entry.WebAppLaunchArguments = "--profile-directory=Alice --token top-secret-value";
        WorkspaceSnapshot snapshot = Snapshot(entry);
        snapshot.Name = "Alice private workspace";
        snapshot.BrowserSessions.Add(new BrowserSession
        {
            Browser = "brave",
            ActiveTitle = "Alice banking dashboard",
            Tabs =
            [
                new BrowserTab
                {
                    Url = "https://bank.example/private?token=browser-secret",
                    Title = "Alice account balance"
                }
            ],
            Groups = [new BrowserTabGroup { Title = "Private finances" }]
        });

        RestorePlan plan = RestorePlanner.Build(
            snapshot,
            new RestoreLiveInventory
            {
                BrowserSessionRestore = BrowserSessionRestoreAvailability.Available,
                Resources =
                [
                    new(0, RestoreResourceKind.Executable, RestoreResourceAvailability.Available)
                ]
            },
            Topology(Monitor("primary", 0, 96, primary: true)),
            RestoreMode.Standard);
        string json = plan.ToRedactedJson(writeIndented: true);

        using JsonDocument parsed = JsonDocument.Parse(json);
        Assert.DoesNotContain("Alice private workspace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\Users\Alice", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sensitive customer dashboard", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Brave.SecretApplicationIdentity", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("top-secret-value", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Alice banking dashboard", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("browser-secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Alice account balance", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private finances", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "<workspace:redacted>",
            parsed.RootElement.GetProperty("workspaceName").GetString());
        Assert.Contains("path:redacted.exe", json);
        Assert.Contains("redacted", json);
    }

    private static RestorePlan Build(
        WorkspaceSnapshot snapshot,
        IReadOnlyList<LiveWindowIdentity>? windows = null,
        IReadOnlyList<RunningApplicationIdentity>? runningApplications = null) =>
        RestorePlanner.Build(
            snapshot,
            new RestoreLiveInventory
            {
                Windows = windows ?? Array.Empty<LiveWindowIdentity>(),
                RunningApplications = runningApplications ?? Array.Empty<RunningApplicationIdentity>()
            },
            Topology(Monitor("primary", 0, 96, primary: true)),
            RestoreMode.Standard);

    private static WorkspaceSnapshot Snapshot(params WorkspaceEntry[] entries) => new()
    {
        WorkspaceId = "workspace-fixture-id",
        Name = "Fixture workspace",
        SavedAt = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc),
        Entries = entries.ToList()
    };

    private static WorkspaceEntry Entry(
        string executable,
        string title,
        string monitorId,
        uint dpi = 96) => new()
    {
        ExecutablePath = executable,
        ProcessName = Path.GetFileNameWithoutExtension(executable),
        WindowClassName = "EditorWindow",
        MonitorId = monitorId,
        MonitorIndex = monitorId == "primary" || monitorId == "left" ? 0 : 1,
        Position = new WindowRecord
        {
            ExecutablePath = executable,
            ProcessName = Path.GetFileNameWithoutExtension(executable),
            ClassName = "EditorWindow",
            TitleSnippet = title,
            MonitorId = monitorId,
            MonitorIndex = monitorId == "primary" || monitorId == "left" ? 0 : 1,
            SavedDpi = dpi,
            NormalLeft = 0,
            NormalTop = 0,
            NormalRight = 800,
            NormalBottom = 600,
            ShowCmd = 1
        }
    };

    private static LiveWindowIdentity Live(
        long hwnd,
        string executable,
        string title,
        string className,
        string aumid = "",
        string browserUrl = "",
        string monitorId = "primary") => WindowIdentityExtractor.FromLive(
            new IntPtr(hwnd),
            (uint)(1000 + hwnd),
            new WindowRecord
            {
                ExecutablePath = executable,
                ProcessName = Path.GetFileNameWithoutExtension(executable),
                ClassName = className,
                TitleSnippet = title,
                AppUserModelId = aumid,
                BrowserUrl = browserUrl,
                MonitorId = monitorId,
                MonitorIndex = monitorId == "primary" || monitorId == "left" ? 0 : 1,
                NormalLeft = 0,
                NormalTop = 0,
                NormalRight = 800,
                NormalBottom = 600
            });

    private static RestoreMonitorTopology Topology(params RestoreMonitor[] monitors) => new()
    {
        Monitors = monitors
    };

    private static RestoreMonitor Monitor(
        string id,
        int index,
        uint dpi,
        bool primary = false) => new(
            id,
            index,
            index * 1920,
            0,
            (index + 1) * 1920,
            1080,
            dpi,
            primary);
}
