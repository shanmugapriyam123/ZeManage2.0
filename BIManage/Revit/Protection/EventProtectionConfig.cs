using System.Collections.Generic;

namespace BIManage.Revit.Protection
{
    /// <summary>
    /// Base class for event-specific configuration stored as JSON in ConfigurationJson field
    /// </summary>
    public abstract class EventProtectionConfig
    {
    }

    /// <summary>
    /// Configuration for file opening location protection
    /// Ze_OpenFileFromNonApprovedProtection
    /// </summary>
    public class FileOpeningLocationConfig : EventProtectionConfig
    {
        /// <summary>
        /// List of approved folder paths (e.g., "\\server\bim\projects\")
        /// Files opened from paths NOT starting with these are flagged
        /// </summary>
        public List<string> ApprovedPaths { get; set; } = new List<string>();

        /// <summary>
        /// Allow opening files from local drives (C:\, D:\, etc.)
        /// </summary>
        public bool AllowLocalDrives { get; set; } = false;

        /// <summary>
        /// Allow opening cloud models (BIM360, ACC, etc.)
        /// </summary>
        public bool AllowCloudModels { get; set; } = true;
    }

    /// <summary>
    /// Configuration for model upgrade protection
    /// Ze_ModelUpgradeProtection
    /// </summary>
    public class ModelUpgradeConfig : EventProtectionConfig
    {
        /// <summary>
        /// Protect project files (.rvt) from upgrade
        /// </summary>
        public bool ProtectProjectFiles { get; set; } = true;

        /// <summary>
        /// Protect family files (.rfa) from upgrade
        /// </summary>
        public bool ProtectFamilyFiles { get; set; } = true;

        /// <summary>
        /// Minimum version difference to trigger protection
        /// e.g., 1 means 2024 file in 2025 Revit triggers
        /// </summary>
        public int? MinimumVersionDifference { get; set; } = 1;
    }

    /// <summary>
    /// Configuration for central file opening protection
    /// Ze_OpenCentralFileProtection
    /// </summary>
    public class CentralFileConfig : EventProtectionConfig
    {
        /// <summary>
        /// Always warn when opening central (even if creating local)
        /// </summary>
        public bool WarnOnCentralAccess { get; set; } = true;

        /// <summary>
        /// Block opening central file completely (require local copy)
        /// </summary>
        public bool BlockDirectCentralOpen { get; set; } = false;
    }

    /// <summary>
    /// Configuration for family library protection
    /// Ze_FamilyLibrarySettings
    /// </summary>
    public class FamilyLibraryConfig : EventProtectionConfig
    {
        /// <summary>
        /// List of approved library paths (e.g., "\\server\content\families\")
        /// </summary>
        public List<string> ApprovedLibraryPaths { get; set; } = new List<string>();

        /// <summary>
        /// List of non-approved (blocked) paths. Families loaded from these paths are always blocked.
        /// Defaults to ["C:\"] — local C drive is blocked out of the box.
        /// </summary>
        public List<string> NonApprovedPaths { get; set; } = new List<string> { @"C:\" };

        /// <summary>
        /// List of protected categories (e.g., "Doors", "Windows")
        /// Only families in these categories are protected
        /// Empty list = all categories protected
        /// </summary>
        public List<string> ProtectedCategories { get; set; } = new List<string>();

        /// <summary>
        /// Check for family version mismatch
        /// </summary>
        public bool CheckVersionMismatch { get; set; } = true;
    }

    /// <summary>
    /// Configuration for family overwrite protection
    /// Ze_FamilyLoadingProtection
    /// </summary>
    public class FamilyOverwriteConfig : EventProtectionConfig
    {
        /// <summary>
        /// Warn when overwriting any existing family
        /// </summary>
        public bool WarnOnOverwrite { get; set; } = true;

        /// <summary>
        /// Block overwriting families marked as protected (in ExtensibleStorage)
        /// </summary>
        public bool BlockProtectedFamilyOverwrite { get; set; } = true;
    }

    /// <summary>
    /// Configuration for save over earlier version protection
    /// Ze_SaveOverEarlierFileVersionProtection
    /// </summary>
    public class SaveVersionConfig : EventProtectionConfig
    {
        /// <summary>
        /// Warn when saving to older version format
        /// </summary>
        public bool WarnOnDowngrade { get; set; } = true;

        /// <summary>
        /// Block saving to older version completely
        /// </summary>
        public bool BlockDowngrade { get; set; } = false;
    }

    /// <summary>
    /// Configuration for sync conflict detection
    /// Ze_SyncConflictDetection
    /// </summary>
    public class SyncConflictConfig : EventProtectionConfig
    {
        /// <summary>
        /// Enable SignalR-based sync conflict detection
        /// </summary>
        public bool EnableSyncCoordination { get; set; } = true;

        /// <summary>
        /// Timeout in seconds before sync lock expires
        /// </summary>
        public int SyncLockTimeoutSeconds { get; set; } = 300; // 5 minutes

        /// <summary>
        /// Show notification when another user starts syncing
        /// </summary>
        public bool ShowSyncNotifications { get; set; } = true;
    }

    /// <summary>
    /// Configuration for CAD import protection
    /// Ze_CADImportProtection
    /// </summary>
    public class CADImportConfig : EventProtectionConfig
    {
        /// <summary>
        /// Block all CAD imports (strict mode)
        /// </summary>
        public bool BlockAllImports { get; set; } = false;

        /// <summary>
        /// Allowed file extensions for import (e.g., ".dwg", ".dxf")
        /// Empty list = all CAD types blocked when protection enabled
        /// </summary>
        public List<string> AllowedExtensions { get; set; } = new List<string>();

        /// <summary>
        /// Suggest linking instead of importing
        /// </summary>
        public bool SuggestLinkInstead { get; set; } = true;

        /// <summary>
        /// Maximum file size in MB allowed for import (0 = no limit)
        /// </summary>
        public int MaxFileSizeMB { get; set; } = 0;
    }

    /// <summary>
    /// Configuration for CAD explode protection
    /// Ze_CADExplodeProtection
    /// </summary>
    public class CADExplodeConfig : EventProtectionConfig
    {
        /// <summary>
        /// Block full explode of CAD instances
        /// </summary>
        public bool BlockFullExplode { get; set; } = true;

        /// <summary>
        /// Block partial explode of CAD instances
        /// </summary>
        public bool BlockPartialExplode { get; set; } = true;

        /// <summary>
        /// Allow explode for specific categories only
        /// Empty list = all explodes blocked when protection enabled
        /// </summary>
        public List<string> AllowedCategories { get; set; } = new List<string>();
    }

    /// <summary>
    /// Configuration for Copy/Monitor pin protection
    /// Ze_CopyMonitorPinProtection
    /// </summary>
    public class CopyMonitorPinConfig : EventProtectionConfig
    {
        /// <summary>
        /// Automatically prompt to pin after Copy/Monitor operation
        /// </summary>
        public bool PromptToPinAfterCopyMonitor { get; set; } = true;

        /// <summary>
        /// Automatically pin without prompting (if enabled, skips dialog)
        /// </summary>
        public bool AutoPinWithoutPrompt { get; set; } = false;

        /// <summary>
        /// Also apply protection to pinned elements (Extensible Storage)
        /// </summary>
        public bool ApplyProtectionToPinnedElements { get; set; } = true;

        /// <summary>
        /// Categories to include for pin protection
        /// Empty list = all Copy/Monitored elements
        /// </summary>
        public List<string> IncludedCategories { get; set; } = new List<string>();
    }

    /// <summary>
    /// Configuration for Transfer Project Standards protection
    /// Ze_TransferProjectStandardsProtection
    /// </summary>
    public class TransferProjectStandardsConfig : EventProtectionConfig
    {
        /// <summary>
        /// Automatically verify project registration after transfer
        /// </summary>
        public bool VerifyProjectRegistration { get; set; } = true;

        /// <summary>
        /// Reset project cookie if corrupted by transfer
        /// </summary>
        public bool AutoResetProjectCookie { get; set; } = true;

        /// <summary>
        /// Notify user when project registration is fixed
        /// </summary>
        public bool NotifyOnRegistrationFix { get; set; } = true;

        /// <summary>
        /// Log transfer events for audit trail
        /// </summary>
        public bool LogTransferEvents { get; set; } = true;
    }

    /// <summary>
    /// Configuration for CAD Import Pin Prompt (post-import)
    /// Ze_CADImportPinPrompt
    /// </summary>
    public class CADImportPinPromptConfig : EventProtectionConfig
    {
        /// <summary>
        /// Prompt to pin after CAD import
        /// </summary>
        public bool PromptToPinAfterImport { get; set; } = true;

        /// <summary>
        /// Automatically pin without prompting
        /// </summary>
        public bool AutoPinWithoutPrompt { get; set; } = false;

        /// <summary>
        /// Also apply protection to pinned CAD (Extensible Storage)
        /// </summary>
        public bool ApplyProtectionToPinnedCAD { get; set; } = true;

        /// <summary>
        /// Include linked CAD in pin prompt (not just imported)
        /// </summary>
        public bool IncludeLinkedCAD { get; set; } = false;
    }

    /// <summary>
    /// Configuration for Revit Link Pin Prompt (post-insert)
    /// Ze_RVTLinkPinPrompt
    /// </summary>
    public class RvtLinkPinPromptConfig : EventProtectionConfig
    {
        /// <summary>
        /// Prompt to pin after Revit link insertion
        /// </summary>
        public bool PromptToPinAfterInsert { get; set; } = true;

        /// <summary>
        /// Automatically pin without prompting
        /// </summary>
        public bool AutoPinWithoutPrompt { get; set; } = false;
    }

    /// <summary>
    /// Configuration for document printing protection
    /// Ze_DocumentPrintingProtection
    /// </summary>
    public class DocumentPrintingConfig : EventProtectionConfig
    {
        /// <summary>
        /// Block all printing operations
        /// </summary>
        public bool BlockAllPrinting { get; set; } = false;

        /// <summary>
        /// Require approval specifically for PDF output
        /// </summary>
        public bool RequireApprovalForPdf { get; set; } = false;
    }

    /// <summary>
    /// Configuration for document exporting protection
    /// Ze_DocumentExportingProtection
    /// </summary>
    public class DocumentExportingConfig : EventProtectionConfig
    {
        /// <summary>
        /// Block all export operations
        /// </summary>
        public bool BlockAllExports { get; set; } = false;

        /// <summary>
        /// Blocked export format names (e.g., "IFC", "NWC", "DWG")
        /// Empty list = no format-specific blocking
        /// </summary>
        public List<string> BlockedFormats { get; set; } = new List<string>();

        /// <summary>
        /// Allowed export format names (when non-empty, only these formats are permitted)
        /// Empty list = all formats allowed (unless BlockAllExports is true)
        /// </summary>
        public List<string> AllowedFormats { get; set; } = new List<string>();
    }

    /// <summary>
    /// Configuration for duplicate user session detection
    /// Ze_DuplicateUserSessionProtection
    /// </summary>
    public class DuplicateUserSessionConfig : EventProtectionConfig
    {
        /// <summary>
        /// Block opening when same Revit username is active on another machine for the same model
        /// </summary>
        public bool BlockDuplicateUsername { get; set; } = true;

        /// <summary>
        /// Also check API for cross-machine sessions (requires network connectivity)
        /// </summary>
        public bool CheckApiForRemoteSessions { get; set; } = true;
    }

    // NOTE: Sync Control configurations (SyncQueueConfig, BackgroundSyncConfig, BackgroundRelinquishConfig, IdleSyncConfig)
    // are NOT event protections. They are part of the Sync Control Module with separate feature flags.
    // See BIManage/Core/SyncControl/ for those configurations.
}
