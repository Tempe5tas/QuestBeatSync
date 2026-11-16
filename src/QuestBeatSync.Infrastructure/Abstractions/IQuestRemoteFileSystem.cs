using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Abstractions;

public interface IQuestRemoteFileSystem
{
    Task<bool> DirectoryExistsAsync(
        QuestDevice device,
        string remotePath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListDirectoriesAsync(
        QuestDevice device,
        string remotePath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListFilesAsync(
        QuestDevice device,
        string remotePath,
        CancellationToken cancellationToken = default);

    Task<string> ReadTextFileAsync(
        QuestDevice device,
        string remotePath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> FindFilesAsync(
        QuestDevice device,
        string remotePath,
        int minDepth,
        int maxDepth,
        string? namePattern = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> ReadTextFilesAsync(
        QuestDevice device,
        IReadOnlyList<string> remotePaths,
        CancellationToken cancellationToken = default);
}

