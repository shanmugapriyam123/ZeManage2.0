using System;
using System.Diagnostics;
using BIManage.Infrastructure.Logging;

namespace BIManageRevit.Tests.Helpers
{
    public class ConsoleLogger : ILogger
    {
        public void LogInfo(string message) => Debug.WriteLine($"[INFO] {message}");
        public void LogWarning(string message) => Debug.WriteLine($"[WARN] {message}");
        public void LogError(string message, Exception? exception = null) =>
            Debug.WriteLine($"[ERROR] {message} {exception?.Message}");
        public void LogDebug(string message) => Debug.WriteLine($"[DEBUG] {message}");
        public void Dispose() { }
    }
}
