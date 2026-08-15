namespace QuestBeatSync.Core.Models;

public sealed class SyncExecutionPlan
{
    public SyncExecutionPlan(
        SyncPlan plan,
        QuestScanBinding target,
        IEnumerable<PlaylistSourceIdentity> playlistSources,
        IReadOnlyDictionary<string, BeatSaverLookupResult>? exactLookups = null)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        var sourceGroups = (playlistSources ?? throw new ArgumentNullException(nameof(playlistSources)))
            .GroupBy(source => source.CanonicalPath, SourcePathComparer)
            .ToArray();
        var conflict = sourceGroups.FirstOrDefault(group =>
            group.Select(source => source.ContentSha256).Distinct(StringComparer.Ordinal).Skip(1).Any());
        if (conflict is not null)
        {
            throw new ArgumentException(
                $"Playlist source '{conflict.Key}' has conflicting content SHA256 identities.",
                nameof(playlistSources));
        }

        PlaylistSources = sourceGroups.Select(group => group.First()).ToArray();
        ExactLookups = (exactLookups ?? new Dictionary<string, BeatSaverLookupResult>())
            .Where(pair => BeatSaverHash.IsValid(pair.Key))
            .ToDictionary(pair => BeatSaverHash.Normalize(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        var sourcesByPath = PlaylistSources.ToDictionary(source => source.CanonicalPath, SourcePathComparer);
        foreach (var operation in Plan.Operations.Where(operation => operation.Kind == SyncOperationKind.ImportPlaylist))
        {
            if (operation.PlaylistSource is null ||
                !sourcesByPath.TryGetValue(operation.PlaylistSource.CanonicalPath, out var source) ||
                !string.Equals(source.ContentSha256, operation.PlaylistSource.ContentSha256, StringComparison.Ordinal))
            {
                throw new ArgumentException("Every ImportPlaylist operation must carry a matching canonical source identity.", nameof(plan));
            }
        }
    }

    public SyncPlan Plan { get; }

    public QuestScanBinding Target { get; }

    public IReadOnlyList<PlaylistSourceIdentity> PlaylistSources { get; }

    public IReadOnlyDictionary<string, BeatSaverLookupResult> ExactLookups { get; }

    public static StringComparer SourcePathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
