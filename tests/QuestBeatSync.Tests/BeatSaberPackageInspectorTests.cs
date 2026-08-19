using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Scanning;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class BeatSaberPackageInspectorTests
{
    [TestMethod]
    public async Task InspectAsync_ParsesRealStyleVersionNameAndCodeWithoutViewModelAdb()
    {
        var transport = new RecordingTransport();
        var device = new QuestDevice("QUEST", QuestConnectionState.Device, QuestTransportKind.Usb);

        var result = await new AdbBeatSaberPackageInspector(transport).InspectAsync(device);

        Assert.IsNotNull(result);
        Assert.AreEqual("1.35.0_8016709773", result.VersionName);
        Assert.AreEqual(1130L, result.VersionCode);
        Assert.AreEqual(new Version(1, 35, 0), result.ParsedVersion);
        CollectionAssert.AreEqual(
            new[] { "dumpsys", "package", "com.beatgames.beatsaber" },
            transport.LastArguments!.ToArray());
    }

    private sealed class RecordingTransport : IQuestTransport
    {
        public IReadOnlyList<string>? LastArguments { get; private set; }
        public Task<QuestDeviceDiscoveryResult> GetDevicesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdbCommandResult> ExecuteShellAsync(QuestDevice device, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            LastArguments = arguments;
            return Task.FromResult(new AdbCommandResult(true, false, 0, "  versionCode=1130 minSdk=32\n  versionName=1.35.0_8016709773\n", ""));
        }
        public Task<AdbCommandResult> PushAsync(QuestDevice device, string localPath, string remotePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdbCommandResult> PullAsync(QuestDevice device, string remotePath, string localPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
