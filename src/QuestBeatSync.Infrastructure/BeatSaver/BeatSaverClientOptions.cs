namespace QuestBeatSync.Infrastructure.BeatSaver;

public sealed class BeatSaverClientOptions
{
    public Uri BaseUri { get; init; } = new("https://api.beatsaver.com/");

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public int MaxRateLimitRetries { get; init; } = 3;

    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(10);
}

