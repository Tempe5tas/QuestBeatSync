namespace QuestBeatSync.Core.Models;

public sealed record PlaylistEntry(BeatMapIdentity Identity, string? SongName = null);

