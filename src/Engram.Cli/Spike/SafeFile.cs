using System.Diagnostics;
using System.Text;

namespace Engram.Cli.Spike;

/// <summary>Append-only file writes that tolerate several hook processes writing at once.</summary>
internal static class SafeFile
{
    private static readonly TimeSpan s_lockWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_retryBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Appends <paramref name="bytes"/> as one write. Writers are serialized with a cross-process named
    /// mutex (Claude Code runs matching hooks in parallel), so lines never interleave or get lost. If
    /// the lock can't be had within 2 s, it writes anyway rather than drop the record. Sharing
    /// violations from outside readers are retried briefly.
    /// </summary>
    public static void Append(string path, ReadOnlySpan<byte> bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var mutex = new Mutex(initiallyOwned: false, MutexName(dir ?? path));
        var owned = false;
        try
        {
            try
            {
                owned = mutex.WaitOne(s_lockWait);
            }
            catch (AbandonedMutexException)
            {
                owned = true; // a previous holder died mid-write; we own it now
            }

            AppendWithRetry(path, bytes);
        }
        finally
        {
            if (owned)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>Best-effort error log. Never throws.</summary>
    public static void TryLogError(string spikeDir, string message)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:O} {message.ReplaceLineEndings(" | ")}\n";
            Append(Path.Combine(spikeDir, "errors.log"), Encoding.UTF8.GetBytes(line));
        }
        catch
        {
            // Nowhere left to report to. Fail open.
        }
    }

    private static void AppendWithRetry(string path, ReadOnlySpan<byte> bytes)
    {
        var start = Stopwatch.GetTimestamp();
        var attempt = 0;
        while (true)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1);
                stream.Write(bytes);
                return;
            }
            catch (IOException ex) when (IsRetryable(ex) && Stopwatch.GetElapsedTime(start) < s_retryBudget)
            {
                attempt++;
                Thread.Sleep(Math.Min(2 * attempt, 20));
            }
        }
    }

    // Sharing/lock violations are retryable; missing directories, bad paths and permission errors are not.
    private static bool IsRetryable(IOException ex) =>
        ex is not (DirectoryNotFoundException or FileNotFoundException or PathTooLongException or DriveNotFoundException);

    /// <summary>One lock per folder. FNV-1a keeps the name short and free of path characters.</summary>
    private static string MutexName(string dir)
    {
        var key = OperatingSystem.IsWindows() ? dir.ToUpperInvariant() : dir;
        var hash = 14695981039346656037UL;
        foreach (var c in key)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return $"engram-append-{hash:x16}";
    }
}
