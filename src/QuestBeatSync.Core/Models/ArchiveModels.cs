namespace QuestBeatSync.Core.Models;

public enum ArchiveLogicalRoot { CustomLevels, Playlists, Covers, SongCoreAuxiliary }
public enum ArchiveEntryKind { File }
public enum ArchiveConsistencyStatus { Consistent }
public enum QuestBackupStatus { Completed, Refused, Canceled, Failed, SourceChanged }
public enum SnapshotVerificationStatus { Valid, NotFound, UnsupportedFormat, Corrupted, Unverified }

public sealed record ArchiveDevice(string Serial, QuestTransportKind TransportKind, string? AndroidModel);
public sealed record ArchiveBeatSaber(string? VersionName, long? VersionCode);

public sealed record ArchiveEntry(
    ArchiveLogicalRoot LogicalRoot,
    string RelativePath,
    ArchiveEntryKind EntryKind,
    long Size,
    string Sha256,
    string BlobId);

public sealed record ArchiveManifest(
    int FormatVersion,
    string SnapshotId,
    DateTimeOffset CreatedAtUtc,
    ArchiveDevice Device,
    ArchiveBeatSaber BeatSaber,
    string QuestStateFingerprintBefore,
    string QuestStateFingerprintAfter,
    ArchiveConsistencyStatus ConsistencyStatus,
    IReadOnlyList<ArchiveEntry> Entries)
{
    public const int CurrentFormatVersion = 1;
}

public sealed record QuestBackupProgress(
    int FilesDiscovered,
    int FilesArchived,
    long BytesArchived,
    int NewBlobsWritten,
    int ExistingBlobsReused,
    string? CurrentFile);

public sealed record QuestBackupResult(
    QuestBackupStatus Status,
    string Message,
    ArchiveManifest? Manifest = null,
    QuestBackupProgress? Progress = null,
    IReadOnlyList<string>? Warnings = null);

public sealed record SnapshotSummary(
    DateTimeOffset CreatedAtUtc,
    string SnapshotId,
    string DeviceSerial,
    string? BeatSaberVersion,
    int MapFolderCount,
    int PlaylistFileCount,
    long TotalLogicalBytes,
    SnapshotVerificationStatus VerificationStatus);

public sealed record SnapshotVerificationResult(
    SnapshotVerificationStatus Status,
    string Message,
    IReadOnlyList<string> Errors);
