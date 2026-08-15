namespace QuestBeatSync.Core.Models;

public sealed class Playlist
{
    private readonly List<PlaylistEntry> _entries = [];

    public Playlist(
        string name,
        string? author = null,
        string? description = null,
        string? image = null,
        string? syncUrl = null,
        string? sourcePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        Author = Normalize(author);
        Description = Normalize(description);
        Image = Normalize(image);
        SyncUrl = Normalize(syncUrl);
        SourcePath = Normalize(sourcePath);
    }

    public string Name { get; }

    public string? Author { get; }

    public string? Description { get; }

    public string? Image { get; }

    public string? SyncUrl { get; }

    public string? SourcePath { get; }

    public IReadOnlyList<PlaylistEntry> Entries => _entries;

    public int EntryCount => _entries.Count;

    public int UniqueHashCount => _entries
        .Where(entry => entry.Hash is not null)
        .Select(entry => entry.Hash!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public int DuplicateReferenceCount =>
        _entries.Count(entry => entry.Hash is not null) - UniqueHashCount;

    public int MissingHashCount =>
        _entries.Count(entry => entry.IdentityStatus == PlaylistEntryIdentityStatus.MissingHash);

    public void Add(PlaylistEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.Add(entry);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
