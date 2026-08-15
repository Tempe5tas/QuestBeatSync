using System.Text.Json;
using QuestBeatSync.Core.Models;
using QuestBeatSync.Infrastructure.Abstractions;

namespace QuestBeatSync.Infrastructure.Execution;

public sealed class JsonSyncExecutionJournal : ISyncExecutionJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _journalRoot;

    public JsonSyncExecutionJournal(string journalRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalRoot);
        _journalRoot = Path.GetFullPath(journalRoot);
    }

    public async Task WriteAsync(SyncResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        Directory.CreateDirectory(_journalRoot);
        var targetPath = Path.Combine(_journalRoot, $"{result.ExecutionId:N}.json");
        var temporaryPath = Path.Combine(_journalRoot, $".{result.ExecutionId:N}-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
