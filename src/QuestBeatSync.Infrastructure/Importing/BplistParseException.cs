namespace QuestBeatSync.Infrastructure.Importing;

public sealed class BplistParseException(string message, Exception? innerException = null)
    : Exception(message, innerException);

