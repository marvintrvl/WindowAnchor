using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using WindowAnchor.Native;

namespace WindowAnchor.Services;

/// <summary>A stable activation identity recovered from a versioned WindowsApps executable.</summary>
public sealed record PackagedAppResolution(
    string AppUserModelId,
    string ExecutablePath,
    string PackageFamilyName,
    bool ExecutableWasRebased);

/// <summary>Resolves Store/MSIX launch identity without treating a versioned executable as durable.</summary>
public interface IPackagedAppResolver
{
    PackagedAppResolution? Resolve(string executablePath, string? appUserModelId = null);
}

/// <summary>
/// Rebinds a saved WindowsApps path to the currently registered package family and reads the
/// package-relative application ID from AppxManifest.xml. Package updates replace the versioned
/// install directory, while <c>PackageFamilyName!ApplicationId</c> remains stable.
/// </summary>
public sealed class PackagedAppResolver : IPackagedAppResolver
{
    public PackagedAppResolution? Resolve(string executablePath, string? appUserModelId = null)
    {
        try
        {
            return ResolveCore(executablePath, appUserModelId);
        }
        catch (Exception ex)
        {
            // Package repair is a best-effort fallback. A registry race, unreadable manifest, or
            // unavailable AppModel API must not break window enumeration or restore planning.
            AppLogger.Debug(
                "packaged_app.identity_resolution_failed",
                "Could not resolve a stable packaged-application identity",
                ex,
                LogField.Path("executablePath", executablePath),
                LogField.Public("errorCategory", "package_identity_resolution"));
            return null;
        }
    }

    private static PackagedAppResolution? ResolveCore(
        string executablePath,
        string? appUserModelId)
    {
        if (!TrySplitPackagePath(
                executablePath,
                out string packageFullName,
                out string relativeExecutable))
        {
            return null;
        }

        string family = FamilyNameFromFullName(packageFullName);
        if (family.Length == 0)
            return null;

        string knownAumid = appUserModelId?.Trim() ?? "";
        foreach ((string _, string installPath) in InstalledPackagePaths(family))
        {
            string currentExecutable = Path.Combine(
                installPath,
                relativeExecutable.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(currentExecutable))
                continue;

            string applicationId = ReadApplicationId(installPath, relativeExecutable);
            string resolvedAumid = knownAumid.Contains('!')
                ? knownAumid
                : applicationId.Length > 0
                    ? $"{family}!{applicationId}"
                    : "";
            if (resolvedAumid.Length == 0)
                continue;

            return new PackagedAppResolution(
                resolvedAumid,
                currentExecutable,
                family,
                !string.Equals(
                    Path.GetFullPath(currentExecutable),
                    Path.GetFullPath(executablePath),
                    StringComparison.OrdinalIgnoreCase));
        }

        // A previously captured AUMID remains a valid Shell activation identity even if package
        // path discovery is temporarily unavailable.
        return knownAumid.Contains('!')
            ? new PackagedAppResolution(knownAumid, executablePath, family, false)
            : null;
    }

    internal static bool TrySplitPackagePath(
        string executablePath,
        out string packageFullName,
        out string relativeExecutable)
    {
        packageFullName = "";
        relativeExecutable = "";
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        const string marker = @"\WindowsApps\";
        int packageStart = executablePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (packageStart < 0)
            return false;
        packageStart += marker.Length;
        int packageEnd = executablePath.IndexOfAny(['\\', '/'], packageStart);
        if (packageEnd <= packageStart || packageEnd >= executablePath.Length - 1)
            return false;

        packageFullName = executablePath[packageStart..packageEnd];
        relativeExecutable = executablePath[(packageEnd + 1)..];
        return packageFullName.Length > 0 && relativeExecutable.Length > 0;
    }

    private static string FamilyNameFromFullName(string packageFullName)
    {
        try
        {
            uint length = 0;
            int rc = NativeMethodsShell.PackageFamilyNameFromFullName(
                packageFullName,
                ref length,
                null);
            if (rc != NativeMethodsShell.ERROR_INSUFFICIENT_BUFFER || length == 0)
                return "";

            var value = new StringBuilder((int)length);
            rc = NativeMethodsShell.PackageFamilyNameFromFullName(
                packageFullName,
                ref length,
                value);
            return rc == NativeMethodsShell.ERROR_SUCCESS ? value.ToString() : "";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return "";
        }
    }

    private static IReadOnlyList<(string FullName, string InstallPath)> InstalledPackagePaths(
        string family)
    {
        uint count = 0;
        uint bufferLength = 0;
        int rc;
        try
        {
            rc = NativeMethodsShell.GetPackagesByPackageFamily(
                family,
                ref count,
                IntPtr.Zero,
                ref bufferLength,
                IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Array.Empty<(string, string)>();
        }
        if (rc != NativeMethodsShell.ERROR_INSUFFICIENT_BUFFER || count == 0 || bufferLength == 0)
            return Array.Empty<(string, string)>();

        IntPtr names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bufferLength * sizeof(char)));
        try
        {
            rc = NativeMethodsShell.GetPackagesByPackageFamily(
                family,
                ref count,
                names,
                ref bufferLength,
                buffer);
            if (rc != NativeMethodsShell.ERROR_SUCCESS)
                return Array.Empty<(string, string)>();

            var packages = new List<(string FullName, string InstallPath)>((int)count);
            for (int index = 0; index < count; index++)
            {
                IntPtr namePointer = Marshal.ReadIntPtr(names, index * IntPtr.Size);
                string fullName = Marshal.PtrToStringUni(namePointer) ?? "";
                string path = StagedPath(fullName);
                if (fullName.Length > 0 && path.Length > 0)
                    packages.Add((fullName, path));
            }
            return packages
                .OrderByDescending(package => PackageVersion(package.FullName))
                .ThenBy(package => package.FullName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(names);
        }
    }

    private static Version PackageVersion(string packageFullName)
    {
        string[] parts = packageFullName.Split('_');
        return parts.Length >= 5 && Version.TryParse(parts[^4], out Version? version)
            ? version
            : new Version(0, 0);
    }

    private static string StagedPath(string packageFullName)
    {
        uint length = 0;
        int rc = NativeMethodsShell.GetStagedPackagePathByFullName(
            packageFullName,
            ref length,
            null);
        if (rc != NativeMethodsShell.ERROR_INSUFFICIENT_BUFFER || length == 0)
            return "";

        var path = new StringBuilder((int)length);
        rc = NativeMethodsShell.GetStagedPackagePathByFullName(
            packageFullName,
            ref length,
            path);
        return rc == NativeMethodsShell.ERROR_SUCCESS ? path.ToString() : "";
    }

    private static string ReadApplicationId(string installPath, string relativeExecutable)
    {
        try
        {
            string manifestPath = Path.Combine(installPath, "AppxManifest.xml");
            XDocument manifest = XDocument.Load(manifestPath, LoadOptions.None);
            string normalizedRelative = relativeExecutable.Replace('/', '\\');
            XElement? application = manifest
                .Descendants()
                .Where(element => element.Name.LocalName == "Application")
                .FirstOrDefault(element => string.Equals(
                    ((string?)element.Attribute("Executable") ?? "").Replace('/', '\\'),
                    normalizedRelative,
                    StringComparison.OrdinalIgnoreCase));
            application ??= manifest
                .Descendants()
                .Where(element => element.Name.LocalName == "Application")
                .FirstOrDefault(element => string.Equals(
                    Path.GetFileName((string?)element.Attribute("Executable") ?? ""),
                    Path.GetFileName(normalizedRelative),
                    StringComparison.OrdinalIgnoreCase));
            return ((string?)application?.Attribute("Id") ?? "").Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Xml.XmlException or ArgumentException)
        {
            return "";
        }
    }
}
