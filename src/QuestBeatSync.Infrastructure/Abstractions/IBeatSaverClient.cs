using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Abstractions;

public interface IBeatSaverClient
{
    Task<BeatSaverAvailability> GetAvailabilityAsync(
        BeatMapIdentity identity,
        CancellationToken cancellationToken = default);
}

