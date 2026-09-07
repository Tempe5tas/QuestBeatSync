using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Backup;

public static class ArchivePathSafety
{
    public static string NormalizeRelativePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('\0')) throw new InvalidDataException("Archive paths cannot contain NUL.");
        var normalized = value;
        if (normalized.StartsWith('/') ||
            (normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/'))
            throw new InvalidDataException("Archive paths must be relative.");
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException("Archive path traversal or empty segments are not allowed.");
        return string.Join('/', segments);
    }

    public static string LogicalKey(ArchiveLogicalRoot root, string relativePath) =>
        $"{root}:{NormalizeRelativePath(relativePath)}";
}
