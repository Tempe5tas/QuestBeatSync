using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace QuestBeatSync.Infrastructure.Adb;

public enum AdbEnvironmentState { Ready, NotFound, Invalid, ValidationFailed, Installing, DownloadFailed }
public enum AdbEnvironmentSource { None, UserSelected, QBSyncManaged, System }

public sealed record AdbEnvironmentStatus(
    AdbEnvironmentState State,
    AdbEnvironmentSource Source = AdbEnvironmentSource.None,
    string? ExecutablePath = null,
    string? Version = null,
    string? ValidationError = null)
{
    public bool IsReady => State == AdbEnvironmentState.Ready;
    public static AdbEnvironmentStatus NotFound() => new(AdbEnvironmentState.NotFound);
}

public sealed record AdbDistribution(Uri DownloadUri, string Identity, string ArchiveFileName, string? Sha256 = null);

public interface IAdbDistributionProvider
{
    Task<AdbDistribution> ResolveAsync(CancellationToken cancellationToken = default);
}

public sealed class OfficialAdbDistributionProvider : IAdbDistributionProvider
{
    public Task<AdbDistribution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : null;
        if (platform is null) throw new PlatformNotSupportedException("Managed ADB is supported only on Windows and Linux.");
        var file = $"platform-tools-latest-{platform}.zip";
        return Task.FromResult(new AdbDistribution(
            new Uri($"https://dl.google.com/android/repository/{file}"),
            $"official-latest-{platform}", file));
    }
}

public interface IAdbPackageClient
{
    Task DownloadAsync(Uri uri, string destinationPath, CancellationToken cancellationToken);
    Task ExtractAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken);
}

public sealed class AdbPackageClient(HttpClient httpClient) : IAdbPackageClient
{
    public async Task DownloadAsync(Uri uri, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    public Task ExtractAsync(string archivePath, string destinationDirectory, CancellationToken cancellationToken) =>
        Task.Run(() => ZipFile.ExtractToDirectory(archivePath, destinationDirectory), cancellationToken);
}

public sealed class AdbEnvironmentManager
{
    private static readonly Regex IdentityPattern = new(@"Android Debug Bridge version\s+([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PackageVersionPattern = new(@"^Version\s+([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private readonly AdbQuestTransportOptions _options;
    private readonly AdbSettingsStore _settings;
    private readonly AdbExecutableResolver _resolver;
    private readonly IAdbProcessRunner _runner;
    private readonly IAdbDistributionProvider _distributionProvider;
    private readonly IAdbPackageClient _packageClient;

    public AdbEnvironmentManager(AdbQuestTransportOptions options, AdbSettingsStore settings,
        AdbExecutableResolver resolver, IAdbProcessRunner runner, IAdbDistributionProvider distributionProvider,
        IAdbPackageClient packageClient)
    {
        _options = options; _settings = settings; _resolver = resolver; _runner = runner;
        _distributionProvider = distributionProvider; _packageClient = packageClient;
        Current = AdbEnvironmentStatus.NotFound();
    }

    public AdbEnvironmentStatus Current { get; private set; }
    public event EventHandler? StatusChanged;

    public async Task<AdbEnvironmentStatus> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        AdbEnvironmentStatus? lastFailure = null;
        var userPath = _settings.LoadConfiguredPath();
        if (!string.IsNullOrWhiteSpace(userPath))
        {
            var user = await ValidateCandidateAsync(userPath, AdbEnvironmentSource.UserSelected, cancellationToken).ConfigureAwait(false);
            if (user.IsReady) return Publish(user);
            lastFailure = user;
        }

        foreach (var candidate in ManagedCandidates())
        {
            var managed = await ValidateCandidateAsync(candidate, AdbEnvironmentSource.QBSyncManaged, cancellationToken).ConfigureAwait(false);
            if (managed.IsReady) return Publish(managed);
            lastFailure = managed;
        }

        var automaticOptions = new AdbQuestTransportOptions { AppDataToolsDirectory = Path.Combine(_options.AppDataToolsDirectory, "__none__") };
        var systemPath = _resolver.Resolve(automaticOptions);
        if (systemPath is not null)
        {
            var system = await ValidateCandidateAsync(systemPath, AdbEnvironmentSource.System, cancellationToken).ConfigureAwait(false);
            if (system.IsReady) return Publish(system);
            return Publish(system);
        }

        return Publish(lastFailure ?? AdbEnvironmentStatus.NotFound());
    }

    public Task<AdbEnvironmentStatus> ValidateAsync(string executablePath, CancellationToken cancellationToken = default) =>
        ValidateCandidateAsync(executablePath, AdbEnvironmentSource.UserSelected, cancellationToken);

    public async Task<AdbEnvironmentStatus> SelectUserExecutableAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        var previous = Current;
        var validated = await ValidateCandidateAsync(executablePath, AdbEnvironmentSource.UserSelected, cancellationToken).ConfigureAwait(false);
        if (!validated.IsReady) { Current = previous; _options.ConfiguredExecutablePath = previous.IsReady ? previous.ExecutablePath : null; StatusChanged?.Invoke(this, EventArgs.Empty); return validated; }
        _settings.SaveConfiguredPath(validated.ExecutablePath);
        return Publish(validated);
    }

    public async Task<AdbEnvironmentStatus> UseAutomaticSelectionAsync(CancellationToken cancellationToken = default)
    {
        _settings.SaveConfiguredPath(null);
        return await DiscoverAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdbEnvironmentStatus> InstallManagedAsync(CancellationToken cancellationToken = default)
    {
        var previous = Current;
        Publish(new(AdbEnvironmentState.Installing));
        var tempRoot = Path.Combine(_options.AppDataToolsDirectory, ".install-" + Guid.NewGuid().ToString("N"));
        string? newlyPublishedRoot = null;
        try
        {
            Directory.CreateDirectory(tempRoot);
            var distribution = await _distributionProvider.ResolveAsync(cancellationToken).ConfigureAwait(false);
            var archive = Path.Combine(tempRoot, distribution.ArchiveFileName);
            await _packageClient.DownloadAsync(distribution.DownloadUri, archive, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(archive) || new FileInfo(archive).Length == 0) throw new InvalidDataException("The downloaded archive was empty.");
            if (!string.IsNullOrWhiteSpace(distribution.Sha256))
            {
                await using var stream = File.OpenRead(archive);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(actual, distribution.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The Platform-Tools archive failed official SHA-256 verification.");
            }
            var extracted = Path.Combine(tempRoot, "extracted");
            Directory.CreateDirectory(extracted);
            await _packageClient.ExtractAsync(archive, extracted, cancellationToken).ConfigureAwait(false);
            var adb = Directory.EnumerateFiles(extracted, OperatingSystem.IsWindows() ? "adb.exe" : "adb", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidDataException("The archive did not contain adb.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(adb, File.GetUnixFileMode(adb) | UnixFileMode.UserExecute);
            var validated = await ValidateCandidateAsync(adb, AdbEnvironmentSource.QBSyncManaged, cancellationToken).ConfigureAwait(false);
            if (!validated.IsReady) throw new InvalidDataException(validated.ValidationError ?? "Extracted adb failed validation.");
            var versionId = Sanitize(validated.Version ?? distribution.Identity);
            var finalRoot = Path.Combine(_options.AppDataToolsDirectory, "platform-tools", versionId);
            Directory.CreateDirectory(Path.GetDirectoryName(finalRoot)!);
            if (!Directory.Exists(finalRoot)) { Directory.Move(Path.GetDirectoryName(adb)!, finalRoot); newlyPublishedRoot = finalRoot; }
            var finalAdb = Path.Combine(finalRoot, Path.GetFileName(adb));
            var finalStatus = await ValidateCandidateAsync(finalAdb, AdbEnvironmentSource.QBSyncManaged, cancellationToken).ConfigureAwait(false);
            if (!finalStatus.IsReady) throw new InvalidDataException(finalStatus.ValidationError ?? "Published adb failed validation.");
            return Publish(finalStatus);
        }
        catch (OperationCanceledException) { CleanupNewPublish(); Current = previous; StatusChanged?.Invoke(this, EventArgs.Empty); throw; }
        catch (Exception exception)
        {
            CleanupNewPublish();
            Current = previous.IsReady ? previous with { ValidationError = exception.Message } : new(AdbEnvironmentState.DownloadFailed, ValidationError: exception.Message);
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return Current;
        }
        finally { try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { } }

        void CleanupNewPublish() { try { if (newlyPublishedRoot is not null && Directory.Exists(newlyPublishedRoot)) Directory.Delete(newlyPublishedRoot, true); } catch { } }
    }

    private async Task<AdbEnvironmentStatus> ValidateCandidateAsync(string path, AdbEnvironmentSource source, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new(AdbEnvironmentState.Invalid, source, path, ValidationError: "ADB executable does not exist.");
        var result = await _runner.RunAsync(path, ["version"], _options.ShellCommandTimeout, token).ConfigureAwait(false);
        if (!result.Started) return new(AdbEnvironmentState.Invalid, source, path, ValidationError: Error(result, "ADB could not be started."));
        if (result.TimedOut) return new(AdbEnvironmentState.ValidationFailed, source, path, ValidationError: "adb version timed out.");
        var combined = result.StandardOutput + "\n" + result.StandardError;
        var identity = IdentityPattern.Match(combined);
        if (result.ExitCode != 0 || !identity.Success) return new(AdbEnvironmentState.ValidationFailed, source, path, ValidationError: Error(result, "Unrecognized adb version output."));
        var package = PackageVersionPattern.Match(combined);
        var version = package.Success ? package.Groups[1].Value.Trim() : identity.Groups[1].Value.Trim();
        return new(AdbEnvironmentState.Ready, source, Path.GetFullPath(path), version);
    }

    private IEnumerable<string> ManagedCandidates()
    {
        var root = Path.Combine(_options.AppDataToolsDirectory, "platform-tools");
        if (!Directory.Exists(root)) yield break;
        var name = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        foreach (var path in Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)) yield return path;
    }

    private AdbEnvironmentStatus Publish(AdbEnvironmentStatus status)
    {
        Current = status;
        _options.ConfiguredExecutablePath = status.IsReady ? status.ExecutablePath : null;
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return status;
    }

    private static string Error(AdbProcessResult result, string fallback) => !string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardError.Trim() : !string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardOutput.Trim() : fallback;
    private static string Sanitize(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
}
