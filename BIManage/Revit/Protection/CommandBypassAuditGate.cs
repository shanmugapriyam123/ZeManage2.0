using System.Threading;

namespace BIManage.Revit.Protection
{
    /// <summary>
    /// One-shot suppression flag shared between:
    ///   1. Command-binding pre-action enforcers (CommandProtectionBinding + RuleCommandInterceptor
    ///      ribbon-path wrappers), and
    ///   2. ElementBypassDetector (DocumentChanged consumer).
    ///
    /// When a ribbon click / keyboard shortcut routes through a command binding and the priority
    /// chain (CommandProtection → Rule) determines the command will proceed, the binding calls
    /// Suppress() to mark the next DocumentChanged firing as already-handled. The bypass detector
    /// consumes the flag and skips re-evaluation for that transaction — preventing a second dialog
    /// for the same user action.
    ///
    /// Design notes (same as CADExplodeAuditGate):
    /// • Interlocked.Exchange for atomicity. Repeating Suppress() is idempotent.
    /// • One-shot Consume on the next *matched* DocumentChanged (the detector checks transaction
    ///   name first; non-matching changes leave the flag intact).
    /// • If the user's gesture somehow doesn't fire a DocumentChanged (e.g. the command was
    ///   cancelled inside Revit's pick stage), the flag sits at 1 until the next tracked
    ///   transaction consumes it. The practical worst case is one bypass gesture incorrectly
    ///   skipped — same severity as CADExplodeAuditGate's accepted edge case.
    /// </summary>
    internal static class CommandBypassAuditGate
    {
        private static int _suppressNextPostAction;

        /// <summary>
        /// Mark the next bypass-detector firing as already-handled by an upstream command binding.
        /// Safe to call repeatedly — the flag is idempotent.
        /// </summary>
        public static void Suppress()
        {
            Interlocked.Exchange(ref _suppressNextPostAction, 1);
        }

        /// <summary>
        /// Atomically read-and-clear. Returns true exactly once per Suppress() run, false otherwise.
        /// Detectors must only call this after confirming the transaction is one they would have
        /// processed (so unmatched DocumentChanged events don't drain the flag spuriously).
        /// </summary>
        public static bool ConsumeSuppressed()
        {
            return Interlocked.Exchange(ref _suppressNextPostAction, 0) == 1;
        }
    }
}
