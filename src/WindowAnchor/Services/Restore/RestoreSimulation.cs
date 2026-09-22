using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowAnchor.Models;

namespace WindowAnchor.Services;

/// <summary>
/// Versioned, human-editable input for a restore-planning simulation. It intentionally contains
/// only already-observed facts, so loading or running a fixture cannot call native discovery or
/// mutate a live window.
/// </summary>
public sealed record RestoreSimulationFixture
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Name { get; init; } = "";
    public WorkspaceSnapshot Snapshot { get; init; } = new();
    public RestoreMonitorTopology CurrentTopology { get; init; } = new();
    public RestoreMode Mode { get; init; } = RestoreMode.Resume;
    public IReadOnlyList<RestoreSimulationLiveWindow> LiveWindows { get; init; } =
        Array.Empty<RestoreSimulationLiveWindow>();
    public IReadOnlyList<RestoreResourceObservation> Resources { get; init; } =
        Array.Empty<RestoreResourceObservation>();
    public IReadOnlyList<RunningApplicationIdentity> RunningApplications { get; init; } =
        Array.Empty<RunningApplicationIdentity>();
    public BrowserSessionRestoreAvailability BrowserSessionRestore { get; init; } =
        BrowserSessionRestoreAvailability.NotAvailable;
    public IReadOnlyList<WindowMatchHint> MatchHints { get; init; } = Array.Empty<WindowMatchHint>();
    public IReadOnlyList<PersistentApplicationIdentity> PersistentApplications { get; init; } =
        Array.Empty<PersistentApplicationIdentity>();
    public RestoreSimulationExpectation? Expected { get; init; }
}

/// <summary>Serializable live-window fact that avoids runtime HWND/PID types in fixture JSON.</summary>
public sealed record RestoreSimulationLiveWindow(
    long WindowHandle,
    uint ProcessId,
    WindowRecord Window);

/// <summary>Optional golden checks that make planner regressions fail with diffable messages.</summary>
public sealed record RestoreSimulationExpectation
{
    public IReadOnlyList<RestorePlanEntryOutcome>? EntryOutcomes { get; init; }
    public IReadOnlyList<RestoreActionKind>? ActionKinds { get; init; }
    public IReadOnlyList<long>? ProtectedWindowHandles { get; init; }
    public IReadOnlyList<RestorePlanIssueCode>? WarningCodes { get; init; }
}

/// <summary>Privacy-safe, deterministic simulation result suitable for CI output or sharing.</summary>
public sealed record RestoreSimulationResult(
    string FixtureName,
    RestorePlan Plan,
    IReadOnlyList<string> ExpectationFailures)
{
    public bool Succeeded => ExpectationFailures.Count == 0;

    public string ToRedactedJson(bool writeIndented = true)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Serialize(this with { Plan = Plan.Redact() }, options);
    }
}

/// <summary>Loads versioned simulation fixtures and runs only the pure restore planner.</summary>
public static class RestoreSimulationRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    static RestoreSimulationRunner() => JsonOptions.Converters.Add(new JsonStringEnumConverter());

    public static RestoreSimulationFixture Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RestoreSimulationFixture fixture = JsonSerializer.Deserialize<RestoreSimulationFixture>(
            File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException(
                "Restore simulation fixture deserialized to null.");
        if (fixture.SchemaVersion != RestoreSimulationFixture.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Unsupported restore simulation fixture schema version {fixture.SchemaVersion}.");
        return fixture;
    }

    public static RestoreSimulationResult Run(RestoreSimulationFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(fixture.Snapshot);
        ArgumentNullException.ThrowIfNull(fixture.CurrentTopology);
        ArgumentNullException.ThrowIfNull(fixture.Mode);

        var inventory = new RestoreLiveInventory
        {
            Windows = fixture.LiveWindows
                .Select(live => WindowIdentityExtractor.FromLive(
                    new IntPtr(live.WindowHandle), live.ProcessId, live.Window))
                .ToArray(),
            Resources = fixture.Resources,
            RunningApplications = fixture.RunningApplications,
            BrowserSessionRestore = fixture.BrowserSessionRestore,
            MatchHints = fixture.MatchHints
        };
        RestorePlan plan = RestorePlanner.Build(
            fixture.Snapshot,
            inventory,
            fixture.CurrentTopology,
            fixture.Mode,
            AppAdapterRegistry.CreatePlanningDefault(),
            fixture.PersistentApplications);
        return new RestoreSimulationResult(
            fixture.Name,
            plan,
            ValidateExpectation(plan, fixture.Expected));
    }

    private static IReadOnlyList<string> ValidateExpectation(
        RestorePlan plan,
        RestoreSimulationExpectation? expected)
    {
        if (expected is null) return Array.Empty<string>();
        var failures = new List<string>();
        Compare("entryOutcomes", expected.EntryOutcomes, plan.Entries.Select(entry => entry.Outcome), failures);
        Compare("actionKinds", expected.ActionKinds, plan.Actions.Select(action => action.Kind), failures);
        Compare("protectedWindowHandles", expected.ProtectedWindowHandles,
            plan.ProtectedWindowHandles.OrderBy(handle => handle), failures);
        Compare("warningCodes", expected.WarningCodes,
            plan.Warnings.Concat(plan.Entries.SelectMany(entry => entry.Warnings))
                .Select(warning => warning.Code).Distinct().OrderBy(code => code), failures);
        return failures;
    }

    private static void Compare<T>(
        string name,
        IReadOnlyList<T>? expected,
        IEnumerable<T> actual,
        ICollection<string> failures)
    {
        if (expected is null) return;
        T[] actualValues = actual.ToArray();
        if (!expected.SequenceEqual(actualValues))
            failures.Add($"{name}: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actualValues)}].");
    }
}
