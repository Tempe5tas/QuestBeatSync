using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Abstractions;

public interface IQuestTransport
{
    Task<QuestDeviceDiscoveryResult> GetDevicesAsync(CancellationToken cancellationToken = default);

    Task<AdbCommandResult> ExecuteShellAsync(
        QuestDevice device,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> PushAsync(
        QuestDevice device,
        string localPath,
        string remotePath,
        CancellationToken cancellationToken = default);

    Task<AdbCommandResult> PullAsync(
        QuestDevice device,
        string remotePath,
        string localPath,
        CancellationToken cancellationToken = default);
}
