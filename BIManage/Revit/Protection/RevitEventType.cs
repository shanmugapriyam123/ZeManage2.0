namespace BIManage.Revit.Protection
{
    /// <summary>
    /// Revit events that can have protections applied.
    /// Values 1-6 are protection event types (used in event_protection_settings table and API).
    /// Values 10-14 are tracking-only events (no protection enforcement).
    /// </summary>
    public enum RevitEventType
    {
        // === Protection Event Types (1-6) ===
        // These values are stored in the database and synced with the server API.
        // Do NOT change existing values without a database migration.

        /// <summary>
        /// DocumentOpening — fires before a document is opened (cancellable).
        /// Protections: Duplicate Username, Central File, Model Upgrade
        /// </summary>
        DocumentOpening = 1,

        /// <summary>
        /// DocumentSaving — fires before a document is saved (cancellable).
        /// Protections: Save Over Earlier File Version
        /// </summary>
        DocumentSaving = 2,

        /// <summary>
        /// FamilyLoadingIntoDocument — fires when a family is being loaded (cancellable).
        /// Protections: Load Family from Non-Approved Location
        /// </summary>
        FamilyLoadingIntoDocument = 3,

        /// <summary>
        /// DocumentPrinting — fires before a document is printed (cancellable).
        /// Protections: Document Printing Protection
        /// </summary>
        DocumentPrinting = 4,

        /// <summary>
        /// DocumentChanged — fires after a document transaction is committed (not cancellable).
        /// Protections: Revit Link Pin Prompt (post-insert detection)
        /// </summary>
        DocumentChanged = 5,

        /// <summary>
        /// CommandProtection — fires before a Revit command executes (cancellable).
        /// Protections: CAD Import, CAD Explode, Transfer Project Standards, Document Exporting
        /// Note: Intercepted via CommandInterceptionService, not a Revit application event.
        /// </summary>
        CommandProtection = 6,

        // === Tracking Events (10+) — no protection enforcement ===

        /// <summary>
        /// Application initialized event - fires when Revit starts.
        /// Used for tracking only.
        /// </summary>
        ApplicationInitialized = 10,

        /// <summary>
        /// Application closing event - fires when Revit closes.
        /// Used for tracking only.
        /// </summary>
        ApplicationClosing = 11,

        /// <summary>
        /// Document opened event - fires after document is opened.
        /// Used for tracking only.
        /// </summary>
        DocumentOpened = 12,

        /// <summary>
        /// Document closed event - fires after document is closed.
        /// Used for tracking only.
        /// </summary>
        DocumentClosed = 13,

        /// <summary>
        /// Document created event - fires after new document is created.
        /// Used for tracking only.
        /// </summary>
        DocumentCreated = 14,

        /// <summary>
        /// Document saved event - fires after document is saved.
        /// Used for tracking only.
        /// </summary>
        DocumentSaved = 15,

        /// <summary>
        /// Sync completed event - fires after sync operation completes.
        /// Used for tracking only.
        /// </summary>
        DocumentSynchronizedWithCentral = 16,

        // === Legacy values (kept for backward compatibility with removed protections' code) ===

        /// <summary>Sync with central (removed from active protections). Handler code preserved.</summary>
        DocumentSynchronizingWithCentral = 50,
        /// <summary>CAD file importing via command (legacy value, now CommandProtection=6).</summary>
        FileImporting = 51,
        /// <summary>CAD explode via command (legacy value, now CommandProtection=6).</summary>
        CADExploding = 52,
        /// <summary>Copy/Monitor detection (removed from active protections). Handler code preserved.</summary>
        CopyMonitor = 53,
        /// <summary>CAD Import Pin Prompt (removed from active protections). Handler code preserved.</summary>
        CADImportCompleted = 54,
    }
}
