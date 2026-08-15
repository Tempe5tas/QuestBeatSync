using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Fakes;

public sealed class FakeLocalCache : ILocalCache
{
    private QuestLibrary? _library;

    public Task<QuestLibrary?> LoadLibraryAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_library);

    public Task SaveLibraryAsync(
        QuestLibrary library,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
        return Task.CompletedTask;
    }
}
