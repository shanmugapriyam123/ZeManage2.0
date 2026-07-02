using System;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;

namespace BIManage.Infrastructure.Threading
{
    /// <summary>
    /// Helper class for running async tasks without blocking the UI thread.
    /// Used to prevent Revit freezes when network operations timeout.
    /// </summary>
    public static class TaskRunner
    {
        /// <summary>
        /// Run async task without blocking. Logs errors but doesn't throw.
        /// Use this for fire-and-forget operations where the result is not needed immediately.
        /// </summary>
        public static void FireAndForget(Task task, ILogger? logger = null, string? context = null)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    var message = t.Exception.InnerException?.Message ?? t.Exception.Message;
                    logger?.LogWarning($"Background task failed{(context != null ? $" ({context})" : "")}: {message}");
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>
        /// Run async task with timeout. Returns default if timeout or error.
        /// </summary>
        public static async Task<T?> WithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, ILogger? logger = null)
        {
            try
            {
                var timeoutTask = Task.Delay(timeout);
                var completedTask = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);

                if (completedTask == timeoutTask)
                {
                    logger?.LogDebug($"Task timed out after {timeout.TotalSeconds}s");
                    return default;
                }

                return await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogDebug($"Task failed: {ex.Message}");
                return default;
            }
        }

        /// <summary>
        /// Run async task with timeout. Returns false if timeout or error.
        /// </summary>
        public static async Task<bool> WithTimeoutAsync(Task task, TimeSpan timeout, ILogger? logger = null)
        {
            try
            {
                var timeoutTask = Task.Delay(timeout);
                var completedTask = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);

                if (completedTask == timeoutTask)
                {
                    logger?.LogDebug($"Task timed out after {timeout.TotalSeconds}s");
                    return false;
                }

                await task.ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogDebug($"Task failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Run an async action in the background without blocking.
        /// Captures the logger for error reporting.
        /// </summary>
        public static void RunInBackground(Func<Task> asyncAction, ILogger? logger = null, string? context = null)
        {
            Task.Run(async () =>
            {
                try
                {
                    await asyncAction().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning($"Background action failed{(context != null ? $" ({context})" : "")}: {ex.Message}");
                }
            });
        }
    }
}
