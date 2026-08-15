using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Abstractions;

public interface ILocalCache
{
    Task<QuestLibrary?> LoadLibraryAsync(CancellationToken cancellationToken = default);

    Task SaveLibraryAsync(
        QuestLibrary library,
        CancellationToken cancellationToken = default);
}

