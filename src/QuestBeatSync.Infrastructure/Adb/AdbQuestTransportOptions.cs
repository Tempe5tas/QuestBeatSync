namespace QuestBeatSync.Infrastructure.Adb;

public sealed class AdbQuestTransportOptions
{
    public string? ConfiguredExecutablePath { get; set; }

    public required string AppDataToolsDirectory { get; init; }

    public TimeSpan ShellCommandTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan FileTransferTimeout { get; init; } = TimeSpan.FromMinutes(10);
}
