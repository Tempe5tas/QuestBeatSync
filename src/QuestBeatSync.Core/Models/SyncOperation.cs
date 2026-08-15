namespace QuestBeatSync.Core.Models;

public enum SyncOperationKind
{
    DownloadMap,
    UploadMap,
    ImportPlaylist,
    KeepExisting,
    PreserveQuestOnly,
    SkipUnavailable,
    SkipUnknown
}

public sealed record SyncOperation(
    SyncOperationKind Kind,
    string Description,
    BeatMapIdentity? MapIdentity = null,
    string? PlaylistName = null);
