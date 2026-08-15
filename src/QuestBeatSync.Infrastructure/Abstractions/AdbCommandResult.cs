namespace QuestBeatSync.Infrastructure.Abstractions;

public sealed record AdbCommandResult(
    bool AdbAvailable,
    bool TimedOut,
    int? ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool IsSuccess => AdbAvailable && !TimedOut && ExitCode == 0;

    public static AdbCommandResult NotAvailable() =>
        new(false, false, null, string.Empty, "ADB executable was not found.");
}
