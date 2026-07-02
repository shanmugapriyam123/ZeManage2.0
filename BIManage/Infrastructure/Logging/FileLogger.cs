using System;
using System.IO;
using System.Text;

namespace BIManage.Infrastructure.Logging
{
    /// <summary>
    ///     File-based logger implementation
    /// </summary>
    public class FileLogger : ILogger
    {
        private readonly string _logDirectory;
        private readonly string _logFilePath;
        private readonly object _lockObject = new object();
        private bool _disposed;

        public FileLogger(string logDirectory)
        {
            _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));

            var logFileName = $"BIManageRevit_{DateTime.Now:yyyyMMdd_HHmm}.log";
            _logFilePath = Path.Combine(_logDirectory, logFileName);

            // Write initial log entry
            WriteLog("INFO", "Logger initialized");
        }

        public void LogInfo(string message)
        {
            WriteLog("INFO", message);
        }

        public void LogWarning(string message)
        {
            WriteLog("WARN", message);
        }

        public void LogError(string message, Exception? exception = null)
        {
            var sb = new StringBuilder(message);
            var ex = exception;
            int depth = 0;
            while (ex != null && depth < 10)
            {
                sb.Append($"\n[{depth}] {ex.GetType().FullName}: {ex.Message}");
                if (ex is TypeInitializationException tie)
                    sb.Append($"\n  TypeName: {tie.TypeName}");
                if (ex is FileNotFoundException fnf)
                    sb.Append($"\n  FileName: {fnf.FileName}\n  FusionLog: {fnf.FusionLog}");
                sb.Append($"\n  StackTrace: {ex.StackTrace}");
                ex = ex.InnerException;
                depth++;
            }
            WriteLog("ERROR", sb.ToString());
        }

        public void LogDebug(string message)
        {
#if DEBUG
            WriteLog("DEBUG", message);
#endif
        }

        private void WriteLog(string level, string message)
        {
            if (_disposed) return;

            lock (_lockObject)
            {
                try
                {
                    var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
                    
                    // Append to log file
                    File.AppendAllText(_logFilePath, logEntry + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // Silently fail if logging fails (avoid infinite loops)
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            lock (_lockObject)
            {
                WriteLog("INFO", "Logger shutting down");
                _disposed = true;
            }
        }
    }
}
