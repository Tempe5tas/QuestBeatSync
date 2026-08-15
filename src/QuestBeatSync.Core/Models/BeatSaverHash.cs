namespace QuestBeatSync.Core.Models;

public static class BeatSaverHash
{
    public const int Sha1Length = 40;

    public static bool IsValid(string? value) => TryNormalize(value, out _);

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length != Sha1Length || candidate.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        normalized = candidate.ToUpperInvariant();
        return true;
    }

    public static string Normalize(string value)
    {
        if (!TryNormalize(value, out var normalized))
        {
            throw new ArgumentException(
                "BeatSaver hash must be exactly 40 hexadecimal characters.",
                nameof(value));
        }

        return normalized;
    }
}
