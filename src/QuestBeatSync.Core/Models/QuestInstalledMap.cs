namespace QuestBeatSync.Core.Models;

public enum QuestMapIdentityStatus
{
    Unknown,
    LocalOnly,
    HashIdentified
}

public sealed record QuestInstalledMap(
    string RemotePath,
    string FolderName,
    bool InfoDatExists,
    string? SongTitle,
    string? Mapper,
    QuestMapIdentityStatus IdentityStatus,
    BeatMapIdentity? Identity = null,
    string? Warning = null);

