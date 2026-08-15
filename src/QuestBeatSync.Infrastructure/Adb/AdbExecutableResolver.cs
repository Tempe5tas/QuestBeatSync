namespace QuestBeatSync.Infrastructure.Adb;

public sealed class AdbExecutableResolver
{
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string?> _pathProvider;

    public AdbExecutableResolver()
        : this(File.Exists, () => Environment.GetEnvironmentVariable("PATH"))
    {
    }

    public AdbExecutableResolver(Func<string, bool> fileExists, Func<string?> pathProvider)
    {
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        _pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
    }

    public string? Resolve(AdbQuestTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configured = ResolveConfiguredPath(options.ConfiguredExecutablePath);
        if (configured is not null)
        {
            return configured;
        }

        foreach (var executableName in GetExecutableNames())
        {
            var appDataCandidate = Path.Combine(options.AppDataToolsDirectory, executableName);
            if (_fileExists(appDataCandidate))
            {
                return appDataCandidate;
            }
        }

        var path = _pathProvider();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var executableName in GetExecutableNames())
            {
                var pathCandidate = Path.Combine(directory, executableName);
                if (_fileExists(pathCandidate))
                {
                    return pathCandidate;
                }
            }
        }

        return null;
    }

    private string? ResolveConfiguredPath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        var trimmedPath = configuredPath.Trim().Trim('"');
        if (_fileExists(trimmedPath))
        {
            return trimmedPath;
        }

        foreach (var executableName in GetExecutableNames())
        {
            var candidate = Path.Combine(trimmedPath, executableName);
            if (_fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string[] GetExecutableNames() =>
        OperatingSystem.IsWindows() ? ["adb.exe", "adb"] : ["adb", "adb.exe"];
}
