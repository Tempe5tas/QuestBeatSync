using System.Net;
using System.Text.RegularExpressions;
using QuestBeatSync.Core.Models;

namespace QuestBeatSync.Infrastructure.Adb;

public sealed record AdbNetworkEndpoint(string Host, int Port = 5555)
{
    public string Authority => $"{Host}:{Port}";

    public static bool TryCreate(string? host, int port, out AdbNetworkEndpoint? endpoint, out string? error)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(host)) { error = "Quest address is required."; return false; }
        if (port is < 1 or > 65535) { error = "Port must be between 1 and 65535."; return false; }
        var value = host.Trim();
        if (value.Any(char.IsWhiteSpace) || value.Contains(':') ||
            (!IPAddress.TryParse(value, out var address) && !Regex.IsMatch(value, @"^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$")))
        { error = "Enter a valid IPv4 address or hostname."; return false; }
        if (address is not null && address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        { error = "IPv6 endpoints are not supported yet."; return false; }
        endpoint = new(value, port); error = null; return true;
    }
}

public enum AdbConnectionOutcome { Connected, AlreadyConnected, Disconnected, TimedOut, Failed, AdbNotReady, InvalidEndpoint, Refused }
public sealed record AdbConnectionResult(AdbConnectionOutcome Outcome, string StandardOutput = "", string StandardError = "", string? ErrorMessage = null)
{
    public bool IsSuccess => Outcome is AdbConnectionOutcome.Connected or AdbConnectionOutcome.AlreadyConnected or AdbConnectionOutcome.Disconnected;
}

public interface IAdbConnectionService
{
    Task<AdbConnectionResult> ConnectAsync(AdbNetworkEndpoint endpoint, CancellationToken cancellationToken = default);
    Task<AdbConnectionResult> DisconnectAsync(AdbNetworkEndpoint endpoint, CancellationToken cancellationToken = default);
    Task<AdbConnectionResult> EnableWirelessAdbAsync(QuestDevice device, int port = 5555, CancellationToken cancellationToken = default);
}

public sealed class AdbConnectionService(AdbEnvironmentManager environment, IAdbProcessRunner runner, AdbQuestTransportOptions options) : IAdbConnectionService
{
    public Task<AdbConnectionResult> ConnectAsync(AdbNetworkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        IsValid(endpoint) ? RunHostAsync(["connect", endpoint.Authority], true, cancellationToken) : Task.FromResult(new AdbConnectionResult(AdbConnectionOutcome.InvalidEndpoint, ErrorMessage: "Invalid ADB network endpoint."));

    public Task<AdbConnectionResult> DisconnectAsync(AdbNetworkEndpoint endpoint, CancellationToken cancellationToken = default) =>
        IsValid(endpoint) ? RunHostAsync(["disconnect", endpoint.Authority], false, cancellationToken) : Task.FromResult(new AdbConnectionResult(AdbConnectionOutcome.InvalidEndpoint, ErrorMessage: "Invalid ADB network endpoint."));

    public async Task<AdbConnectionResult> EnableWirelessAdbAsync(QuestDevice device, int port = 5555, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535) return new(AdbConnectionOutcome.InvalidEndpoint, ErrorMessage: "Port must be between 1 and 65535.");
        if (!device.IsConnected || device.TransportKind != QuestTransportKind.Usb)
            return new(AdbConnectionOutcome.Refused, ErrorMessage: "Wireless ADB can only be enabled for a connected USB Quest.");
        return await RunAsync(["-s", device.Serial, "tcpip", port.ToString(System.Globalization.CultureInfo.InvariantCulture)], false, cancellationToken).ConfigureAwait(false);
    }

    private Task<AdbConnectionResult> RunHostAsync(IReadOnlyList<string> arguments, bool connect, CancellationToken token) => RunAsync(arguments, connect, token);
    private static bool IsValid(AdbNetworkEndpoint endpoint) => AdbNetworkEndpoint.TryCreate(endpoint.Host, endpoint.Port, out _, out _);

    private async Task<AdbConnectionResult> RunAsync(IReadOnlyList<string> arguments, bool connect, CancellationToken token)
    {
        var status = environment.Current;
        if (!status.IsReady || string.IsNullOrWhiteSpace(status.ExecutablePath)) return new(AdbConnectionOutcome.AdbNotReady, ErrorMessage: "ADB environment is not ready.");
        var result = await runner.RunAsync(status.ExecutablePath, arguments, options.ShellCommandTimeout, token).ConfigureAwait(false);
        if (result.TimedOut) return new(AdbConnectionOutcome.TimedOut, result.StandardOutput, result.StandardError, "ADB command timed out.");
        var output = (result.StandardOutput + "\n" + result.StandardError).Trim();
        if (!result.Started || result.ExitCode != 0) return new(AdbConnectionOutcome.Failed, result.StandardOutput, result.StandardError, string.IsNullOrWhiteSpace(output) ? "ADB command failed." : output);
        if (!connect) return new(AdbConnectionOutcome.Disconnected, result.StandardOutput, result.StandardError);
        if (Regex.IsMatch(output, @"^already connected to\s+\S+\s*$", RegexOptions.IgnoreCase)) return new(AdbConnectionOutcome.AlreadyConnected, result.StandardOutput, result.StandardError);
        if (Regex.IsMatch(output, @"^connected to\s+\S+\s*$", RegexOptions.IgnoreCase)) return new(AdbConnectionOutcome.Connected, result.StandardOutput, result.StandardError);
        return new(AdbConnectionOutcome.Failed, result.StandardOutput, result.StandardError, "ADB did not report a recognized connection result.");
    }
}
