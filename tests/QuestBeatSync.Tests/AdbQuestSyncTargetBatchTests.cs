using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;
using QuestBeatSync.Infrastructure.Execution;

namespace QuestBeatSync.Tests;

[TestClass]
public sealed class AdbQuestSyncTargetBatchTests
{
    private static readonly QuestDevice Device = new("SERIAL", QuestConnectionState.Device, QuestTransportKind.Usb);

    [TestMethod]
    public async Task StagingAndFinalValidation_EachUseOneShellCommandAndBindExactHashes()
    {
        var first = Identity('A'); var second = Identity('B'); var execution = Guid.NewGuid();
        var transport = new SentinelTransport(arguments =>
        {
            var script = arguments[2]; var prefix = script.Contains(".qbsync-", StringComparison.Ordinal) ? "staging" : "final";
            return Ok($"__QBSYNC_MAP__|{second.Hash}|MISSING_INFO\n__QBSYNC_MAP__|{first.Hash}|OK\n", prefix);
        });
        var target = new AdbQuestSyncTarget(transport, QuestBeatSaberPaths.Default);
        var staging = new[] { new MapWritePathBinding(first, QuestExecutionPaths.MapStaging(QuestBeatSaberPaths.Default, first, execution)), new MapWritePathBinding(second, QuestExecutionPaths.MapStaging(QuestBeatSaberPaths.Default, second, execution)) };
        var finals = new[] { new MapWritePathBinding(first, QuestExecutionPaths.MapFinal(QuestBeatSaberPaths.Default, first)), new MapWritePathBinding(second, QuestExecutionPaths.MapFinal(QuestBeatSaberPaths.Default, second)) };

        var stagedResult = await target.ValidateStagedMapsAsync(Device, staging);
        var finalResult = await target.VerifyFinalMapsAsync(Device, finals);

        Assert.AreEqual(2, transport.ShellCount);
        Assert.AreEqual(MapStructureValidationStatus.Valid, stagedResult.Single(result => result.Identity == first).Status);
        Assert.AreEqual(MapStructureValidationStatus.MissingInfoDat, stagedResult.Single(result => result.Identity == second).Status);
        Assert.AreEqual(MapStructureValidationStatus.Valid, finalResult.Single(result => result.Identity == first).Status);
        Assert.IsTrue(transport.Scripts.All(script => script.Contains("find", StringComparison.Ordinal) && !script.Contains("eval", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CompactPromotion_UsesOneRemoteTransactionAndParsesSentinel()
    {
        var identity = Identity('C'); var transport = new SentinelTransport(_ => Ok($"__QBSYNC_PROMOTE__|{identity.Hash}|PROMOTED\n", "promote"));
        var target = new AdbQuestSyncTarget(transport, QuestBeatSaberPaths.Default);
        var result = await target.PromoteStagingCompactAsync(Device, new(identity,
            QuestExecutionPaths.MapStaging(QuestBeatSaberPaths.Default, identity, Guid.NewGuid()),
            QuestExecutionPaths.MapFinal(QuestBeatSaberPaths.Default, identity)));
        Assert.AreEqual(MapPromotionStatus.Promoted, result.Status); Assert.AreEqual(1, transport.ShellCount);
        StringAssert.Contains(transport.Scripts.Single(), "toybox mv -nT"); StringAssert.Contains(transport.Scripts.Single(), "AMBIGUOUS");
    }

    [TestMethod]
    public async Task CompactPromotion_IdentityMismatchRefusesBeforeRemoteCommand()
    {
        var first = Identity('D'); var second = Identity('E'); var transport = new SentinelTransport(_ => throw new AssertFailedException("No remote call expected."));
        var target = new AdbQuestSyncTarget(transport, QuestBeatSaberPaths.Default);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => target.PromoteStagingCompactAsync(Device, new(first,
            QuestExecutionPaths.MapStaging(QuestBeatSaberPaths.Default, first, Guid.NewGuid()),
            QuestExecutionPaths.MapFinal(QuestBeatSaberPaths.Default, second))));
        Assert.AreEqual(0, transport.ShellCount);
    }

    private static BeatMapIdentity Identity(char value) => new(new string(value, 40));
    private static AdbCommandResult Ok(string output, string _) => new(true, false, 0, output, "");

    private sealed class SentinelTransport(Func<IReadOnlyList<string>, AdbCommandResult> response) : IQuestTransport
    {
        public int ShellCount { get; private set; } public List<string> Scripts { get; } = [];
        public Task<QuestDeviceDiscoveryResult> GetDevicesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdbCommandResult> ExecuteShellAsync(QuestDevice device, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) { ShellCount++; Scripts.Add(arguments[2]); return Task.FromResult(response(arguments)); }
        public Task<AdbCommandResult> PushAsync(QuestDevice device, string localPath, string remotePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdbCommandResult> PullAsync(QuestDevice device, string remotePath, string localPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
