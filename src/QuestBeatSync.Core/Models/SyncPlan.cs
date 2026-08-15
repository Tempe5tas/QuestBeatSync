namespace QuestBeatSync.Core.Models;

public sealed class SyncPlan
{
    private readonly List<SyncOperation> _operations = [];

    public IReadOnlyList<SyncOperation> Operations => _operations;

    public int OperationCount => _operations.Count;

    public void Add(SyncOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _operations.Add(operation);
    }
}

