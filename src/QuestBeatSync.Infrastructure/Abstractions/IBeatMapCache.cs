using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Abstractions;

public interface IBeatMapCache
{
    Task<bool> IsCachedAsync(string hash, CancellationToken cancellationToken = default);

    Task<BeatMapCacheResult> CacheAsync(
        BeatSaverLookupResult lookupResult,
        CancellationToken cancellationToken = default);
}
