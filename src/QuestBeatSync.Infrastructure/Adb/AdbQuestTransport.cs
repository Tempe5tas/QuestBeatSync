using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Adb;

public sealed class AdbQuestTransport : IQuestTransport
{
    private readonly AdbQuestTransportOptions _options;
    private readonly AdbExecutableResolver _executableResolver;
    private readonly IAdbProcessRunner _processRunner;

    public AdbQuestTransport(
        AdbQuestTransportOptions options,
        AdbExecutableResolver? executableResolver = null,
        IAdbProcessRunner? processRunner = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _executableResolver = executableResolver ?? new AdbExecutableResolver();
        _processRunner = processRunner ?? new SystemAdbProcessRunner();
    }

    public async Task<QuestDeviceDiscoveryResult> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var executablePath = _executableResolver.Resolve(_options);
        if (executablePath is null)
        {
            return QuestDeviceDiscoveryResult.Failed(
                QuestDeviceDiscoveryStatus.AdbNotAvailable,
                "ADB executable was not found.");
        }

        var result = await _processRunner.RunAsync(
            executablePath,
            ["devices"],
            _options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
        {
            return QuestDeviceDiscoveryResult.Failed(
                QuestDeviceDiscoveryStatus.TimedOut,
                "The adb devices command timed out.");
        }

        if (!result.Started || result.ExitCode != 0)
        {
            return QuestDeviceDiscoveryResult.Failed(
                QuestDeviceDiscoveryStatus.Error,
                SelectErrorMessage(result, "The adb devices command failed."));
        }

        var parsedDevices = AdbDevicesParser.Parse(result.StandardOutput);
        var devices = new List<QuestDevice>(parsedDevices.Count);

        foreach (var device in parsedDevices)
        {
            devices.Add(device.IsConnected
                ? await TryReadModelAsync(device, cancellationToken).ConfigureAwait(false)
                : device);
        }

        return QuestDeviceDiscoveryResult.Successful(devices);
    }

    public Task<AdbCommandResult> ExecuteShellAsync(
        QuestDevice device,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return RunDeviceCommandAsync(device, ["shell", .. arguments], cancellationToken);
    }

    public Task<AdbCommandResult> PushAsync(
        QuestDevice device,
        string localPath,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        return RunDeviceCommandAsync(device, ["push", localPath, remotePath], cancellationToken);
    }

    public Task<AdbCommandResult> PullAsync(
        QuestDevice device,
        string remotePath,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        return RunDeviceCommandAsync(device, ["pull", remotePath, localPath], cancellationToken);
    }

    public Task<QuestLibrary> GetLibraryAsync(
        QuestDevice device,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Beat Saber library scanning is not implemented in Phase 1.");

    private async Task<QuestDevice> TryReadModelAsync(
        QuestDevice device,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteShellAsync(
            device,
            ["getprop", "ro.product.model"],
            cancellationToken).ConfigureAwait(false);

        var model = result.IsSuccess ? result.StandardOutput.Trim() : null;
        return string.IsNullOrWhiteSpace(model) ? device : device with { AndroidModel = model };
    }

    private async Task<AdbCommandResult> RunDeviceCommandAsync(
        QuestDevice device,
        IReadOnlyList<string> commandArguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        var executablePath = _executableResolver.Resolve(_options);
        if (executablePath is null)
        {
            return AdbCommandResult.NotAvailable();
        }

        var arguments = new List<string>(commandArguments.Count + 2)
        {
            "-s",
            device.Serial
        };
        arguments.AddRange(commandArguments);

        var result = await _processRunner.RunAsync(
            executablePath,
            arguments,
            _options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);

        return new AdbCommandResult(
            result.Started,
            result.TimedOut,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError);
    }

    private static string SelectErrorMessage(AdbProcessResult result, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            return result.StandardError.Trim();
        }

        return string.IsNullOrWhiteSpace(result.StandardOutput) ? fallback : result.StandardOutput.Trim();
    }
}
