using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Fakes;

public sealed class FakeQuestTransport : IQuestTransport
{
    public Task<IReadOnlyList<QuestDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<QuestDevice>>([]);

    public Task<QuestLibrary> GetLibraryAsync(
        QuestDevice device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Task.FromResult(new QuestLibrary());
    }
}

