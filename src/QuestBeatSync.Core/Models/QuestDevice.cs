namespace QuestBeatSync.Core.Models;

public enum QuestConnectionState
{
    Device,
    Unauthorized,
    Offline,
    Unknown
}

public enum QuestTransportKind
{
    Unknown,
    Usb,
    Network
}

public sealed record QuestDevice(
    string Serial,
    QuestConnectionState ConnectionState,
    QuestTransportKind TransportKind,
    string? AndroidModel = null)
{
    public bool IsConnected => ConnectionState == QuestConnectionState.Device;

    public string DisplayName => string.IsNullOrWhiteSpace(AndroidModel) ? Serial : AndroidModel;
}

