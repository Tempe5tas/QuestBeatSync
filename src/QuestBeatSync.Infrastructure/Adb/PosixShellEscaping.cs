namespace QuestBeatSync.Infrastructure.Adb;

internal static class PosixShellEscaping
{
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    public static string SerializeArguments(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(' ', arguments.Select(Quote));
    }
}
