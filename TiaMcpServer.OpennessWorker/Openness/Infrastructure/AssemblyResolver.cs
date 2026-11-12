using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TiaMcpServer.OpennessWorker.Openness.Infrastructure;

public static class AssemblyResolver
{
    private const string TiaPortalV21DirEnvironmentVariable = "TiaPortalV21Dir";
    private const string TiaPortalLocationEnvironmentVariable = "TiaPortalLocation";
    private const string TiaPortalV21RegistrySubKey =
        @"SOFTWARE\Siemens\Automation\InstalledApps\Totally Integrated Automation Portal V21";
    // Openness registers its assemblies here on every installation, also off the default drive.
    private const string OpennessRegistrySubKey =
        @"SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48";
    private const string OpennessRegistryValueName = "Siemens.Engineering.Base";
    private const string SkipRegistryEnvironmentVariable = "TiaPortalSkipRegistry";
    private const string StandardOpennessInstallPath =
        @"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48";
    private const string PublicApiSuffix = @"PublicAPI\V21\net48";

    private static string? _selectedInstallPath;

    private static readonly string[] RequiredAssemblies =
    {
        "Siemens.Engineering.Base.dll",
        "Siemens.Engineering.Step7.dll"
    };

    public static void Register()
    {
        AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
    }

    internal static string ExpandTiaPortalLocation(string tiaPortalRoot)
    {
        return Path.Combine(tiaPortalRoot.Trim().Trim('"'), PublicApiSuffix);
    }

    internal static IEnumerable<string> CreateCandidatePaths(
        string? tiaPortalV21Dir,
        string? tiaPortalLocation,
        IEnumerable<string?> registryInstallPaths,
        string defaultPath,
        IEnumerable<string?>? registeredOpennessAssemblies = null)
    {
        if (!string.IsNullOrWhiteSpace(tiaPortalV21Dir))
        {
            yield return tiaPortalV21Dir!.Trim().Trim('"');
        }

        if (!string.IsNullOrWhiteSpace(tiaPortalLocation))
        {
            yield return ExpandTiaPortalLocation(tiaPortalLocation!);
        }

        foreach (var registryInstallPath in registryInstallPaths)
        {
            if (!string.IsNullOrWhiteSpace(registryInstallPath))
            {
                yield return ExpandTiaPortalLocation(registryInstallPath!);
            }
        }

        foreach (var registeredAssembly in registeredOpennessAssemblies ?? Enumerable.Empty<string?>())
        {
            if (!string.IsNullOrWhiteSpace(registeredAssembly))
            {
                var directory = Path.GetDirectoryName(registeredAssembly!.Trim().Trim('"'));
                if (!string.IsNullOrEmpty(directory))
                {
                    yield return directory!;
                }
            }
        }

        yield return defaultPath;
    }

    /// <summary>
    /// The message to show when no candidate holds the Openness assemblies, null when one does.
    /// Called before a request touches a Siemens type: a failed load during JIT only names the
    /// missing assembly, not where it was looked for or how to fix that.
    /// </summary>
    public static string? DescribeMissingOpenness()
    {
        if (_selectedInstallPath is not null)
        {
            return null;
        }

        var checkedLocations = new List<string>();
        _selectedInstallPath = FindOpennessInstallPath(checkedLocations);
        return _selectedInstallPath is null ? MissingOpennessMessage(checkedLocations) : null;
    }

    internal static string MissingOpennessMessage(IEnumerable<string> checkedLocations)
        => "TIA Portal V21 Openness assemblies were not found. Checked locations: "
            + string.Join("; ", checkedLocations)
            + @". In the plugin settings set openness_dir (TIA-Openness-Ordner) to the folder PublicAPI\V21\net48 of the TIA Portal installation, "
            + "then restart the Claude app; restarting only the server does not apply the setting. "
            + $"Without the plugin, set the environment variable {TiaPortalV21DirEnvironmentVariable} to that folder.";

    internal static string? SelectFirstCompleteCandidate(
        IEnumerable<string> candidates,
        Func<string, bool> isComplete,
        Action<string>? inspected = null)
    {
        foreach (var candidate in candidates)
        {
            inspected?.Invoke(candidate);
            if (isComplete(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var requestedName = new AssemblyName(args.Name);

        if (requestedName.Name is null ||
            !requestedName.Name.StartsWith("Siemens.Engineering.", StringComparison.Ordinal))
        {
            return null;
        }

        var opennessInstallPath = GetOpennessInstallPath();
        var assemblyPath = Path.Combine(opennessInstallPath, $"{requestedName.Name}.dll");

        if (!File.Exists(assemblyPath))
        {
            return null;
        }

        var loadedAssembly = Assembly.LoadFrom(assemblyPath);

        if (!string.Equals(requestedName.FullName, loadedAssembly.GetName().FullName, StringComparison.Ordinal))
        {
            throw new FileNotFoundException("TIA Portal Openness version does not match the requested assembly.", assemblyPath);
        }

        return loadedAssembly;
    }

    private static string GetOpennessInstallPath()
    {
        var checkedLocations = new List<string>();
        return FindOpennessInstallPath(checkedLocations)
            ?? throw new FileNotFoundException(MissingOpennessMessage(checkedLocations));
    }

    private static string? FindOpennessInstallPath(List<string> checkedLocations)
    {
        var environmentPath = Environment.GetEnvironmentVariable(TiaPortalV21DirEnvironmentVariable);
        var tiaPortalLocation = Environment.GetEnvironmentVariable(TiaPortalLocationEnvironmentVariable);

        // Test switch: lets a live test on a PC with TIA Portal see the "nothing found" path.
        var useRegistry = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && Environment.GetEnvironmentVariable(SkipRegistryEnvironmentVariable) != "1";
        var candidates = CreateCandidatePaths(
            environmentPath,
            tiaPortalLocation,
            useRegistry ? GetRegistryInstallPaths(checkedLocations) : Enumerable.Empty<string?>(),
            StandardOpennessInstallPath,
            useRegistry ? GetRegisteredOpennessAssemblies(checkedLocations) : Enumerable.Empty<string?>());
        return SelectFirstCompleteCandidate(
            candidates,
            ContainsRequiredAssemblies,
            path => checkedLocations.Add(path));
    }

    private static IEnumerable<string?> GetRegisteredOpennessAssemblies(List<string> checkedLocations)
    {
        foreach (var registryView in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            var registryPath = $@"HKLM\{OpennessRegistrySubKey} ({registryView})";
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, registryView);
            using var key = baseKey.OpenSubKey(OpennessRegistrySubKey);

            if (key is null)
            {
                checkedLocations.Add($"{registryPath}: key not found");
                continue;
            }

            if (key.GetValue(OpennessRegistryValueName) is not string assembly || assembly.Trim().Length == 0)
            {
                checkedLocations.Add($"{registryPath}: {OpennessRegistryValueName} not set");
                continue;
            }

            yield return assembly;
        }
    }

    private static IEnumerable<string?> GetRegistryInstallPaths(List<string> checkedLocations)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield break;
        }

        foreach (var registryView in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            var registryPath = $@"HKLM\{TiaPortalV21RegistrySubKey} ({registryView})";
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, registryView);
            using var key = baseKey.OpenSubKey(TiaPortalV21RegistrySubKey);

            if (key is null)
            {
                checkedLocations.Add($"{registryPath}: key not found");
                continue;
            }

            var installPath = key.GetValue("INSTALLPATH") as string;
            if (installPath is null || installPath.Trim().Length == 0)
            {
                checkedLocations.Add($"{registryPath}: INSTALLPATH not set");
                continue;
            }

            yield return installPath;
        }
    }

    private static bool ContainsRequiredAssemblies(string directoryPath)
    {
        return Directory.Exists(directoryPath) &&
            RequiredAssemblies.All(assemblyName => File.Exists(Path.Combine(directoryPath, assemblyName)));
    }
}
