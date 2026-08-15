using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Fakes;

public sealed class FakeBeatMapCache : IBeatMapCache
{
    public Task<bool> IsCachedAsync(string hash, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<BeatMapCacheResult> CacheAsync(
        BeatSaverLookupResult lookup,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new BeatMapCacheResult(
            BeatMapCacheOutcome.Failed,
            ErrorMessage: "Fake cache does not download maps."));
}
