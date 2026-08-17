using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Adb;

public sealed class AdbQuestTransport : IQuestTransport
{
    private readonly AdbQuestTransportOptions _options;
    private readonly AdbExecutableResolver _executableResolver;
    private readonly IAdbProcessRunner _processRunner;
    private readonly AdbEnvironmentManager? _environmentManager;

    public AdbQuestTransport(
        AdbQuestTransportOptions options,
        AdbExecutableResolver? executableResolver = null,
        IAdbProcessRunner? processRunner = null,
        AdbEnvironmentManager? environmentManager = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _executableResolver = executableResolver ?? new AdbExecutableResolver();
        _processRunner = processRunner ?? new SystemAdbProcessRunner();
        _environmentManager = environmentManager;
    }

    public async Task<QuestDeviceDiscoveryResult> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var executablePath = ResolveExecutable();
        if (executablePath is null)
        {
            return QuestDeviceDiscoveryResult.Failed(
                QuestDeviceDiscoveryStatus.AdbNotAvailable,
                "ADB executable was not found.");
        }

        var result = await _processRunner.RunAsync(
            executablePath,
            ["devices"],
            _options.ShellCommandTimeout,
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
        return RunDeviceCommandAsync(
            device,
            ["shell", .. arguments],
            _options.ShellCommandTimeout,
            cancellationToken);
    }

    public Task<AdbCommandResult> PushAsync(
        QuestDevice device,
        string localPath,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        return RunDeviceCommandAsync(
            device,
            ["push", localPath, remotePath],
            _options.FileTransferTimeout,
            cancellationToken);
    }

    public Task<AdbCommandResult> PullAsync(
        QuestDevice device,
        string remotePath,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        return RunDeviceCommandAsync(
            device,
            ["pull", remotePath, localPath],
            _options.FileTransferTimeout,
            cancellationToken);
    }

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
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        var executablePath = ResolveExecutable();
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
            timeout,
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

    private string? ResolveExecutable() => _environmentManager is null
        ? _executableResolver.Resolve(_options)
        : _environmentManager.Current.IsReady ? _environmentManager.Current.ExecutablePath : null;
}
