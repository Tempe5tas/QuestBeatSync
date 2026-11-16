using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Scanning;

namespace QuestBeatSync.Tests.Fixtures;

internal sealed class FixtureQuestRemoteFileSystem : IQuestRemoteFileSystem
{
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _childDirectories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _childFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fileContents = new(StringComparer.Ordinal);

    public void AddDirectory(string remotePath)
    {
        _directories.Add(remotePath);
        _childDirectories.TryAdd(remotePath, []);
        _childFiles.TryAdd(remotePath, []);
    }

    public string AddSubdirectory(string parentPath, string folderName)
    {
        AddDirectory(parentPath);
        var path = Join(parentPath, folderName);
        AddDirectory(path);
        _childDirectories[parentPath].Add(path);
        return path;
    }

    public string AddFile(string parentPath, string filename, string content)
    {
        AddDirectory(parentPath);
        var path = Join(parentPath, filename);
        _childFiles[parentPath].Add(path);
        _fileContents[path] = content;
        return path;
    }

    public Task<bool> DirectoryExistsAsync(
        QuestDevice device,
        string remotePath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_directories.Contains(remotePath));

    public Task<IReadOnlyList<string>> ListDirectoriesAsync(
        QuestDevice device,
        string remotePath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(
            _childDirectories.TryGetValue(remotePath, out var entries) ? entries.ToArray() : []);

    public Task<IReadOnlyList<string>> ListFilesAsync(
        QuestDevice device,
        string remotePath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(
            _childFiles.TryGetValue(remotePath, out var entries) ? entries.ToArray() : []);

    public Task<string> ReadTextFileAsync(
        QuestDevice device,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        if (!_fileContents.TryGetValue(remotePath, out var content))
        {
            throw new QuestRemoteFileSystemException($"Fixture file not found: {remotePath}");
        }

        return Task.FromResult(content);
    }

    public Task<IReadOnlyList<string>> FindFilesAsync(
        QuestDevice device,
        string remotePath,
        int minDepth,
        int maxDepth,
        string? namePattern = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<string>();
        CollectFiles(remotePath, 1, minDepth, maxDepth, namePattern, results);
        return Task.FromResult<IReadOnlyList<string>>(results.Order(StringComparer.Ordinal).ToArray());
    }

    public Task<IReadOnlyDictionary<string, string>> ReadTextFilesAsync(
        QuestDevice device,
        IReadOnlyList<string> remotePaths,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in remotePaths)
        {
            if (_fileContents.TryGetValue(path, out var content))
            {
                results[path] = content;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, string>>(results);
    }

    private void CollectFiles(
        string currentDir,
        int currentDepth,
        int minDepth,
        int maxDepth,
        string? namePattern,
        List<string> results)
    {
        if (currentDepth > maxDepth) return;

        if (currentDepth >= minDepth && _childFiles.TryGetValue(currentDir, out var files))
        {
            foreach (var file in files)
            {
                var fileName = GetRemoteName(file);
                if (string.IsNullOrWhiteSpace(namePattern) ||
                    string.Equals(fileName, namePattern, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(file);
                }
            }
        }

        if (currentDepth < maxDepth && _childDirectories.TryGetValue(currentDir, out var subdirs))
        {
            foreach (var subdir in subdirs)
            {
                CollectFiles(subdir, currentDepth + 1, minDepth, maxDepth, namePattern, results);
            }
        }
    }

    private static string GetRemoteName(string remotePath)
    {
        var normalized = remotePath.TrimEnd('/');
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex < 0 ? normalized : normalized[(separatorIndex + 1)..];
    }

    private static string Join(string parentPath, string name) =>
        $"{parentPath.TrimEnd('/')}/{name}";
}
