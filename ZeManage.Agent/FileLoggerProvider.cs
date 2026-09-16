using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ZeManage.Agent;

/// <summary>
/// Writes agent logs to {LocalAppData}\BIManageRevit\Logs\agent.log
/// Same folder as the Revit plugin DB so both logs live in one place.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    // Debug-level EF Core/HTTP tracing is extremely chatty — left unbounded, this file grows to
    // multiple GB over a long-running install (confirmed: 2.7GB after repeated same-day restarts).
    // Past a certain size, appending to it (combined with AutoFlush below) becomes slow enough to
    // visibly starve whichever thread is waiting on the shared lock for its turn to write a line —
    // including the UI thread, which can make the whole window appear hung even though it's really
    // just queued behind logging I/O. Rotate out anything already oversized before opening.
    private const long MaxLogSizeBytes = 20 * 1024 * 1024; // 20 MB

    public FileLoggerProvider(string logPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        try
        {
            var existing = new FileInfo(logPath);
            if (existing.Exists && existing.Length > MaxLogSizeBytes)
            {
                var rotatedPath = Path.ChangeExtension(logPath, null) + ".previous.log";
                File.Delete(rotatedPath);
                File.Move(logPath, rotatedPath);
            }
        }
        catch { /* best-effort — a rotation failure shouldn't block the agent from starting */ }

        _writer = new StreamWriter(logPath, append: true, Encoding.UTF8) { AutoFlush = true };
        _writer.WriteLine($"--- ZeManage Agent started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _lock);

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger : ILogger
    {
        private readonly string _cat;
        private readonly StreamWriter _writer;
        private readonly object _lock;

        public FileLogger(string category, StreamWriter writer, object lockObj)
        {
            var dot = category.LastIndexOf('.');
            _cat    = dot >= 0 ? category[(dot + 1)..] : category;
            _writer = writer;
            _lock   = lockObj;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var prefix = logLevel switch
            {
                LogLevel.Warning  => "WARN",
                LogLevel.Error    => "ERR ",
                LogLevel.Critical => "CRIT",
                LogLevel.Debug    => "DBUG",
                _                 => "INFO"
            };
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{prefix}] {_cat}: {formatter(state, exception)}";
            if (exception != null) line += Environment.NewLine + "  " + exception.ToString();
            lock (_lock)
                _writer.WriteLine(line);
        }
    }
}
