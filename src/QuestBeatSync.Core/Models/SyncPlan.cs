namespace QuestBeatSync.Core.Models;

public sealed class SyncPlan
{
    private readonly List<SyncOperation> _operations = [];

    public SyncPlan(int playlistReferenceCount = 0, int uniqueMapCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(playlistReferenceCount);
        ArgumentOutOfRangeException.ThrowIfNegative(uniqueMapCount);
        PlaylistReferenceCount = playlistReferenceCount;
        UniqueMapCount = uniqueMapCount;
    }

    public IReadOnlyList<SyncOperation> Operations => _operations;

    public int OperationCount => _operations.Count;

    public int PlaylistReferenceCount { get; }

    public int UniqueMapCount { get; }

    public int Count(SyncOperationKind kind) => _operations.Count(operation => operation.Kind == kind);

    public int AlreadyInstalledCount => Count(SyncOperationKind.KeepExisting);

    public int DownloadRequiredCount => Count(SyncOperationKind.DownloadMap);

    public int UploadRequiredCount => Count(SyncOperationKind.UploadMap);

    public int UnavailableCount => Count(SyncOperationKind.SkipUnavailable);

    public int UnknownCount => Count(SyncOperationKind.SkipUnknown);

    public int QuestOnlyPreservedCount => Count(SyncOperationKind.PreserveQuestOnly);

    public int DeletionCount => 0;

    public void Add(SyncOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _operations.Add(operation);
    }
}
