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
    string? Message = null);
