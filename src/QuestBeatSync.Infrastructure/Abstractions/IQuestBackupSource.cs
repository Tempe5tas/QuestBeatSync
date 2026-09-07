using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Abstractions;

public sealed record QuestBackupSourceFile(ArchiveLogicalRoot LogicalRoot, string RelativePath, string RemotePath);

public interface IQuestBackupSource
{
    Task<IReadOnlyList<QuestBackupSourceFile>> DiscoverAsync(QuestDevice device, CancellationToken cancellationToken = default);
    Task PullAsync(QuestDevice device, QuestBackupSourceFile source, string safeLocalPath, CancellationToken cancellationToken = default);
}
