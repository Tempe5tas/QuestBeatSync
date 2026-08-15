using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Abstractions;

public interface IBeatSaverClient
{
    Task<BeatSaverLookupResult> LookupAsync(
        BeatSaverLookupRequest request,
        CancellationToken cancellationToken = default);

    Task DownloadZipAsync(
        Uri downloadUri,
        Stream destination,
        CancellationToken cancellationToken = default);
}

