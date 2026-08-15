using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Adb;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class AdbDevicesParserTests
{
    [TestMethod]
    public void Parse_RecognizesNetworkDeviceAndUnauthorizedUsbDevice()
    {
        const string output = """
            List of devices attached
            192.168.1.100:5555 device
            ABC123 unauthorized
            """;

        var devices = AdbDevicesParser.Parse(output);

        Assert.HasCount(2, devices);
        Assert.AreEqual("192.168.1.100:5555", devices[0].Serial);
        Assert.AreEqual(QuestConnectionState.Device, devices[0].ConnectionState);
        Assert.AreEqual(QuestTransportKind.Network, devices[0].TransportKind);
        Assert.AreEqual("ABC123", devices[1].Serial);
        Assert.AreEqual(QuestConnectionState.Unauthorized, devices[1].ConnectionState);
        Assert.AreEqual(QuestTransportKind.Usb, devices[1].TransportKind);
    }

    [TestMethod]
    public void Parse_RecognizesOfflineAndUnknownStates()
    {
        const string output = """
            List of devices attached
            OFFLINE123 offline
            MYSTERY recovery
            """;

        var devices = AdbDevicesParser.Parse(output);

        Assert.AreEqual(QuestConnectionState.Offline, devices[0].ConnectionState);
        Assert.AreEqual(QuestConnectionState.Unknown, devices[1].ConnectionState);
    }

    [TestMethod]
    public void Parse_IgnoresHeaderAndDaemonMessages()
    {
        const string output = """
            * daemon not running; starting now at tcp:5037
            * daemon started successfully
            List of devices attached

            """;

        Assert.IsEmpty(AdbDevicesParser.Parse(output));
    }
}
