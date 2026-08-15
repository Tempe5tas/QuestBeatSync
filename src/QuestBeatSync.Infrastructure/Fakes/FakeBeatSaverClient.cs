using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Fakes;

public sealed class FakeBeatSaverClient : IBeatSaverClient
{
    public Task<BeatSaverAvailability> GetAvailabilityAsync(
        BeatMapIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Task.FromResult(BeatSaverAvailability.Unknown);
    }
}

