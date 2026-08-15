using QuestBeatSync.App.ViewModels;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Adb;
using QuestBeatSync.Infrastructure.Importing;
using QuestBeatSync.Infrastructure.Fakes;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class MainWindowViewModelTests
{
    [TestMethod]
    public async Task InitializeAsync_DoesNotSelectFirstDeviceWhenMultipleArePresent()
    {
        var devices = new[]
        {
            new QuestDevice("USB123", QuestConnectionState.Device, QuestTransportKind.Usb, "Quest 3"),
            new QuestDevice("192.168.1.100:5555", QuestConnectionState.Device, QuestTransportKind.Network, "Quest 2")
        };
        var options = new AdbQuestTransportOptions { AppDataToolsDirectory = "unused" };
        var settingsStore = new AdbSettingsStore(Path.Combine(Path.GetTempPath(), "qbsync-unused-settings.json"));
        var viewModel = new MainWindowViewModel(
            new StubQuestTransport(devices),
            new StubBeatSaberScanner(),
            new StubPlaylistImporter(),
            new FakeBeatSaverClient(),
            new FakeBeatMapCache(),
            options,
            settingsStore);

        await viewModel.InitializeAsync();

        Assert.IsTrue(viewModel.HasMultipleDevices);
        Assert.IsNull(viewModel.SelectedDevice);
        Assert.AreEqual("Select a device", viewModel.DeviceStatus);
    }

    [TestMethod]
    public async Task InitializeAsync_PublishesReadOnlyScanResultForSingleDevice()
    {
        var device = new QuestDevice("USB123", QuestConnectionState.Device, QuestTransportKind.Usb, "Quest 3");
        var map = new QuestInstalledMap(
            "/maps/song",
            "song",
            true,
            "Song",
            "Mapper",
            QuestMapIdentityStatus.LocalOnly);
        var playlist = new QuestInstalledPlaylist(
            "/playlists/list.bplist",
            "list.bplist",
            "List",
            1,
            QuestPlaylistFormat.Bplist);
        var scanResult = new QuestBeatSaberScanResult(
            true,
            true,
            true,
            true,
            true,
            [map],
            [playlist]);
        var options = new AdbQuestTransportOptions { AppDataToolsDirectory = "unused" };
        var settingsStore = new AdbSettingsStore(Path.Combine(Path.GetTempPath(), "qbsync-unused-settings.json"));
        var viewModel = new MainWindowViewModel(
            new StubQuestTransport([device]),
            new StubBeatSaberScanner(scanResult),
            new StubPlaylistImporter(),
            new FakeBeatSaverClient(),
            new FakeBeatMapCache(),
            options,
            settingsStore);

        await viewModel.InitializeAsync();

        Assert.AreEqual(device, viewModel.SelectedDevice);
        Assert.AreEqual("Beat Saber detected", viewModel.BeatSaberStatus);
        Assert.AreEqual("SongCore detected", viewModel.SongCoreStatus);
        Assert.AreEqual("PlaylistManager detected", viewModel.PlaylistManagerStatus);
        Assert.AreEqual(1, viewModel.SongCount);
        Assert.AreEqual(1, viewModel.PlaylistCount);
        Assert.HasCount(1, viewModel.InstalledMaps);
        Assert.HasCount(1, viewModel.InstalledPlaylists);
    }

    [TestMethod]
    public async Task ImportPlaylistFilesAsync_PublishesMultiplePlaylistPreviewAndAggregateState()
    {
        var first = new Playlist("ACG", "Dana_Iclucia");
        first.Add(new PlaylistEntry("one", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "One"));
        first.Add(new PlaylistEntry("duplicate", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "Duplicate"));
        first.Add(new PlaylistEntry("missing", null, "Missing hash"));
        var second = new Playlist("J-Pop", "Author");
        second.Add(new PlaylistEntry("shared", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "Shared"));
        second.Add(new PlaylistEntry("two", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", "Two"));
        var importResults = new[]
        {
            new PlaylistImportResult("acg.bplist", first, null),
            new PlaylistImportResult("jpop.bplist", second, null),
            new PlaylistImportResult("broken.bplist", null, "Malformed .bplist JSON")
        };
        var options = new AdbQuestTransportOptions { AppDataToolsDirectory = "unused" };
        var settingsStore = new AdbSettingsStore(Path.Combine(Path.GetTempPath(), "qbsync-unused-settings.json"));
        var viewModel = new MainWindowViewModel(
            new StubQuestTransport([]),
            new StubBeatSaberScanner(),
            new StubPlaylistImporter(importResults),
            new FakeBeatSaverClient(),
            new FakeBeatMapCache(),
            options,
            settingsStore);

        await viewModel.ImportPlaylistFilesAsync(["acg.bplist", "jpop.bplist", "broken.bplist"]);

        Assert.HasCount(2, viewModel.ImportedPlaylists);
        Assert.AreSame(first, viewModel.SelectedImportedPlaylist);
        Assert.AreEqual("by Dana_Iclucia", viewModel.SelectedPlaylistAuthorDisplay);
        Assert.AreEqual(5, viewModel.TotalPlaylistReferences);
        Assert.AreEqual(2, viewModel.UniqueRequiredHashes);
        Assert.AreEqual(2, viewModel.DuplicateReferences);
        Assert.HasCount(1, viewModel.PlaylistImportErrors);
    }

    [TestMethod]
    public async Task CompletedEnvironmentScan_CanBeFollowedByAnotherDeviceScan()
    {
        var first = new QuestDevice("FIRST", QuestConnectionState.Device, QuestTransportKind.Usb);
        var second = new QuestDevice("SECOND", QuestConnectionState.Device, QuestTransportKind.Usb);
        var scanner = new CountingBeatSaberScanner();
        var viewModel = CreateViewModel(new StubQuestTransport([first]), scanner);

        await viewModel.InitializeAsync();
        viewModel.SelectedDevice = second;
        await scanner.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(2, scanner.CallCount);
        Assert.AreEqual(second, viewModel.SelectedDevice);
    }

    [TestMethod]
    public async Task RapidDeviceSwitch_CancelsPreviousScanAndKeepsLatestResult()
    {
        var first = new QuestDevice("FIRST", QuestConnectionState.Device, QuestTransportKind.Usb);
        var second = new QuestDevice("SECOND", QuestConnectionState.Device, QuestTransportKind.Usb);
        var scanner = new OverlappingBeatSaberScanner();
        var viewModel = CreateViewModel(new StubQuestTransport([]), scanner);

        viewModel.SelectedDevice = first;
        await scanner.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SelectedDevice = second;
        await Task.WhenAll(
            scanner.FirstCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            scanner.SecondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.AreEqual(second, viewModel.SelectedDevice);
        Assert.IsTrue(viewModel.EnvironmentScanCompleted);
        Assert.AreEqual("Beat Saber detected", viewModel.BeatSaberStatus);
        Assert.IsFalse(viewModel.IsEnvironmentScanning);
    }

    private static MainWindowViewModel CreateViewModel(
        IQuestTransport transport,
        IQuestBeatSaberScanner scanner)
    {
        var options = new AdbQuestTransportOptions { AppDataToolsDirectory = "unused" };
        var settingsStore = new AdbSettingsStore(Path.Combine(Path.GetTempPath(), "qbsync-unused-settings.json"));
        return new MainWindowViewModel(
            transport,
            scanner,
            new StubPlaylistImporter(),
            new FakeBeatSaverClient(),
            new FakeBeatMapCache(),
            options,
            settingsStore);
    }

    private sealed class CountingBeatSaberScanner : IQuestBeatSaberScanner
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public TaskCompletionSource SecondCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<QuestBeatSaberScanResult> ScanAsync(
            QuestDevice device,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _callCount) == 2)
            {
                SecondCall.TrySetResult();
            }

            return Task.FromResult(QuestBeatSaberScanResult.Empty);
        }
    }

    private sealed class OverlappingBeatSaberScanner : IQuestBeatSaberScanner
    {
        private int _callCount;

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<QuestBeatSaberScanResult> ScanAsync(
            QuestDevice device,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                FirstStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    FirstCanceled.TrySetResult();
                    throw;
                }
            }

            SecondCompleted.TrySetResult();
            return new QuestBeatSaberScanResult(
                true,
                true,
                true,
                true,
                true,
                [],
                []);
        }
    }

    private sealed class StubBeatSaberScanner(
        QuestBeatSaberScanResult? result = null) : IQuestBeatSaberScanner
    {
        public Task<QuestBeatSaberScanResult> ScanAsync(
            QuestDevice device,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result ?? QuestBeatSaberScanResult.Empty);
    }

    private sealed class StubPlaylistImporter(
        IReadOnlyList<PlaylistImportResult>? results = null) : ILocalPlaylistImporter
    {
        public Task<IReadOnlyList<PlaylistImportResult>> ImportAsync(
            IEnumerable<string> filePaths,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(results ?? []);
    }

    private sealed class StubQuestTransport(IReadOnlyList<QuestDevice> devices) : IQuestTransport
    {
        public Task<QuestDeviceDiscoveryResult> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(QuestDeviceDiscoveryResult.Successful(devices));

        public Task<AdbCommandResult> ExecuteShellAsync(
            QuestDevice device,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AdbCommandResult> PushAsync(
            QuestDevice device,
            string localPath,
            string remotePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AdbCommandResult> PullAsync(
            QuestDevice device,
            string remotePath,
            string localPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<QuestLibrary> GetLibraryAsync(
            QuestDevice device,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
