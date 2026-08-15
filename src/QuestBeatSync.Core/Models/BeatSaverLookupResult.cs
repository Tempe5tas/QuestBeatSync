namespace QuestBeatSync.Core.Models;

public sealed record BeatSaverLookupRequest(string? Hash, string? Key)
{
    public static BeatSaverLookupRequest FromEntry(PlaylistEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new BeatSaverLookupRequest(entry.Hash, entry.Key);
    }
}

public sealed record BeatSaverLookupResult(
    BeatSaverAvailability Availability,
    string? RequestedHash,
    string? RequestedKey,
    string? ResolvedHash,
    string? ResolvedKey,
    Uri? DownloadUri,
    bool ExactHashMatched,
    string? Message = null)
{
    public bool CanDownload =>
        Availability == BeatSaverAvailability.Online &&
        ResolvedHash is not null &&
        DownloadUri is not null;

    public static BeatSaverLookupResult Unknown(
        BeatSaverLookupRequest request,
        string message) =>
        new(
            BeatSaverAvailability.Unknown,
            Normalize(request.Hash),
            Normalize(request.Key),
            null,
            null,
            null,
            false,
            message);

    public static BeatSaverLookupResult Unavailable(
        BeatSaverLookupRequest request,
        string message) =>
        new(
            BeatSaverAvailability.Unavailable,
            Normalize(request.Hash),
            Normalize(request.Key),
            null,
            null,
            null,
            false,
            message);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}

