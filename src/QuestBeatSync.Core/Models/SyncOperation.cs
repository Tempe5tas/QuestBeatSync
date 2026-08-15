namespace QuestBeatSync.Core.Models;

public enum SyncOperationKind
{
    CopyMapToQuest,
    CopyPlaylistToQuest,
    BackupMap,
    BackupPlaylist
}

public sealed record SyncOperation(
    SyncOperationKind Kind,
    string Description,
    BeatMapIdentity? MapIdentity = null);

