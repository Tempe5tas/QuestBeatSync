using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Importing;

public sealed record PlaylistImportResult(
    string FilePath,
    Playlist? Playlist,
    string? ErrorMessage)
{
    public bool IsSuccess => Playlist is not null && ErrorMessage is null;
}

