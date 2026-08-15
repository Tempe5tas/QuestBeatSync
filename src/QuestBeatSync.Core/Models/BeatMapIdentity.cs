namespace QuestBeatSync.Core.Models;

public sealed class BeatMapIdentity : IEquatable<BeatMapIdentity>
{
    public BeatMapIdentity(string hash, string? mapKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        Hash = hash.Trim().ToUpperInvariant();
        MapKey = string.IsNullOrWhiteSpace(mapKey) ? null : mapKey.Trim();
    }

    public string Hash { get; }

    public string? MapKey { get; }

    public bool Equals(BeatMapIdentity? other) =>
        other is not null && StringComparer.OrdinalIgnoreCase.Equals(Hash, other.Hash);

    public override bool Equals(object? obj) => Equals(obj as BeatMapIdentity);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Hash);

    public static bool operator ==(BeatMapIdentity? left, BeatMapIdentity? right) =>
        EqualityComparer<BeatMapIdentity>.Default.Equals(left, right);

    public static bool operator !=(BeatMapIdentity? left, BeatMapIdentity? right) => !(left == right);

    public override string ToString() => Hash;
}

