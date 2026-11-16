using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Adb;

namespace QuestBeatSync.Infrastructure.Scanning;

public sealed class AdbQuestRemoteFileSystem(IQuestTransport transport) : IQuestRemoteFileSystem
{
    public async Task<bool> DirectoryExistsAsync(
        QuestDevice device,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(remotePath);
        var result = await transport.ExecuteShellAsync(
            device,
            ["sh", "-c", $"test -d {PosixShellEscaping.Quote(remotePath)}"],
            cancellationToken).ConfigureAwait(false);

        ThrowForTransportFailure(result, $"Could not inspect directory {remotePath}.");
        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new QuestRemoteFileSystemException(
                SelectError(result, $"Could not inspect directory {remotePath}."))
        };
    }

    public Task<IReadOnlyList<string>> ListDirectoriesAsync(
        QuestDevice device,
        string remotePath,
        CancellationToken cancellationToken = default) =>
        ListEntriesAsync(device, remotePath, "d", cancellationToken);

    public Task<IReadOnlyList<string>> ListFilesAsync(
        QuestDevice device,
        string remotePath,
        CancellationToken cancellationToken = default) =>
        ListEntriesAsync(device, remotePath, "f", cancellationToken);

    public async Task<string> ReadTextFileAsync(
        QuestDevice device,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(remotePath);
        var result = await transport.ExecuteShellAsync(
            device,
            ["sh", "-c", $"cat {PosixShellEscaping.Quote(remotePath)}"],
            cancellationToken).ConfigureAwait(false);
        ThrowForCommandFailure(result, $"Could not read {remotePath}.");
        return result.StandardOutput;
    }

    public async Task<IReadOnlyList<string>> FindFilesAsync(
        QuestDevice device,
        string remotePath,
        int minDepth,
        int maxDepth,
        string? namePattern = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(remotePath);
        var patternClause = !string.IsNullOrWhiteSpace(namePattern)
            ? $"\\( -name {PosixShellEscaping.Quote(namePattern)} -o -iname {PosixShellEscaping.Quote(namePattern)} \\) "
            : string.Empty;
        var script = $"find {PosixShellEscaping.Quote(remotePath)} -mindepth {minDepth} -maxdepth {maxDepth} -type f {patternClause}-print";
        var result = await transport.ExecuteShellAsync(
            device,
            ["sh", "-c", script],
            cancellationToken).ConfigureAwait(false);
        ThrowForCommandFailure(result, $"Could not find files in {remotePath}.");

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadTextFilesAsync(
        QuestDevice device,
        IReadOnlyList<string> remotePaths,
        CancellationToken cancellationToken = default)
    {
        if (remotePaths.Count == 0) return new Dictionary<string, string>();
        foreach (var path in remotePaths) ValidatePath(path);

        const int batchSize = 32;
        var results = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var batch in remotePaths.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var script = string.Join(" ", batch.Select(path =>
            {
                var quoted = PosixShellEscaping.Quote(path);
                return $"if test -f {quoted}; then printf '%s\\n' {PosixShellEscaping.Quote($"__QBSYNC_FILE_START__|{path}")}; cat {quoted}; printf '\\n%s\\n' {PosixShellEscaping.Quote($"__QBSYNC_FILE_END__|{path}")}; fi;";
            }));

            var result = await transport.ExecuteShellAsync(
                device,
                ["sh", "-c", script],
                cancellationToken).ConfigureAwait(false);
            ThrowForCommandFailure(result, "Could not read remote files.");

            ParseFileBatchOutput(result.StandardOutput, batch, results);
        }

        return results;
    }

    internal static void ParseFileBatchOutput(
        string output,
        IEnumerable<string> expectedPaths,
        Dictionary<string, string> results)
    {
        const string startPrefix = "__QBSYNC_FILE_START__|";
        const string endPrefix = "__QBSYNC_FILE_END__|";

        foreach (var path in expectedPaths)
        {
            var startMarker = $"{startPrefix}{path}";
            var endMarker = $"{endPrefix}{path}";

            var startIdx = output.IndexOf(startMarker, StringComparison.Ordinal);
            if (startIdx < 0) continue;

            var contentStart = startIdx + startMarker.Length;
            if (contentStart < output.Length && output[contentStart] == '\r') contentStart++;
            if (contentStart < output.Length && output[contentStart] == '\n') contentStart++;

            var endIdx = output.IndexOf(endMarker, contentStart, StringComparison.Ordinal);
            if (endIdx < 0) continue;

            var contentEnd = endIdx;
            if (contentEnd > contentStart && output[contentEnd - 1] == '\n')
            {
                contentEnd--;
                if (contentEnd > contentStart && output[contentEnd - 1] == '\r')
                    contentEnd--;
            }

            var content = output[contentStart..contentEnd];
            results[path] = content;
        }
    }

    private async Task<IReadOnlyList<string>> ListEntriesAsync(
        QuestDevice device,
        string remotePath,
        string entryType,
        CancellationToken cancellationToken)
    {
        ValidatePath(remotePath);
        var result = await transport.ExecuteShellAsync(
            device,
            [
                "sh",
                "-c",
                $"find {PosixShellEscaping.Quote(remotePath)} -mindepth 1 -maxdepth 1 -type {entryType} -print"
            ],
            cancellationToken).ConfigureAwait(false);
        ThrowForCommandFailure(result, $"Could not list {remotePath}.");

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void ThrowForCommandFailure(AdbCommandResult result, string fallbackMessage)
    {
        ThrowForTransportFailure(result, fallbackMessage);
        if (result.ExitCode != 0)
        {
            throw new QuestRemoteFileSystemException(SelectError(result, fallbackMessage));
        }
    }

    private static void ThrowForTransportFailure(AdbCommandResult result, string fallbackMessage)
    {
        if (!result.AdbAvailable)
        {
            throw new QuestRemoteFileSystemException("ADB executable is not available.");
        }

        if (result.TimedOut)
        {
            throw new QuestRemoteFileSystemException("ADB read-only filesystem command timed out.");
        }

        if (result.ExitCode is null)
        {
            throw new QuestRemoteFileSystemException(SelectError(result, fallbackMessage));
        }
    }

    private static string SelectError(AdbCommandResult result, string fallbackMessage) =>
        !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError.Trim()
            : fallbackMessage;

    private static void ValidatePath(string remotePath) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
}
