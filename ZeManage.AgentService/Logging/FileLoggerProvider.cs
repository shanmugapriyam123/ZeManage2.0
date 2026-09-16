using Microsoft.Extensions.Logging;

namespace ZeManage.AgentService.Logging;

/// <summary>Writes logs to {LocalAppData}\ZeManageAgentService\Logs\agentservice.log — its own
/// folder, separate from the reference Agent's BIManageRevit\Logs\agent.log. Same size-based
/// rotation pattern as the reference Agent's FileLoggerProvider (ported as a fix already proven
/// there, not reference-only): unbounded Debug-level logging can grow to multiple GB and, combined
/// with AutoFlush, slow enough to matter — rotate anything already oversized before opening.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
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
        catch { }

        _writer = new StreamWriter(logPath, append: true, System.Text.Encoding.UTF8) { AutoFlush = true };
        _writer.WriteLine($"--- ZeManage Agent Service started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
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
            _cat = dot >= 0 ? category[(dot + 1)..] : category;
            _writer = writer;
            _lock = lockObj;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var prefix = logLevel switch
            {
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERR ",
                LogLevel.Critical => "CRIT",
                LogLevel.Debug => "DBUG",
                _ => "INFO"
            };
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{prefix}] {_cat}: {formatter(state, exception)}";
            if (exception != null) line += Environment.NewLine + "  " + exception;
            lock (_lock) _writer.WriteLine(line);
        }
    }
}
