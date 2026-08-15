using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Adb;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class AdbQuestTransportTests
{
    [TestMethod]
    public async Task GetDevicesAsync_ReportsDevicesCommandTimeout()
    {
        var runner = new StubProcessRunner((_, _) =>
            new AdbProcessResult(true, true, null, string.Empty, string.Empty));
        var transport = CreateTransport(runner);

        var result = await transport.GetDevicesAsync();

        Assert.AreEqual(QuestDeviceDiscoveryStatus.TimedOut, result.Status);
        Assert.IsEmpty(result.Devices);
    }

    [TestMethod]
    public async Task GetDevicesAsync_ReadsModelOnlyForConnectedDevices()
    {
        var runner = new StubProcessRunner((_, arguments) =>
        {
            if (arguments.SequenceEqual(["devices"]))
            {
                return new AdbProcessResult(
                    true,
                    false,
                    0,
                    "List of devices attached\n192.168.1.100:5555 device\nABC123 unauthorized\n",
                    string.Empty);
            }

            CollectionAssert.AreEqual(
                new[] { "-s", "192.168.1.100:5555", "shell", "getprop", "ro.product.model" },
                arguments.ToArray());
            return new AdbProcessResult(true, false, 0, "Quest 3\n", string.Empty);
        });
        var transport = CreateTransport(runner);

        var result = await transport.GetDevicesAsync();

        Assert.AreEqual(QuestDeviceDiscoveryStatus.Success, result.Status);
        Assert.HasCount(2, result.Devices);
        Assert.AreEqual("Quest 3", result.Devices[0].AndroidModel);
        Assert.IsNull(result.Devices[1].AndroidModel);
        Assert.AreEqual(2, runner.InvocationCount);
    }

    private static AdbQuestTransport CreateTransport(IAdbProcessRunner processRunner)
    {
        const string executable = "test-adb";
        var resolver = new AdbExecutableResolver(candidate => candidate == executable, () => null);
        var options = new AdbQuestTransportOptions
        {
            ConfiguredExecutablePath = executable,
            AppDataToolsDirectory = "unused",
            CommandTimeout = TimeSpan.FromMilliseconds(50)
        };
        return new AdbQuestTransport(options, resolver, processRunner);
    }

    private sealed class StubProcessRunner(
        Func<string, IReadOnlyList<string>, AdbProcessResult> resultFactory) : IAdbProcessRunner
    {
        public int InvocationCount { get; private set; }

        public Task<AdbProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult(resultFactory(executablePath, arguments));
        }
    }
}
