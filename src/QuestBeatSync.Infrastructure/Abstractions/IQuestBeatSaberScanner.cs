using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Abstractions;

public interface IQuestBeatSaberScanner
{
    Task<QuestBeatSaberScanResult> ScanAsync(
        QuestDevice device,
        CancellationToken cancellationToken = default);
}

