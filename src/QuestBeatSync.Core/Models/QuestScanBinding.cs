using System.Security.Cryptography;
using System.Text;

namespace QuestBeatSync.Core.Models;

public sealed record QuestScanBinding
{
    public QuestScanBinding(string deviceSerial, string stateFingerprint, BeatSaberPackageVersion? beatSaberPackageVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSerial);
        if (stateFingerprint is not { Length: 64 } || !stateFingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Quest state fingerprint must be a SHA256 value.", nameof(stateFingerprint));

        DeviceSerial = deviceSerial.Trim();
        StateFingerprint = stateFingerprint.ToUpperInvariant();
        BeatSaberPackageVersion = beatSaberPackageVersion;
    }

    public string DeviceSerial { get; }

    public string StateFingerprint { get; }

    public BeatSaberPackageVersion? BeatSaberPackageVersion { get; }

    public static QuestScanBinding Capture(string deviceSerial, QuestBeatSaberScanResult scan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSerial);
        ArgumentNullException.ThrowIfNull(scan);
        return new QuestScanBinding(deviceSerial.Trim(), ComputeFingerprint(scan), scan.BeatSaberPackageVersion);
    }

    public bool Matches(string deviceSerial, QuestBeatSaberScanResult scan) =>
        string.Equals(DeviceSerial, deviceSerial, StringComparison.Ordinal) &&
        string.Equals(StateFingerprint, ComputeFingerprint(scan), StringComparison.Ordinal);

    public static string ComputeFingerprint(QuestBeatSaberScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var state = new StringBuilder()
            .Append(scan.BeatSaberDetected).Append('|')
            .Append(scan.SongCoreDetected).Append('|')
            .Append(scan.CustomLevelsDetected).Append('|')
            .Append(scan.PlaylistManagerDetected).Append('|')
            .Append(scan.PlaylistsDirectoryDetected).Append('|')
            .Append(scan.BeatSaberPackageVersion?.VersionName).Append('|')
            .Append(scan.BeatSaberPackageVersion?.VersionCode).AppendLine();

        foreach (var map in scan.InstalledMaps.OrderBy(map => map.RemotePath, StringComparer.Ordinal))
        {
            Append(state, "M", map.RemotePath, map.FolderName, map.InfoDatExists.ToString(),
                map.IdentityStatus.ToString(), map.Identity?.Hash, map.SongTitle, map.Mapper,
                map.Format?.Kind.ToString(), map.Format?.ParsedVersion?.ToString());
        }

        foreach (var playlist in scan.InstalledPlaylists.OrderBy(playlist => playlist.RemotePath, StringComparer.Ordinal))
        {
            Append(state, "P", playlist.RemotePath, playlist.Filename, playlist.PlaylistTitle,
                playlist.SongReferenceCount.ToString(), playlist.Format.ToString(),
                playlist.SemanticIdentityComplete.ToString(), playlist.FilenameLineage,
                string.Join(",", playlist.NormalizedSongIdentities ?? []));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToString())));
    }

    private static void Append(StringBuilder target, params string?[] values)
    {
        foreach (var value in values)
        {
            target.Append(value?.Length ?? -1).Append(':').Append(value).Append('|');
        }

        target.AppendLine();
    }
}
