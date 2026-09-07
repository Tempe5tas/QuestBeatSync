using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Backup;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class AdbQuestBackupSourceTests
{
    private static readonly QuestDevice Device = new("USB_SERIAL", QuestConnectionState.Device, QuestTransportKind.Usb);

    [TestMethod]
    public async Task Discover_EnumeratesApprovedRootsAndExcludesOnlyExactOwnedStaging()
    {
        var paths = QuestBeatSaberPaths.Default;
        var transport = new RecordingTransport(command =>
        {
            var script = command[2];
            if (script.Contains(paths.CustomLevels, StringComparison.Ordinal))
                return Success($"{paths.CustomLevels}/Legacy/Info.dat\0{paths.CustomLevels}/contains-qbsync/song.ogg\0{paths.CustomLevels}/.qbsync-{new string('A', 40)}-{Guid.Empty:N}/Info.dat\0");
            if (script.Contains(paths.Playlists, StringComparison.Ordinal)) return Success($"{paths.Playlists}/unknown.bin\0");
            if (script.Contains("Covers", StringComparison.Ordinal)) return Success($"{paths.PlaylistManager}/Covers/sub/cover.png\0");
            if (script.Contains("CachedSongData", StringComparison.Ordinal)) return new(true, false, 1, "", "");
            return Success("");
        });
        var files = await new AdbQuestBackupSource(transport, paths).DiscoverAsync(Device);
        CollectionAssert.Contains(files.Select(file => file.RelativePath).ToArray(), "Legacy/Info.dat");
        CollectionAssert.Contains(files.Select(file => file.RelativePath).ToArray(), "contains-qbsync/song.ogg");
        Assert.IsFalse(files.Any(file => file.RelativePath.StartsWith(".qbsync-", StringComparison.Ordinal)));
        Assert.AreEqual(4, transport.ShellCommands.Count);
        Assert.IsTrue(transport.ShellCommands.All(command => command[0] == "sh" && command[1] == "-c" && command[2].Contains("-print0", StringComparison.Ordinal) || command[2].Contains("printf", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Pull_UsesOnlyPullBoundaryAndNeverQuestMutation()
    {
        var transport = new RecordingTransport(_ => Success(""));
        var source = new AdbQuestBackupSource(transport, QuestBeatSaberPaths.Default);
        var local = Path.Combine(Path.GetTempPath(), "qbsync-safe-" + Guid.NewGuid().ToString("N"));
        try
        {
            await source.PullAsync(Device, new(ArchiveLogicalRoot.Playlists, "x", "/remote/x"), local);
            Assert.AreEqual(1, transport.PullCount); Assert.AreEqual(0, transport.PushCount);
            Assert.IsTrue(transport.ShellCommands.All(args => !args.Any(arg => arg.Contains("rm ", StringComparison.Ordinal) || arg.Contains("mv ", StringComparison.Ordinal) || arg.Contains("mkdir", StringComparison.Ordinal))));
        }
        finally { if (File.Exists(local)) File.Delete(local); }
    }

    [TestMethod]
    public async Task Discover_MissingCoversAndCachedSongDataAreNotFatal()
    {
        var transport = new RecordingTransport(command => command[2].Contains("CachedSongData", StringComparison.Ordinal)
            ? new(true, false, 1, "", "missing") : Success(""));
        var files = await new AdbQuestBackupSource(transport, QuestBeatSaberPaths.Default).DiscoverAsync(Device);
        Assert.AreEqual(0, files.Count);
    }

    [TestMethod]
    public void BackupViewModel_DoesNotOwnQuestTransportBoundary()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "QuestBeatSync.App", "ViewModels", "BackupViewModel.cs"));
        Assert.IsFalse(source.Contains("IQuestTransport", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("PullAsync", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("ExecuteShellAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BackupProductionCode_ContainsNoPushOrRemoteMutationCalls()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "src", "QuestBeatSync.Infrastructure", "Backup");
        var source = string.Join('\n', Directory.GetFiles(directory, "*.cs").Select(File.ReadAllText));
        Assert.IsFalse(source.Contains("PushAsync(", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("rm -", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains(" mv ", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("mkdir ", StringComparison.Ordinal));
    }

    private static AdbCommandResult Success(string output) => new(true, false, 0, output, "");
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuestBeatSync.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class RecordingTransport(Func<IReadOnlyList<string>, AdbCommandResult> shell) : IQuestTransport
    {
        public List<IReadOnlyList<string>> ShellCommands { get; } = []; public int PullCount { get; private set; } public int PushCount { get; private set; }
        public Task<QuestDeviceDiscoveryResult> GetDevicesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdbCommandResult> ExecuteShellAsync(QuestDevice device, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) { ShellCommands.Add(arguments); return Task.FromResult(shell(arguments)); }
        public Task<AdbCommandResult> PushAsync(QuestDevice device, string localPath, string remotePath, CancellationToken cancellationToken = default) { PushCount++; throw new AssertFailedException("Backup must not push."); }
        public async Task<AdbCommandResult> PullAsync(QuestDevice device, string remotePath, string localPath, CancellationToken cancellationToken = default) { PullCount++; await File.WriteAllTextAsync(localPath, "bytes", cancellationToken); return Success("pulled"); }
    }
}
