using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Adb;

public static class AdbDevicesParser
{
    public static IReadOnlyList<QuestDevice> Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var devices = new List<QuestDevice>();
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("List of devices attached", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith('*'))
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 2)
            {
                continue;
            }

            var serial = fields[0];
            var connectionState = ParseConnectionState(fields[1]);
            devices.Add(new QuestDevice(serial, connectionState, ClassifyTransport(serial)));
        }

        return devices;
    }

    private static QuestConnectionState ParseConnectionState(string value) =>
        value.ToLowerInvariant() switch
        {
            "device" => QuestConnectionState.Device,
            "unauthorized" => QuestConnectionState.Unauthorized,
            "offline" => QuestConnectionState.Offline,
            _ => QuestConnectionState.Unknown
        };

    private static QuestTransportKind ClassifyTransport(string serial)
    {
        if (serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
        {
            return QuestTransportKind.Unknown;
        }

        var lastColon = serial.LastIndexOf(':');
        if (lastColon > 0 &&
            lastColon < serial.Length - 1 &&
            int.TryParse(serial[(lastColon + 1)..].TrimEnd(']'), out _))
        {
            return QuestTransportKind.Network;
        }

        return QuestTransportKind.Usb;
    }
}
