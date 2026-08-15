namespace QuestBeatSync.Infrastructure.Adb;

public sealed class AdbQuestTransportOptions
{
    public string? ConfiguredExecutablePath { get; set; }

    public required string AppDataToolsDirectory { get; init; }

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
