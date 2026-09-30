using WindowAnchor.Models;
using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class RestoreSimulationTests
{
    private static readonly string FixtureDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "restore-simulations");

    [Fact]
    public void Versioned_fixture_runs_deterministically_and_satisfies_its_golden_expectations()
    {
        RestoreSimulationFixture fixture = RestoreSimulationRunner.Load(
            TestDirectory.FixturePath(@"restore-simulations\single-monitor.json"));

        RestoreSimulationResult first = RestoreSimulationRunner.Run(fixture);
        RestoreSimulationResult second = RestoreSimulationRunner.Run(fixture);

        Assert.True(first.Succeeded, string.Join(Environment.NewLine, first.ExpectationFailures));
        Assert.Equal(first.ToRedactedJson(), second.ToRedactedJson());
        Assert.Single(first.Plan.Actions);
    }

    [Fact]
    public void Adversarial_fixture_reports_synthetic_mapping_and_never_leaks_sensitive_values()
    {
        RestoreSimulationFixture fixture = RestoreSimulationRunner.Load(
            TestDirectory.FixturePath(@"restore-simulations\adversarial-topology.json"));

        RestoreSimulationResult result = RestoreSimulationRunner.Run(fixture);
        string json = result.ToRedactedJson();

        Assert.Equal(3, result.Plan.Entries.Count);
        Assert.DoesNotContain(@"C:\Users\fixture", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private dashboard", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitivepwaid", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture-secret-token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test/private", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("monitorMapping", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("confidence", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Golden_expectation_mismatches_are_explicit_and_diffable()
    {
        RestoreSimulationFixture fixture = RestoreSimulationRunner.Load(
            TestDirectory.FixturePath(@"restore-simulations\single-monitor.json"));
        fixture = fixture with
        {
            Expected = fixture.Expected! with { ActionKinds = [RestoreActionKind.LaunchApplication] }
        };

        RestoreSimulationResult result = RestoreSimulationRunner.Run(fixture);

        Assert.False(result.Succeeded);
        Assert.Contains(result.ExpectationFailures, failure => failure.StartsWith("actionKinds:"));
    }

    [Fact]
    public void Complete_fixture_matrix_is_deterministic_and_satisfies_entry_plan_goldens()
    {
        string[] fixturePaths = Directory.GetFiles(FixtureDirectory, "*.json")
            .Where(path => !Path.GetFileName(path).Equals("corrupt-input.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(fixturePaths.Length >= 7, "The restore simulation matrix is incomplete.");
        foreach (string path in fixturePaths)
        {
            RestoreSimulationFixture fixture = RestoreSimulationRunner.Load(path);
            Assert.NotNull(fixture.Expected);
            Assert.NotNull(fixture.Expected!.EntryPlans);

            RestoreSimulationResult first = RestoreSimulationRunner.Run(fixture);
            RestoreSimulationResult second = RestoreSimulationRunner.Run(fixture);

            Assert.True(first.Succeeded,
                $"{Path.GetFileName(path)}:{Environment.NewLine}{string.Join(Environment.NewLine, first.ExpectationFailures)}");
            Assert.Equal(first.ToRedactedJson(), second.ToRedactedJson());
        }
    }

    [Fact]
    public void Corrupt_fixture_is_rejected_deterministically_before_any_planning()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => RestoreSimulationRunner.Load(
            Path.Combine(FixtureDirectory, "corrupt-input.json")));

        Assert.Equal("Unsupported restore simulation fixture schema version 999.", exception.Message);
    }
}
