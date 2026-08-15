using System.Text.Json;

namespace QuestBeatSync.Infrastructure.Adb;

public sealed class AdbSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public AdbSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = settingsPath;
    }

    public string? LoadConfiguredPath()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return null;
            }

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AdbSettings>(json)?.AdbExecutablePath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void SaveConfiguredPath(string? configuredPath)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var settings = new AdbSettings
        {
            AdbExecutablePath = string.IsNullOrWhiteSpace(configuredPath) ? null : configuredPath.Trim()
        };

        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, SerializerOptions));
    }

    private sealed class AdbSettings
    {
        public string? AdbExecutablePath { get; init; }
    }
}
