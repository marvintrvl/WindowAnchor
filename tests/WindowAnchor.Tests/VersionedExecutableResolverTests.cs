using WindowAnchor.Services;

namespace WindowAnchor.Tests;

public class VersionedExecutableResolverTests
{
    [Fact]
    public void Missing_squirrel_executable_resolves_to_highest_installed_version()
    {
        using var directory = new TestDirectory();
        string root = Path.Combine(directory.Path, "DiscordCanary");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Update.exe"), "updater");
        string oldExecutable = Path.Combine(root, "app-1.0.1133", "DiscordCanary.exe");
        string lower = CreateExecutable(root, "app-1.0.1140", "DiscordCanary.exe");
        string expected = CreateExecutable(root, "app-1.0.1148", "DiscordCanary.exe");
        _ = lower;

        var boundary = new FileSystemRestoreResourceBoundary();
        RestoreResourceObservation observation = boundary.Observe(
            0,
            RestoreResourceKind.Executable,
            oldExecutable);

        Assert.Equal(RestoreResourceAvailability.Available, observation.Availability);
        Assert.Equal(expected, observation.ResolvedTarget);
    }

    [Fact]
    public void Resolver_does_not_wildcard_unrecognized_install_roots()
    {
        using var directory = new TestDirectory();
        string root = Path.Combine(directory.Path, "Unrecognized");
        Directory.CreateDirectory(root);
        string oldExecutable = Path.Combine(root, "app-1.0.0", "App.exe");
        _ = CreateExecutable(root, "app-2.0.0", "App.exe");

        var boundary = new FileSystemRestoreResourceBoundary();
        RestoreResourceObservation observation = boundary.Observe(
            0,
            RestoreResourceKind.Executable,
            oldExecutable);

        Assert.Equal(RestoreResourceAvailability.Missing, observation.Availability);
        Assert.Empty(observation.ResolvedTarget);
    }

    [Fact]
    public void Exact_existing_executable_is_never_replaced_by_sibling_scan()
    {
        using var directory = new TestDirectory();
        string root = Path.Combine(directory.Path, "SquirrelApp");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Update.exe"), "updater");
        string exact = CreateExecutable(root, "app-1.0.0", "App.exe");
        _ = CreateExecutable(root, "app-2.0.0", "App.exe");

        var boundary = new FileSystemRestoreResourceBoundary();
        RestoreResourceObservation observation = boundary.Observe(
            0,
            RestoreResourceKind.Executable,
            exact);

        Assert.Equal(exact, observation.ResolvedTarget);
    }

    private static string CreateExecutable(string root, string versionDirectory, string name)
    {
        string directory = Path.Combine(root, versionDirectory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "executable");
        return path;
    }
}
