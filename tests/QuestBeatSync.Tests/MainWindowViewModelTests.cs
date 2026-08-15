using QuestBeatSync.App.ViewModels;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Adb;

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
        var viewModel = new MainWindowViewModel(new StubQuestTransport(devices), options, settingsStore);

        await viewModel.InitializeAsync();

        Assert.IsTrue(viewModel.HasMultipleDevices);
        Assert.IsNull(viewModel.SelectedDevice);
        Assert.AreEqual("Select a device", viewModel.DeviceStatus);
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
