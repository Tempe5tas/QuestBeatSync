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
