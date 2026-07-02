using System;

namespace BIManage.Infrastructure.Logging
{
    /// <summary>
    ///     Interface for logging services
    /// </summary>
    public interface ILogger : IDisposable
    {
        /// <summary>
        ///     Log an informational message
        /// </summary>
        void LogInfo(string message);

        /// <summary>
        ///     Log a warning message
        /// </summary>
        void LogWarning(string message);

        /// <summary>
        ///     Log an error message
        /// </summary>
        void LogError(string message, Exception? exception = null);

        /// <summary>
        ///     Log a debug message (only in debug builds)
        /// </summary>
        void LogDebug(string message);
    }
}
