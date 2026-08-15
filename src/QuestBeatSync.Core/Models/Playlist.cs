namespace QuestBeatSync.Core.Models;

public sealed class Playlist
{
    private readonly List<PlaylistEntry> _entries = [];

    public Playlist(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    public string Name { get; }

    public IReadOnlyList<PlaylistEntry> Entries => _entries;

    public void Add(PlaylistEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.Add(entry);
    }
}

