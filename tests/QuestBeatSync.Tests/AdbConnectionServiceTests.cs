using QuestBeatSync.Core.Models;
using QuestBeatSync.App.ViewModels;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Adb;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class AdbConnectionServiceTests
{
    private string _root = null!;
    [TestInitialize] public void Start() { _root = Path.Combine(Path.GetTempPath(), "qbsync-connect-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Stop() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public async Task ConnectUsesExactArgumentListAndRecognizesAlreadyConnected()
    {
        var runner = new RecordingRunner(new(true, false, 0, "already connected to 192.168.1.20:5555", ""));
        var service = await CreateReadyAsync(runner);
        var result = await service.ConnectAsync(new("192.168.1.20", 5555));
        CollectionAssert.AreEqual(new[] { "connect", "192.168.1.20:5555" }, runner.Calls.Last().ToArray());
        Assert.AreEqual(AdbConnectionOutcome.AlreadyConnected, result.Outcome);
    }

    [TestMethod]
    public async Task InvalidPortIsRejectedBeforeProcessExecution()
    {
        var runner = new RecordingRunner(Success()); var service = await CreateReadyAsync(runner); var before = runner.Calls.Count;
        var result = await service.ConnectAsync(new("192.168.1.20", 0));
        Assert.AreEqual(AdbConnectionOutcome.InvalidEndpoint, result.Outcome); Assert.AreEqual(before, runner.Calls.Count);
    }

    [TestMethod]
    public async Task UsbTcpipUsesTypedDeviceArgumentsAndRefusesNetworkOrOffline()
    {
        var runner = new RecordingRunner(Success()); var service = await CreateReadyAsync(runner);
        await service.EnableWirelessAdbAsync(new("USB_SERIAL", QuestConnectionState.Device, QuestTransportKind.Usb), 5555);
        CollectionAssert.AreEqual(new[] { "-s", "USB_SERIAL", "tcpip", "5555" }, runner.Calls.Last().ToArray());
        var count = runner.Calls.Count;
        Assert.AreEqual(AdbConnectionOutcome.Refused, (await service.EnableWirelessAdbAsync(new("1.2.3.4:5555", QuestConnectionState.Device, QuestTransportKind.Network))).Outcome);
        Assert.AreEqual(AdbConnectionOutcome.Refused, (await service.EnableWirelessAdbAsync(new("USB", QuestConnectionState.Offline, QuestTransportKind.Usb))).Outcome);
        Assert.AreEqual(count, runner.Calls.Count);
    }

    [TestMethod]
    public async Task TimeoutAndUnrecognizedOutputFailWithoutCreatingDevices()
    {
        var timeoutRunner = new RecordingRunner(new(true, true, null, "", "")); var timeoutService = await CreateReadyAsync(timeoutRunner);
        Assert.AreEqual(AdbConnectionOutcome.TimedOut, (await timeoutService.ConnectAsync(new("quest.local", 5555))).Outcome);
        var badRunner = new RecordingRunner(new(true, false, 0, "daemon started", "")); var badService = await CreateReadyAsync(badRunner);
        Assert.AreEqual(AdbConnectionOutcome.Failed, (await badService.ConnectAsync(new("quest.local", 5555))).Outcome);
    }

    [TestMethod]
    public async Task SuccessfulDashboardConnectRefreshesNormalDeviceDiscovery()
    {
        var runner = new RecordingRunner(Success());
        var adb = Path.Combine(_root, OperatingSystem.IsWindows() ? "adb.exe" : "adb"); File.WriteAllText(adb, "fake");
        var options = new AdbQuestTransportOptions { AppDataToolsDirectory = Path.Combine(_root, "tools") };
        var store = new AdbSettingsStore(Path.Combine(_root, "settings.json")); store.SaveConfiguredPath(adb);
        var manager = new AdbEnvironmentManager(options, store, new AdbExecutableResolver(File.Exists, () => null), runner, new NoDistribution(), new NoPackage());
        await manager.DiscoverAsync();
        var transport = new DiscoveryTransport();
        var dashboard = new DashboardViewModel(transport, new NoScan(), new LibraryViewModel(), _ => Task.CompletedTask, manager, new SuccessfulConnection());
        dashboard.WirelessHost = "192.168.1.20";

        await dashboard.ConnectCommand.ExecuteAsync();

        Assert.AreEqual(1, transport.DiscoveryCount);
    }

    private async Task<AdbConnectionService> CreateReadyAsync(RecordingRunner runner)
    {
        var adb = Path.Combine(_root, OperatingSystem.IsWindows() ? "adb.exe" : "adb"); File.WriteAllText(adb, "fake");
        var options = new AdbQuestTransportOptions { AppDataToolsDirectory = Path.Combine(_root, "tools") };
        var store = new AdbSettingsStore(Path.Combine(_root, "settings.json")); store.SaveConfiguredPath(adb);
        var manager = new AdbEnvironmentManager(options, store, new AdbExecutableResolver(File.Exists, () => null), runner, new NoDistribution(), new NoPackage());
        await manager.DiscoverAsync(); return new(manager, runner, options);
    }
    private static AdbProcessResult Success() => new(true, false, 0, "Android Debug Bridge version 1.0.41", "");
    private sealed class RecordingRunner(AdbProcessResult commandResult) : IAdbProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<AdbProcessResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) { Calls.Add(arguments.ToArray()); return Task.FromResult(arguments.SequenceEqual(["version"]) ? Success() : commandResult); }
    }
    private sealed class NoDistribution : IAdbDistributionProvider { public Task<AdbDistribution> ResolveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException(); }
    private sealed class NoPackage : IAdbPackageClient { public Task DownloadAsync(Uri uri, string destinationPath, CancellationToken cancellationToken) => throw new NotSupportedException(); public Task ExtractAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken) => throw new NotSupportedException(); }
    private sealed class SuccessfulConnection : IAdbConnectionService
    {
        public Task<AdbConnectionResult> ConnectAsync(AdbNetworkEndpoint endpoint, CancellationToken cancellationToken = default) => Task.FromResult(new AdbConnectionResult(AdbConnectionOutcome.Connected));
        public Task<AdbConnectionResult> DisconnectAsync(AdbNetworkEndpoint endpoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdbConnectionResult> EnableWirelessAdbAsync(QuestDevice device, int port = 5555, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class DiscoveryTransport : IQuestTransport
    {
        public int DiscoveryCount { get; private set; }
        public Task<QuestDeviceDiscoveryResult> GetDevicesAsync(CancellationToken cancellationToken = default) { DiscoveryCount++; return Task.FromResult(QuestDeviceDiscoveryResult.Successful([])); }
        public Task<QuestBeatSync.Infrastructure.Abstractions.AdbCommandResult> ExecuteShellAsync(QuestDevice device, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QuestBeatSync.Infrastructure.Abstractions.AdbCommandResult> PushAsync(QuestDevice device, string localPath, string remotePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<QuestBeatSync.Infrastructure.Abstractions.AdbCommandResult> PullAsync(QuestDevice device, string remotePath, string localPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class NoScan : IQuestBeatSaberScanner { public Task<QuestBeatSaberScanResult> ScanAsync(QuestDevice device, CancellationToken cancellationToken = default) => throw new NotSupportedException(); }
}
