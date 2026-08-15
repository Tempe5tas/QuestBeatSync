namespace QuestBeatSync.Infrastructure.Adb;

public interface IAdbProcessRunner
{
    Task<AdbProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record AdbProcessResult(
    bool Started,
    bool TimedOut,
    int? ExitCode,
    string StandardOutput,
    string StandardError);
