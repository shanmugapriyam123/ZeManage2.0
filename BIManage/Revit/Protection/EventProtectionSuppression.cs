using System;
using System.Threading;

namespace BIManage.Revit.Protection
{
    /// <summary>
    /// Scoped suppression for the Event Protection "Save As" dialog.
    /// Wrap plugin-internal SaveAs calls (e.g. the Deep Analysis metrics collectors that
    /// save each family to a temp .rfa to read its size) in a using-block so the
    /// DocumentSavingAs handler skips the user-facing comment prompt for those calls.
    /// Use a counter (not a bool) so nested scopes compose correctly.
    /// </summary>
    public static class EventProtectionSuppression
    {
        private static int _suppressCount;

        public static bool IsActive => Volatile.Read(ref _suppressCount) > 0;

        public static IDisposable BeginScope()
        {
            Interlocked.Increment(ref _suppressCount);
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Decrement(ref _suppressCount);
            }
        }
    }
}
