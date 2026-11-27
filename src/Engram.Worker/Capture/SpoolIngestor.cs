using System.Text.Json;
using Engram.Core;
using Engram.Worker.Memory;

namespace Engram.Worker.Capture;

/// <summary>
/// Picks up what the CLI wrote to <c>~/.engram/spool</c> while the worker was down or still starting: SessionStart
/// payloads (<c>spool/*.json</c>) and notes from <c>engram save</c> (<c>spool/notes/*.json</c>). Checks at startup
/// and then every few seconds.
/// </summary>
internal sealed partial class SpoolIngestor(CaptureQueue queue, WorkerContext context, NoteService notes, ILogger<SpoolIngestor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            IngestOnce();
            IngestNotes();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Enqueues and deletes every spooled file, oldest first. Returns how many were queued.</summary>
    public int IngestOnce()
    {
        var dir = EngramPaths.Spool(context.Home);
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var body = File.ReadAllBytes(file);
                File.Delete(file);
                if (queue.TryEnqueue(new IngestItem(body, "spool")))
                {
                    count++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still being written, or locked; next tick.
                LogSkipped(Path.GetFileName(file), ex.Message);
            }
        }

        return count;
    }

    /// <summary>
    /// Stores every spooled note, oldest first, and deletes its file. A note that can't be stored (unreadable, or
    /// invalid) is renamed to <c>.bad</c> and logged rather than retried forever. Returns how many were stored.
    /// </summary>
    public int IngestNotes()
    {
        var dir = EngramPaths.NoteSpool(context.Home);
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            byte[] body;
            try
            {
                body = File.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSkipped(Path.GetFileName(file), ex.Message);
                continue;
            }

            string? error;
            try
            {
                (var saved, error) = notes.Save(NoteRequest.Parse(body));
                if (saved is not null)
                {
                    LogNoteStored(Path.GetFileName(file), saved.Id, saved.Project);
                    count++;
                }
            }
            catch (JsonException ex)
            {
                error = "not valid JSON: " + ex.Message;
            }
            catch (Exception ex)
            {
                // A database problem (locked, disk full): keep the file and try again next tick.
                LogSkipped(Path.GetFileName(file), ex.Message);
                continue;
            }

            try
            {
                if (error is null)
                {
                    File.Delete(file);
                }
                else
                {
                    LogBadNote(Path.GetFileName(file), error);
                    File.Move(file, Path.ChangeExtension(file, ".bad"), overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Stored but not deleted: the next pass finds the note's client_id and doesn't store it twice.
                LogSkipped(Path.GetFileName(file), ex.Message);
            }
        }

        return count;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Spool file {File} skipped for now: {Reason}")]
    private partial void LogSkipped(string file, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Spooled note {File} stored as #{Id} ({Project})")]
    private partial void LogNoteStored(string file, long id, string project);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Spooled note {File} could not be stored ({Error}); renamed to .bad")]
    private partial void LogBadNote(string file, string error);
}
