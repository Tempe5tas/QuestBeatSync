using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Fakes;

public sealed class FakeBeatSaverClient : IBeatSaverClient
{
    public Task<BeatSaverLookupResult> LookupAsync(
        BeatSaverLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(BeatSaverLookupResult.Unknown(request, "Fake BeatSaver client."));
    }

    public Task DownloadZipAsync(
        Uri downloadUri,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The fake BeatSaver client does not download maps.");
}
