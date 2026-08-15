namespace QuestBeatSync.Core.Models;

public sealed record PreparedPlaylistSource
{
    public PreparedPlaylistSource(
        string originalCanonicalPath,
        string snapshotPath,
        string contentSha256)
    {
        var identity = new PlaylistSourceIdentity(originalCanonicalPath, contentSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        OriginalCanonicalPath = identity.CanonicalPath;
        SnapshotPath = Path.GetFullPath(snapshotPath);
        ContentSha256 = identity.ContentSha256;
    }

    public string OriginalCanonicalPath { get; }

    public string SnapshotPath { get; }

    public string ContentSha256 { get; }
}
