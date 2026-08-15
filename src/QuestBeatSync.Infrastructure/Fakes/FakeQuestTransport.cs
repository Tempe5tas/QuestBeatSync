using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Fakes;

public sealed class FakeQuestTransport : IQuestTransport
{
    public Task<QuestDeviceDiscoveryResult> GetDevicesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(QuestDeviceDiscoveryResult.Successful([]));

    public Task<AdbCommandResult> ExecuteShellAsync(
        QuestDevice device,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AdbCommandResult(true, false, 0, string.Empty, string.Empty));

    public Task<AdbCommandResult> PushAsync(
        QuestDevice device,
        string localPath,
        string remotePath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AdbCommandResult(true, false, 0, string.Empty, string.Empty));

    public Task<AdbCommandResult> PullAsync(
        QuestDevice device,
        string remotePath,
        string localPath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AdbCommandResult(true, false, 0, string.Empty, string.Empty));

    public Task<QuestLibrary> GetLibraryAsync(
        QuestDevice device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Task.FromResult(new QuestLibrary());
    }
}
