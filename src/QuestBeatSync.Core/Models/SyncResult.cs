namespace QuestBeatSync.Core.Models;

public enum SyncOperationStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Canceled
}

public enum SyncRunStatus
{
    Running,
    Completed,
    CompletedWithFailures,
    Canceled,
    Refused
}

public sealed record SyncOperationResult(
    int OperationIndex,
    SyncOperation Operation,
    SyncOperationStatus Status,
    string? Message = null);

public sealed record SyncResult(
    Guid ExecutionId,
    SyncRunStatus Status,
    string DeviceSerial,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<SyncOperationResult> Operations,
    IReadOnlyList<string> DiagnosticWarnings,
    string? Message = null);

public sealed record QuestWritePreparationResult(bool IsReady, string? Message = null)
{
    public static QuestWritePreparationResult Ready { get; } = new(true);

    public static QuestWritePreparationResult Refused(string message) => new(false, message);
}

public sealed record SyncProgress(
    string Phase,
    int Current,
    int Total,
    string Message,
    SyncOperation? Operation = null);
