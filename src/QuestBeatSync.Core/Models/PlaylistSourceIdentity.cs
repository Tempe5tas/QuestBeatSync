namespace QuestBeatSync.Core.Models;

public sealed record PlaylistSourceIdentity
{
    public PlaylistSourceIdentity(string canonicalPath, string contentSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        if (!IsSha256(contentSha256))
        {
            throw new ArgumentException("Playlist content SHA256 must be exactly 64 hexadecimal characters.", nameof(contentSha256));
        }

        CanonicalPath = Path.GetFullPath(canonicalPath);
        ContentSha256 = contentSha256.ToUpperInvariant();
    }

    public string CanonicalPath { get; }

    public string ContentSha256 { get; }

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
