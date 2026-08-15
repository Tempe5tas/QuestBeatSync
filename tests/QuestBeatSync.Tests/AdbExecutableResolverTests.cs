using QuestBeatSync.Infrastructure.Adb;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class AdbExecutableResolverTests
{
    private static readonly string ExecutableName = OperatingSystem.IsWindows() ? "adb.exe" : "adb";

    [TestMethod]
    public void Resolve_PrefersConfiguredPath()
    {
        var configured = Path.Combine("configured", ExecutableName);
        var appData = Path.Combine("app-data-tools", ExecutableName);
        var pathCandidate = Path.Combine("path-tools", ExecutableName);
        var existingFiles = new HashSet<string>([configured, appData, pathCandidate]);
        var resolver = new AdbExecutableResolver(existingFiles.Contains, () => "path-tools");
        var options = new AdbQuestTransportOptions
        {
            ConfiguredExecutablePath = configured,
            AppDataToolsDirectory = "app-data-tools"
        };

        Assert.AreEqual(configured, resolver.Resolve(options));
    }

    [TestMethod]
    public void Resolve_UsesAppDataToolsBeforePath()
    {
        var appData = Path.Combine("app-data-tools", ExecutableName);
        var pathCandidate = Path.Combine("path-tools", ExecutableName);
        var existingFiles = new HashSet<string>([appData, pathCandidate]);
        var resolver = new AdbExecutableResolver(existingFiles.Contains, () => "path-tools");
        var options = new AdbQuestTransportOptions
        {
            ConfiguredExecutablePath = "missing-adb",
            AppDataToolsDirectory = "app-data-tools"
        };

        Assert.AreEqual(appData, resolver.Resolve(options));
    }

    [TestMethod]
    public void Resolve_FallsBackToPath()
    {
        var pathCandidate = Path.Combine("path-tools", ExecutableName);
        var resolver = new AdbExecutableResolver(
            candidate => candidate == pathCandidate,
            () => "path-tools");
        var options = new AdbQuestTransportOptions
        {
            AppDataToolsDirectory = "missing-app-data-tools"
        };

        Assert.AreEqual(pathCandidate, resolver.Resolve(options));
    }
}
