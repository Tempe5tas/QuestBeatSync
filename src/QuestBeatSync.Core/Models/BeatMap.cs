namespace QuestBeatSync.Core.Models;

public sealed record BeatMap(
    BeatMapIdentity Identity,
    string SongName,
    string? SongAuthor = null,
    BeatSaverAvailability Availability = BeatSaverAvailability.Unknown);

