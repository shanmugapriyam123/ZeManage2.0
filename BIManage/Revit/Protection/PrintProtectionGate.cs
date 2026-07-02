using System.Threading;

namespace BIManage.Revit.Protection
{
    /// <summary>
    /// One-shot suppression flag shared between the two code paths that enforce
    /// Ze_DocumentPrintingProtection for a single print action:
    ///
    ///   1. CommandInterceptionService Priority 3 (BeforeExecuted on ID_REVIT_FILE_PRINT /
    ///      ID_BATCH_PRINT / ID_EXPORT_PDF), and
    ///   2. EventRegistryService.OnDocumentPrinting (DocumentPrinting application event).
    ///
    /// Path 1 fires when the user presses Ctrl+P (or activates the print command). If the
    /// user passes the protection dialog there, Revit then shows its own print settings
    /// dialog. When the user clicks OK, Revit fires the DocumentPrinting event (path 2),
    /// which would show the dialog again. This gate lets path 1 mark the next
    /// OnDocumentPrinting firing as already-handled so path 2 is skipped.
    ///
    /// Design mirrors CADExplodeAuditGate — see that class for the full rationale.
    /// </summary>
    internal static class PrintProtectionGate
    {
        private static int _suppressNextDocumentPrinting;

        /// <summary>
        /// Mark the next OnDocumentPrinting firing as already-handled by the command
        /// interception pre-action enforcer. Safe to call repeatedly — idempotent.
        /// </summary>
        public static void Suppress()
        {
            Interlocked.Exchange(ref _suppressNextDocumentPrinting, 1);
        }

        /// <summary>
        /// Atomically read-and-clear. Returns true exactly once per Suppress() call (or per
        /// run of consecutive Suppress() calls), and false otherwise.
        /// </summary>
        public static bool ConsumeSuppressed()
        {
            return Interlocked.Exchange(ref _suppressNextDocumentPrinting, 0) == 1;
        }
    }
}
