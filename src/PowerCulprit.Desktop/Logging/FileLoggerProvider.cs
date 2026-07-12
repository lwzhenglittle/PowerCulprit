using System.Text;
using Microsoft.Extensions.Logging;

namespace PowerCulprit.Desktop.Logging;

/// <summary>
/// A minimal file-based <see cref="ILoggerProvider"/> that appends log lines
/// to a daily-rotating file under the configured log directory. Desktop is a
/// <c>WinExe</c> with no console, so <c>AddConsole()</c> writes to nothing —
/// this provider is the only persistent diagnostic sink. No NuGet dependency;
/// deliberately small so it cannot itself be a source of crashes that the
/// global handlers are trying to log.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly object _writeLock = new();
    private StreamWriter? _writer;
    private string? _currentFileName;
    private bool _disposed;

    public FileLoggerProvider(string logDirectory)
    {
        _logDirectory = logDirectory;
        try { Directory.CreateDirectory(_logDirectory); }
        catch { /* best effort: logging must never throw */ }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_writeLock)
        {
            _disposed = true;
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(string categoryName, LogLevel logLevel, string message, Exception? exception)
    {
        var timestamp = DateTime.Now;
        var line = new StringBuilder(256)
            .Append(timestamp.ToString("O"))
            .Append(" [").Append(logLevel.ToString()).Append("] ")
            .Append('[').Append(categoryName).Append("] ")
            .Append(message);
        if (exception is not null)
        {
            line.Append(Environment.NewLine).Append(exception);
        }
        line.AppendLine();

        var fileName = $"desktop-{timestamp:yyyyMMdd}.log";
        var fullPath = Path.Combine(_logDirectory, fileName);

        lock (_writeLock)
        {
            if (_disposed)
                return;

            try
            {
                if (_writer is null || _currentFileName != fileName)
                {
                    _writer?.Flush();
                    _writer?.Dispose();
                    _currentFileName = fileName;
                    _writer = new StreamWriter(fullPath, append: true, Encoding.UTF8)
                    {
                        AutoFlush = true
                    };
                }
                _writer.Write(line);
            }
            catch
            {
                // Swallow — a logging failure must not propagate.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var message = formatter(state, exception);
            owner.Write(category, logLevel, message, exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Writes crash records directly to disk, usable before the DI logging
/// pipeline is built (e.g. from <c>App.UnhandledException</c> in the
/// constructor). Self-contained: no dependencies on the DI container or on
/// <see cref="FileLoggerProvider"/> being alive.
/// </summary>
internal static class CrashLog
{
    public static void Write(Exception? exception, string context)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PowerCulprit", "logs");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd}.log");
            var entry = new StringBuilder(256)
                .Append(DateTime.Now.ToString("O"))
                .Append(" [CRASH] ")
                .Append(context);
            if (exception is not null)
            {
                entry.Append(Environment.NewLine).Append(exception);
            }
            entry.AppendLine().AppendLine();
            File.AppendAllText(path, entry.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Logging must never throw — nothing else to do.
        }
    }
}
