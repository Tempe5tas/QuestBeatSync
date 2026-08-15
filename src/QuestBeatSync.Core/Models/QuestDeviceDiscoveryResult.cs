namespace QuestBeatSync.Core.Models;

public enum QuestDeviceDiscoveryStatus
{
    Success,
    AdbNotAvailable,
    TimedOut,
    Error
}

public sealed record QuestDeviceDiscoveryResult(
    QuestDeviceDiscoveryStatus Status,
    IReadOnlyList<QuestDevice> Devices,
    string? ErrorMessage = null)
{
    public static QuestDeviceDiscoveryResult Successful(IEnumerable<QuestDevice> devices) =>
        new(QuestDeviceDiscoveryStatus.Success, devices.ToArray());

    public static QuestDeviceDiscoveryResult Failed(
        QuestDeviceDiscoveryStatus status,
        string errorMessage) =>
        new(status, [], errorMessage);
}
