using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Engram.Worker;

/// <summary>
/// Minimal daily log file under <c>~/.engram/logs</c>. The worker usually runs detached with no console,
/// so this is where <c>engram doctor</c> and you look when something is off. No third-party logger.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose() => _loggers.Clear();

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var now = DateTimeOffset.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)).Append(' ')
            .Append(level.ToString()[..4].ToUpperInvariant()).Append(' ')
            .Append(category).Append(": ").Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        line.AppendLine();
        var path = Path.Combine(_dir, $"worker-{now:yyyyMMdd}.log");
        lock (_gate)
        {
            try
            {
                File.AppendAllText(path, line.ToString());
            }
            catch (IOException)
            {
                // Logging must never take the worker down.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
