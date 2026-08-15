namespace QuestBeatSync.Core.Models;

public enum PlaylistEntryIdentityStatus
{
    HashIdentified,
    MissingHash,
    InvalidHash
}

public sealed record PlaylistEntry
{
    public PlaylistEntry(BeatMapIdentity identity, string? songName = null)
        : this(identity.MapKey, identity.Hash, songName)
    {
    }

    public PlaylistEntry(string? key, string? hash, string? songName)
    {
        Key = Normalize(key);
        SongName = Normalize(songName);

        if (string.IsNullOrWhiteSpace(hash))
        {
            IdentityStatus = PlaylistEntryIdentityStatus.MissingHash;
            return;
        }

        if (!BeatSaverHash.IsValid(hash))
        {
            IdentityStatus = PlaylistEntryIdentityStatus.InvalidHash;
            return;
        }

        Identity = new BeatMapIdentity(hash, Key);
        IdentityStatus = PlaylistEntryIdentityStatus.HashIdentified;
    }

    public string? Key { get; }

    public string? Hash => Identity?.Hash;

    public string? SongName { get; }

    public BeatMapIdentity? Identity { get; }

    public PlaylistEntryIdentityStatus IdentityStatus { get; }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
