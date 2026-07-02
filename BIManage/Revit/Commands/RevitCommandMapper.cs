using System;
using System.Collections.Generic;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.UI;

namespace BIManage.Revit.Commands
{
    /// <summary>
    ///     Maps core commands to Revit command IDs with version-specific handling
    ///     Reference: BIManage/Common/Helpers/Postable Command IDs.xlsx
    /// </summary>
    public static class RevitCommandMapper
    {
        // Core command IDs - hardcoded monitoring
        internal const string DUPLICATE_COMMAND_ID = "ID_SYM_CLONE";
        internal const string RENAME_COMMAND_ID = "ID_PRJBROWSER_RENAME";
        internal const string PRJBROWSER_DELETE_ID = "ID_PRJBROWSER_DELETE";
        internal const string SCHEDULE_DELETE_ROW_ID = "ID_DELETE_ROWS";
        internal const string WALLOPENING_MODIFY_ID = "ID_CREATE_WALL_OPENING";
        internal const string FULL_EXPLODE_CONTEXTMENU_ID = "ID_IMPORT_INSTANCE_EXPLODE";
        internal const string PARTIAL_EXPLODE_CONTEXTMENU_ID = "ID_IMPORT_INST_PARTIAL_EXPLODE";
        internal const string SYNC_COMMAND_ID = "ID_FILE_SAVE_TO_CENTRAL";
        internal const string SYNC_COMMAND_ID_SHORTCUT = "ID_FILE_SAVE_TO_CENTRAL_SHORTCUT";
        internal const string DRAG_ELEMENTS_ON_SELECTION_COMMAND_ID = "ID_TOGGLE_ALLOW_DRAG_ON_SELECTION";

        /// <summary>
        ///     Get all core command IDs for the current Revit version (comprehensive monitoring)
        ///     Registers 50+ commands across all categories: Critical, Modification, Protection, Sync, View, etc.
        /// </summary>
        public static IEnumerable<RevitCommandId> GetCoreCommands(UIApplication uiApplication)
        {
            if (uiApplication == null) throw new ArgumentNullException(nameof(uiApplication));

            var revitVersion = GetRevitVersion(uiApplication.Application);
            var commands = new List<RevitCommandId>();

            // ============================================
            // CRITICAL COMMANDS (Destructive)
            // ============================================
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.Delete));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EDIT_CUT"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.Demolish));
            // DeleteFromProject is command-string based
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_DELETE_FROM_PROJECT"));

            // ============================================
            // MODIFICATION COMMANDS
            // ============================================
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.Move));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.Rotate));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.MirrorPickAxis));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.MirrorDrawAxis));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.Copy));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.PasteFromClipboard));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.Align));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.Array));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_OBJECTS_ARRAY_RADIAL"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.Scale));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.SplitElement));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.TrimOrExtendMultipleElements));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.Offset));

            // ============================================
            // PROTECTION COMMANDS
            // ============================================
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_LOCK_ELEMENTS"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_UNLOCK_ELEMENTS"));

            // ============================================
            // SYNC/COLLABORATE COMMANDS
            // ============================================
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId(SYNC_COMMAND_ID));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId(SYNC_COMMAND_ID_SHORTCUT));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.ReloadLatest));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_RELINQUISH_ALL_MINE"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.Worksets));

            // ============================================
            // VIEW COMMANDS
            // ============================================
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_CLOSE_HIDDEN_WINDOWS"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.CloseInactiveViews));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.DuplicateView));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_APPLY_VIEW_TEMPLATE"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_CREATE_VIEW_TEMPLATE"));

            // ============================================
            // PARAMETER COMMANDS
            // ============================================
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EDIT_TYPE"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.TypeProperties));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_PROPERTIES"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_FAMILY_EDIT"));

            // ============================================
            // ELEMENT EDITING COMMANDS
            // ============================================
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EDIT_FAMILY_IN_PLACE"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EDIT_PROFILE"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EDIT_BOUNDARY"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EDIT_PATH"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_PICK_NEW_WORKPLANE"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_PICK_NEW_HOST"));

            // ============================================
            // GROUP COMMANDS
            // ============================================
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.CreateGroup));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_UNGROUP"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EDIT_GROUP"));

            // ============================================
            // LEVEL/GRID COMMANDS
            // ============================================
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.Level));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.Grid));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.ReferencePlane));

            // ============================================
            // PHASE & DESIGN OPTIONS
            // ============================================
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.Phases));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.DesignOptions));

            // ============================================
            // SHEET COMMANDS
            // ============================================
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.NewSheet));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_PLACE_VIEWS_ON_SHEET"));

            // ============================================
            // FILE OPERATIONS
            // ============================================
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.Save));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_FILE_SAVE_AS"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_REVIT_FILE_SAVE_AS"));
            // Save As variants (Ze_DocumentSaveAsProtection) — confirmed in Revit journal
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_REVIT_FILE_SAVE_AS_CLOUD_MODEL"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_REVIT_SAVE_AS_FAMILY"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_SAVE_FAMILY_ANY"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_REVIT_SAVE_AS_TEMPLATE"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_SAVE_GROUP"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_SAVE_VIEWS_TO_FILE"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_FILE_EXPORT"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_IFC"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_DWG"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_DXF"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_DGN"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_FBX"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_STL"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_DWF"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_OBJ"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_NWC"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_SAT"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_GBXML"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_ODBC"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_IMAGES"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_FAMILY_TYPES"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_REPORT"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_FILE_IMPORT"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_IMPORT_CAD"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_RVTLINK_IMPORT_CAD"));
            // Print commands (Ze_DocumentPrintingProtection)
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_REVIT_FILE_PRINT"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_REVIT_FILE_PRINT_SETUP"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_BATCH_PRINT"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_PDF"));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId("ID_EXPORT_PDF_IN_PRINT"));

            // ============================================
            // ADDITIONAL COMMANDS
            // ============================================
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId(DUPLICATE_COMMAND_ID));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId(RENAME_COMMAND_ID));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId(PRJBROWSER_DELETE_ID));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId(SCHEDULE_DELETE_ROW_ID));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId(WALLOPENING_MODIFY_ID));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId(FULL_EXPLODE_CONTEXTMENU_ID));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId(PARTIAL_EXPLODE_CONTEXTMENU_ID));
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupCommandId(DRAG_ELEMENTS_ON_SELECTION_COMMAND_ID));

            // ============================================
            // EVENT PROTECTION COMMANDS
            // ============================================
            AddCommandIfAvailable(commands, () => RevitCommandId.LookupPostableCommandId(PostableCommand.TransferProjectStandards));

            return commands;
        }

        /// <summary>
        ///     Get Revit major version number
        /// </summary>
        private static int GetRevitVersion(Application application)
        {
            var versionString = application.VersionNumber;
            if (int.TryParse(versionString, out var version))
            {
                return version;
            }
            return 2021; // Default fallback
        }

        /// <summary>
        ///     Add command to list if it exists (null-safe)
        /// </summary>
        private static void AddCommandIfAvailable(List<RevitCommandId> commands, Func<RevitCommandId?> commandFactory)
        {
            try
            {
                var commandId = commandFactory();
                if (commandId != null)
                {
                    commands.Add(commandId);
                }
            }
            catch
            {
                // Command not available in this version - skip silently
            }
        }

        /// <summary>
        ///     Get friendly command name for logging
        /// </summary>
        public static string GetCommandDisplayName(RevitCommandId commandId)
        {
            if (commandId == null) return "Unknown";

            var name = commandId.Name;

            // Map internal IDs to friendly names
            return name switch
            {
                "ID_EDIT_DELETE" => "Delete",
                "ID_FILE_SAVE_TO_CENTRAL" => "Sync to Central",
                "ID_FILE_SAVE_TO_CENTRAL_SHORTCUT" => "Sync to Central (Shortcut)",
                "ID_OBJECTS_MIRROR_PICK" => "Mirror (Pick Axis)",
                "ID_OBJECTS_MIRROR" => "Mirror (Draw Axis)",
                "ID_EDIT_COPY" => "Copy",
                "ID_EDIT_CLIPBOARD_PASTE" => "Paste from Clipboard",
                "ID_LOCK_ELEMENTS" => "Pin",
                "ID_UNLOCK_ELEMENTS" => "Unpin",
                "ID_SYM_CLONE" => "Duplicate",
                "ID_PRJBROWSER_RENAME" => "Rename (Project Browser)",
                "ID_PRJBROWSER_DELETE" => "Delete (Project Browser)",
                "ID_DELETE_ROWS" => "Delete Rows (Schedule)",
                "ID_CREATE_WALL_OPENING" => "Create/Modify Wall Opening",
                "ID_IMPORT_INSTANCE_EXPLODE" => "Full Explode",
                "ID_IMPORT_INST_PARTIAL_EXPLODE" => "Partial Explode",
                "ID_TOGGLE_ALLOW_DRAG_ON_SELECTION" => "Toggle Drag on Selection",
                "ID_TRANSFER_PROJECT_STANDARDS" => "Transfer Project Standards",
                _ => name
            };
        }
    }
}
