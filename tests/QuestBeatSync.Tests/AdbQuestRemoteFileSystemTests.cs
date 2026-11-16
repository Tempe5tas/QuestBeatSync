using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Scanning;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class AdbQuestRemoteFileSystemTests
{
    private static readonly QuestDevice Device =
        new("QUEST123", QuestConnectionState.Device, QuestTransportKind.Usb);

    [TestMethod]
    public async Task FilesystemOperations_UseOnlyEscapedReadOnlyShellCommands()
    {
        var scripts = new List<string>();
        var transport = new StubTransport(arguments =>
        {
            scripts.Add(arguments[2]);
            var output = arguments[2].StartsWith("find", StringComparison.Ordinal)
                ? "/remote/item\n"
                : arguments[2].StartsWith("cat", StringComparison.Ordinal) ? "{}" : string.Empty;
            return new AdbCommandResult(true, false, 0, output, string.Empty);
        });
        var fileSystem = new AdbQuestRemoteFileSystem(transport);
        const string path = "/remote/Mapper's Song";

        Assert.IsTrue(await fileSystem.DirectoryExistsAsync(Device, path));
        await fileSystem.ListDirectoriesAsync(Device, path);
        await fileSystem.ListFilesAsync(Device, path);
        await fileSystem.ReadTextFileAsync(Device, path);

        Assert.HasCount(4, scripts);
        Assert.IsTrue(scripts.All(script =>
            script.StartsWith("test -d", StringComparison.Ordinal) ||
            script.StartsWith("find", StringComparison.Ordinal) ||
            script.StartsWith("cat", StringComparison.Ordinal)));
        Assert.IsTrue(scripts.All(script => script.Contains("'\"'\"'", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DirectoryExistsAsync_DistinguishesMissingDirectoryFromShellFailure()
    {
        var missingFileSystem = new AdbQuestRemoteFileSystem(
            new StubTransport(_ => new AdbCommandResult(true, false, 1, string.Empty, string.Empty)));
        Assert.IsFalse(await missingFileSystem.DirectoryExistsAsync(Device, "/missing"));

        var failedFileSystem = new AdbQuestRemoteFileSystem(
            new StubTransport(_ => new AdbCommandResult(true, false, 127, string.Empty, "find: not found")));
        try
        {
            await failedFileSystem.DirectoryExistsAsync(Device, "/error");
            Assert.Fail("A shell execution failure must not be treated as a missing directory.");
        }
        catch (QuestRemoteFileSystemException exception)
        {
            StringAssert.Contains(exception.Message, "find: not found");
        }
    }

    [TestMethod]
    public async Task FindFilesAsync_UsesEscapedArgumentsAndReturnsMatches()
    {
        string? capturedScript = null;
        var transport = new StubTransport(arguments =>
        {
            capturedScript = arguments[2];
            return new AdbCommandResult(true, false, 0, "/remote/a/Info.dat\n/remote/b/Info.dat\n", string.Empty);
        });
        var fileSystem = new AdbQuestRemoteFileSystem(transport);

        var matches = await fileSystem.FindFilesAsync(Device, "/remote/CustomLevels", 2, 2, "Info.dat");

        Assert.AreEqual(2, matches.Count);
        Assert.AreEqual("/remote/a/Info.dat", matches[0]);
        Assert.AreEqual("/remote/b/Info.dat", matches[1]);
        Assert.IsNotNull(capturedScript);
        StringAssert.Contains(capturedScript, "find '/remote/CustomLevels' -mindepth 2 -maxdepth 2 -type f");
        StringAssert.Contains(capturedScript, "-name 'Info.dat'");
    }

    [TestMethod]
    public async Task ReadTextFilesAsync_BatchesFilesAndParsesSentinels()
    {
        string? capturedScript = null;
        var transport = new StubTransport(arguments =>
        {
            capturedScript = arguments[2];
            var output =
                "__QBSYNC_FILE_START__|/remote/a/Info.dat\n{\"title\":\"Song A\"}\n__QBSYNC_FILE_END__|/remote/a/Info.dat\n" +
                "__QBSYNC_FILE_START__|/remote/b/Info.dat\n{\"title\":\"Song B\"}\n__QBSYNC_FILE_END__|/remote/b/Info.dat\n";
            return new AdbCommandResult(true, false, 0, output, string.Empty);
        });
        var fileSystem = new AdbQuestRemoteFileSystem(transport);

        var results = await fileSystem.ReadTextFilesAsync(Device, ["/remote/a/Info.dat", "/remote/b/Info.dat"]);

        Assert.AreEqual(2, results.Count);
        Assert.AreEqual("{\"title\":\"Song A\"}", results["/remote/a/Info.dat"]);
        Assert.AreEqual("{\"title\":\"Song B\"}", results["/remote/b/Info.dat"]);
        Assert.IsNotNull(capturedScript);
        StringAssert.Contains(capturedScript, "__QBSYNC_FILE_START__|/remote/a/Info.dat");
        StringAssert.Contains(capturedScript, "__QBSYNC_FILE_START__|/remote/b/Info.dat");
    }

    private sealed class StubTransport(
        Func<IReadOnlyList<string>, AdbCommandResult> shellResultFactory) : IQuestTransport
    {
        public Task<QuestDeviceDiscoveryResult> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AdbCommandResult> ExecuteShellAsync(
            QuestDevice device,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(shellResultFactory(arguments));

        public Task<AdbCommandResult> PushAsync(
            QuestDevice device,
            string localPath,
            string remotePath,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("The read-only filesystem must not call adb push.");

        public Task<AdbCommandResult> PullAsync(
            QuestDevice device,
            string remotePath,
            string localPath,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("The read-only filesystem must not call adb pull.");

    }
}
