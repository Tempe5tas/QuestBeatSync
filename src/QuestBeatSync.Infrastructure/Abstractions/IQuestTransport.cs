using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Abstractions;

public interface IQuestTransport
{
    Task<IReadOnlyList<QuestDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);

    Task<QuestLibrary> GetLibraryAsync(
        QuestDevice device,
        CancellationToken cancellationToken = default);
}

