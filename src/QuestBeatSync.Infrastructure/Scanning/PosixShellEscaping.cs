namespace QuestBeatSync.Infrastructure.Scanning;

internal static class PosixShellEscaping
{
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }
}

