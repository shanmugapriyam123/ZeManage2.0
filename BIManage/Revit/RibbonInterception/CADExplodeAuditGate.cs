using System.Threading;

namespace BIManage.Revit.RibbonInterception
{
    /// <summary>
    /// One-shot suppression flag shared between the two code paths that enforce
    /// Ze_CADExplodeProtection BEFORE the actual Revit Explode transaction commits:
    ///
    ///   1. CADExplodeRibbonSwap (ribbon Full/Partial Explode buttons), and
    ///   2. CommandInterceptionService Priority 3 (context-menu Explode commands
    ///      ID_IMPORT_INSTANCE_EXPLODE / ID_IMPORT_INST_PARTIAL_EXPLODE).
    ///
    /// Both paths show the protection dialog, write an audit entry, and dispatch the
    /// real explode. When that explode commits, EventRegistryService.CheckCADExplodePostAction
    /// would otherwise re-run EnforceEventProtection on the resulting "Full Explode" /
    /// "Partial Explode" transaction and double-audit (or worse, double-prompt). This gate
    /// lets either upstream path mark the next post-action firing as already-handled.
    ///
    /// Design notes:
    /// • Static state on a tiny class so both producers and the consumer can reach it
    ///   without a service-locator detour or making a public mutable field on the swap.
    /// • Interlocked.Exchange for atomicity. Rapid double-clicks repeating Suppress() are
    ///   benign — the second Set is idempotent — and the first ConsumeSuppressed() resets
    ///   the flag, so the second post-action firing (if any) runs the normal path.
    /// • One-shot semantics on purpose: if a swap-dispatched explode somehow doesn't fire
    ///   a "Full Explode" transaction (e.g. the user cancelled the picker after we Posted
    ///   the command), the suppression sits at 1 and is consumed by whichever post-action
    ///   transaction fires next. That's the closest we can get to "consume on commit"
    ///   without coupling to Revit transaction events; the practical worst case is one
    ///   non-Explode action getting incorrectly skipped, which the post-action handler
    ///   only audits anyway (no user-visible regression).
    /// </summary>
    internal static class CADExplodeAuditGate
    {
        private static int _suppressNextPostAction;

        /// <summary>
        /// Mark the next CheckCADExplodePostAction firing as already-handled by an upstream
        /// pre-action enforcer. Safe to call repeatedly — the flag is idempotent.
        /// </summary>
        public static void Suppress()
        {
            Interlocked.Exchange(ref _suppressNextPostAction, 1);
        }

        /// <summary>
        /// Atomically read-and-clear. Returns true exactly once per Suppress() call (or per
        /// run of consecutive Suppress() calls), and false otherwise.
        /// </summary>
        public static bool ConsumeSuppressed()
        {
            return Interlocked.Exchange(ref _suppressNextPostAction, 0) == 1;
        }
    }
}
