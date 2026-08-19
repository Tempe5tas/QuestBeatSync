using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Abstractions;

public interface IBeatSaberPackageInspector
{
    Task<BeatSaberPackageVersion?> InspectAsync(QuestDevice device, CancellationToken cancellationToken = default);
}

public interface ILocalMapCompatibilityInspector
{
    Task<MapCompatibilityResult> InspectAsync(
        string localMapDirectory,
        BeatSaberPackageVersion? target,
        CancellationToken cancellationToken = default);
}
