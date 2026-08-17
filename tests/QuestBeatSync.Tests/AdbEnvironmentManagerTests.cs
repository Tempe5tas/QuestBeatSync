using QuestBeatSync.Infrastructure.Adb;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class AdbEnvironmentManagerTests
{
    private string _root = null!;
    [TestInitialize] public void Start() { _root = Path.Combine(Path.GetTempPath(), "qbsync-adb-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Stop() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public async Task ValidUserSelectionWinsOverManagedAndSystem()
    {
        var user = Touch("user-adb"); var managed = Touch(Path.Combine("tools", "platform-tools", "1", AdbName)); var system = Touch(Path.Combine("path", AdbName));
        var fixture = Create(system, path => Success(path)); fixture.Store.SaveConfiguredPath(user);
        var result = await fixture.Manager.DiscoverAsync();
        Assert.AreEqual(AdbEnvironmentSource.UserSelected, result.Source); Assert.AreEqual(Path.GetFullPath(user), result.ExecutablePath);
    }

    [TestMethod]
    public async Task InvalidUserFallsBackToManaged()
    {
        var invalid = Touch("wrong.exe"); var managed = Touch(Path.Combine("tools", "platform-tools", "1", AdbName));
        var fixture = Create(null, path => path == invalid ? Failed() : Success(path)); fixture.Store.SaveConfiguredPath(invalid);
        var result = await fixture.Manager.DiscoverAsync();
        Assert.AreEqual(AdbEnvironmentSource.QBSyncManaged, result.Source); Assert.AreEqual(Path.GetFullPath(managed), result.ExecutablePath);
    }

    [TestMethod]
    public async Task ValidPathAdbIsSystemAndNoCandidateIsNotFound()
    {
        var system = Touch(Path.Combine("path", AdbName)); var fixture = Create(system, Success);
        Assert.AreEqual(AdbEnvironmentSource.System, (await fixture.Manager.DiscoverAsync()).Source);
        var empty = Create(null, Success); Assert.AreEqual(AdbEnvironmentState.NotFound, (await empty.Manager.DiscoverAsync()).State);
    }

    [TestMethod]
    public async Task ExistingFileWhoseVersionFailsIsNotReady()
    {
        var system = Touch(Path.Combine("path", AdbName)); var fixture = Create(system, _ => Failed());
        Assert.AreNotEqual(AdbEnvironmentState.Ready, (await fixture.Manager.DiscoverAsync()).State);
    }

    [TestMethod]
    public async Task InvalidManualSelectionPreservesWorkingSelection()
    {
        var system = Touch(Path.Combine("path", AdbName)); var invalid = Touch("invalid");
        var fixture = Create(system, path => path == invalid ? Failed() : Success(path)); await fixture.Manager.DiscoverAsync();
        var result = await fixture.Manager.SelectUserExecutableAsync(invalid);
        Assert.IsFalse(result.IsReady); Assert.AreEqual(Path.GetFullPath(system), fixture.Manager.Current.ExecutablePath); Assert.AreEqual(Path.GetFullPath(system), fixture.Options.ConfiguredExecutablePath);
    }

    [TestMethod]
    public async Task ManagedInstallPublishesOnlyAfterValidation()
    {
        var fixture = Create(null, Success, new FakePackageClient());
        var result = await fixture.Manager.InstallManagedAsync();
        Assert.IsTrue(result.IsReady); Assert.AreEqual(AdbEnvironmentSource.QBSyncManaged, result.Source); Assert.IsTrue(File.Exists(result.ExecutablePath));
    }

    [TestMethod]
    public async Task FailedOrCanceledInstallPreservesExistingManagedAdb()
    {
        Touch(Path.Combine("tools", "platform-tools", "old", AdbName));
        var fixture = Create(null, Success, new FakePackageClient(fail: true)); var old = await fixture.Manager.DiscoverAsync();
        var failed = await fixture.Manager.InstallManagedAsync(); Assert.AreEqual(old.ExecutablePath, failed.ExecutablePath);

        var canceledFixture = Create(null, Success, new FakePackageClient(cancel: true)); await canceledFixture.Manager.DiscoverAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => canceledFixture.Manager.InstallManagedAsync(new CancellationTokenSource().Token));
        Assert.IsTrue(canceledFixture.Manager.Current.IsReady);
    }

    [TestMethod]
    public async Task ExtractionOrExtractedAdbValidationFailureDoesNotActivatePartialInstall()
    {
        var extraction = Create(null, Success, new FakePackageClient(failExtract: true));
        Assert.AreEqual(AdbEnvironmentState.DownloadFailed, (await extraction.Manager.InstallManagedAsync()).State);
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "tools", "platform-tools")));

        var validationRoot = Path.Combine(_root, "validation"); Directory.CreateDirectory(validationRoot);
        var options = new AdbQuestTransportOptions { AppDataToolsDirectory = Path.Combine(validationRoot, "tools") };
        var store = new AdbSettingsStore(Path.Combine(validationRoot, "settings.json"));
        var manager = new AdbEnvironmentManager(options, store, new AdbExecutableResolver(File.Exists, () => null),
            new FakeRunner(path => path.Contains(".install-", StringComparison.Ordinal) ? Failed() : Success(path)), new FakeDistribution(), new FakePackageClient());
        Assert.AreEqual(AdbEnvironmentState.DownloadFailed, (await manager.InstallManagedAsync()).State);
        Assert.IsFalse(Directory.Exists(Path.Combine(validationRoot, "tools", "platform-tools")));
    }

    private Fixture Create(string? systemPath, Func<string, AdbProcessResult> result, IAdbPackageClient? package = null)
    {
        var options = new AdbQuestTransportOptions { AppDataToolsDirectory = Path.Combine(_root, "tools") };
        var store = new AdbSettingsStore(Path.Combine(_root, "settings.json"));
        var pathValue = systemPath is null ? null : Path.GetDirectoryName(systemPath);
        var resolver = new AdbExecutableResolver(File.Exists, () => pathValue);
        var manager = new AdbEnvironmentManager(options, store, resolver, new FakeRunner(result), new FakeDistribution(), package ?? new FakePackageClient());
        return new(manager, store, options);
    }

    private string Touch(string relative) { var path = Path.Combine(_root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fake"); return path; }
    private static string AdbName => OperatingSystem.IsWindows() ? "adb.exe" : "adb";
    private static AdbProcessResult Success(string _) => new(true, false, 0, "Android Debug Bridge version 1.0.41\nVersion 36.0.2", "");
    private static AdbProcessResult Failed() => new(true, false, 1, "not adb", "bad executable");
    private sealed record Fixture(AdbEnvironmentManager Manager, AdbSettingsStore Store, AdbQuestTransportOptions Options);
    private sealed class FakeRunner(Func<string, AdbProcessResult> result) : IAdbProcessRunner { public Task<AdbProcessResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(result(executablePath)); }
    private sealed class FakeDistribution : IAdbDistributionProvider { public Task<AdbDistribution> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AdbDistribution(new Uri("https://example.invalid/adb.zip"), "test", "adb.zip")); }
    private sealed class FakePackageClient(bool fail = false, bool cancel = false, bool failExtract = false) : IAdbPackageClient
    {
        public Task DownloadAsync(Uri uri, string destinationPath, CancellationToken cancellationToken) { if (cancel) throw new OperationCanceledException(cancellationToken); if (fail) throw new HttpRequestException("network failed"); File.WriteAllText(destinationPath, "zip"); return Task.CompletedTask; }
        public Task ExtractAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken) { if (failExtract) throw new InvalidDataException("bad archive"); var dir = Path.Combine(destinationDirectory, "platform-tools"); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, AdbName), "fake"); return Task.CompletedTask; }
    }
}
