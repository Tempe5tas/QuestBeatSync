namespace QuestBeatSync.Infrastructure.BeatSaver;

public sealed class BeatSaverRequestException(string message, Exception? innerException = null)
    : Exception(message, innerException);

