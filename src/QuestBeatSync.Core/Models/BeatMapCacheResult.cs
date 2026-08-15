namespace QuestBeatSync.Core.Models;

public enum BeatMapCacheOutcome
{
    Cached,
    AlreadyCached,
    Failed
}

public sealed record BeatMapCacheResult(
    BeatMapCacheOutcome Outcome,
    string? CachePath = null,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Outcome is BeatMapCacheOutcome.Cached or BeatMapCacheOutcome.AlreadyCached;
}
