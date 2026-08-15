using QuestBeatSync.Infrastructure.Importing;

namespace QuestBeatSync.Infrastructure.Abstractions;

public interface ILocalPlaylistImporter
{
    Task<IReadOnlyList<PlaylistImportResult>> ImportAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default);
}

