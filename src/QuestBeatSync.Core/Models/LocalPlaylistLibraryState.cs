namespace QuestBeatSync.Core.Models;

public sealed class LocalPlaylistLibraryState
{
    public LocalPlaylistLibraryState(IEnumerable<Playlist> playlists)
    {
        ArgumentNullException.ThrowIfNull(playlists);
        Playlists = playlists.ToArray();

        var entries = Playlists.SelectMany(playlist => playlist.Entries).ToArray();
        TotalPlaylistReferences = entries.Length;
        UniqueRequiredHashes = entries
            .Where(entry => entry.Hash is not null)
            .Select(entry => entry.Hash!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        DuplicateReferences = entries.Count(entry => entry.Hash is not null) - UniqueRequiredHashes;
    }

    public IReadOnlyList<Playlist> Playlists { get; }

    public int TotalPlaylistReferences { get; }

    public int UniqueRequiredHashes { get; }

    public int DuplicateReferences { get; }
}
