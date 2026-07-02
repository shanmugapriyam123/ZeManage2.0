using Autodesk.Revit.UI;

namespace BIManage.Revit.Commands
{
    /// <summary>
    /// Command mappings - 50+ critical commands to monitor
    /// Comprehensive command protection
    /// </summary>
    public static class PostableCommands
    {
        // ============================================
        // CRITICAL COMMANDS (Destructive)
        // ============================================

        /// <summary>Delete selected elements</summary>
        public const int Delete = 32778;

        /// <summary>Cut elements to clipboard</summary>
        public const int Cut = 32779;

        /// <summary>Demolish elements</summary>
        public const int Demolish = 33310;

        /// <summary>Delete from project</summary>
        public const int DeleteFromProject = 33311;

        // ============================================
        // MODIFICATION COMMANDS
        // ============================================

        /// <summary>Mirror - Pick Axis</summary>
        public const int Mirror = 32936;

        /// <summary>Mirror - Draw Axis</summary>
        public const int MirrorProject = 32937;

        /// <summary>Copy elements</summary>
        public const int Copy = 33129;

        /// <summary>Move elements</summary>
        public const int Move = 33066;

        /// <summary>Rotate elements</summary>
        public const int Rotate = 33068;

        /// <summary>Align elements</summary>
        public const int Align = 32771;

        /// <summary>Array - Linear</summary>
        public const int Array = 32772;

        /// <summary>Array - Radial</summary>
        public const int ArrayRadial = 32773;

        /// <summary>Scale elements</summary>
        public const int Scale = 33070;

        /// <summary>Split Element</summary>
        public const int Split = 33074;

        /// <summary>Trim/Extend to Corner</summary>
        public const int TrimExtend = 33078;

        /// <summary>Offset</summary>
        public const int Offset = 33060;

        // ============================================
        // PROTECTION COMMANDS
        // ============================================

        /// <summary>Pin elements (Lock)</summary>
        public const int Pin = 32997;

        /// <summary>Unpin elements (Unlock)</summary>
        public const int Unpin = 33001;

        // ============================================
        // SYNC/COLLABORATE COMMANDS
        // ============================================

        /// <summary>Synchronize with Central</summary>
        public const int SyncWithCentral = 33308;

        /// <summary>Reload Latest</summary>
        public const int ReloadLatest = 33293;

        /// <summary>Relinquish All Mine</summary>
        public const int RelinquishAllMine = 33294;

        /// <summary>Save to Central</summary>
        public const int SaveToCentral = 33301;
        
        /// <summary>Worksets dialog</summary>
        public const int Worksets = 33331;

        // ============================================
        // VIEW COMMANDS
        // ============================================

        /// <summary>Close Hidden Windows</summary>
        public const int CloseHiddenWindows = 32816;

        /// <summary>Close Inactive Views</summary>
        public const int CloseInactiveViews = 32817;

        /// <summary>Duplicate View</summary>
        public const int DuplicateView = 32850;

        /// <summary>Apply View Template</summary>
        public const int ApplyViewTemplate = 32781;

        /// <summary>Create View Template</summary>
        public const int CreateViewTemplate = 32829;

        // ============================================
        // PARAMETER COMMANDS
        // ============================================

        /// <summary>Edit Type</summary>
        public const int EditType = 32856;

        /// <summary>Type Properties</summary>
        public const int TypeProperties = 33090;

        /// <summary>Instance Properties</summary>
        public const int InstanceProperties = 32917;

        /// <summary>Family Editor</summary>
        public const int FamilyEditor = 32865;

        // ============================================
        // ELEMENT EDITING
        // ============================================

        /// <summary>Edit Element</summary>
        public const int EditElement = 32853;

        /// <summary>Edit Profile</summary>
        public const int EditProfile = 32857;

        /// <summary>Edit Boundary</summary>
        public const int EditBoundary = 32851;

        /// <summary>Edit Path</summary>
        public const int EditPath = 32858;

        /// <summary>Edit Work Plane</summary>
        public const int EditWorkPlane = 32861;

        /// <summary>Pick New Host</summary>
        public const int PickNewHost = 33004;

        // ============================================
        // GROUP COMMANDS
        // ============================================

        /// <summary>Create Group</summary>
        public const int Group = 33305;

        /// <summary>Ungroup</summary>
        public const int Ungroup = 33091;

        /// <summary>Edit Group</summary>
        public const int EditGroup = 32854;

        // ============================================
        // LEVEL/GRID COMMANDS
        // ============================================

        /// <summary>Create Level</summary>
        public const int Level = 32945;

        /// <summary>Create Grid</summary>
        public const int Grid = 32897;

        /// <summary>Create Reference Plane</summary>
        public const int ReferencePlane = 33017;

        // ============================================
        // PHASE COMMANDS
        // ============================================

        /// <summary>Phasing dialog</summary>
        public const int Phases = 33002;

        // ============================================
        // DESIGN OPTIONS
        // ============================================

        /// <summary>Design Options</summary>
        public const int DesignOptions = 32840;

        // ============================================
        // SHEET COMMANDS
        // ============================================

        /// <summary>New Sheet</summary>
        public const int NewSheet = 32971;

        /// <summary>Place View on Sheet</summary>
        public const int PlaceView = 33003;

        // ============================================
        // FILE OPERATIONS
        // ============================================

        /// <summary>Save</summary>
        public const int Save = 32791;

        /// <summary>Save As</summary>
        public const int SaveAs = 32792;

        /// <summary>Export</summary>
        public const int Export = 32862;

        /// <summary>Import</summary>
        public const int Import = 32915;

        // ============================================
        // Helper Methods
        // ============================================

        /// <summary>
        /// Get command name from PostableCommand enum
        /// </summary>
        public static string GetCommandName(PostableCommand command)
        {
            return command switch
            {
                PostableCommand.Delete => "Delete",
                PostableCommand.CutToClipboard => "Cut",
                PostableCommand.Copy => "Copy",
                PostableCommand.Move => "Move",
                PostableCommand.Rotate => "Rotate",
                PostableCommand.MirrorDrawAxis => "Mirror",
                PostableCommand.MirrorPickAxis => "Mirror",
                PostableCommand.Array => "Array",
                PostableCommand.Align => "Align",
                PostableCommand.SplitElement => "Split Element",
                PostableCommand.TrimOrExtendMultipleElements => "Trim/Extend",
                PostableCommand.TrimOrExtendSingleElement => "Trim/Extend",
                PostableCommand.TrimOrExtendToCorner => "Trim/Extend",
                PostableCommand.Offset => "Offset",
                PostableCommand.Scale => "Scale",
                _ => command.ToString()
            };
        }

        /// <summary>
        /// Check if command is destructive
        /// </summary>
        public static bool IsDestructiveCommand(int commandId)
        {
            return commandId == Delete ||
                   commandId == Cut ||
                   commandId == Demolish ||
                   commandId == DeleteFromProject;
        }

        /// <summary>
        /// Check if command modifies elements
        /// </summary>
        public static bool IsModificationCommand(int commandId)
        {
            return commandId == Move ||
                   commandId == Rotate ||
                   commandId == Mirror ||
                   commandId == MirrorProject ||
                   commandId == Copy ||
                   commandId == Align ||
                   commandId == Array ||
                   commandId == ArrayRadial ||
                   commandId == Scale ||
                   commandId == Split ||
                   commandId == TrimExtend ||
                   commandId == Offset;
        }

        /// <summary>
        /// Check if command is protection-related
        /// </summary>
        public static bool IsProtectionCommand(int commandId)
        {
            return commandId == Pin ||
                   commandId == Unpin;
        }

        /// <summary>
        /// Check if command is sync/collaborate related
        /// </summary>
        public static bool IsSyncCommand(int commandId)
        {
            return commandId == SyncWithCentral ||
                   commandId == ReloadLatest ||
                   commandId == RelinquishAllMine ||
                   commandId == SaveToCentral;
        }

        /// <summary>
        /// Get all critical command IDs (for default protection)
        /// </summary>
        public static int[] GetCriticalCommands()
        {
            return new[]
            {
                Delete, Cut, Demolish, DeleteFromProject
            };
        }

        /// <summary>
        /// Get all modification command IDs
        /// </summary>
        public static int[] GetModificationCommands()
        {
            return new[]
            {
                Move, Rotate, Mirror, MirrorProject, Copy, Align,
                Array, ArrayRadial, Scale, Split, TrimExtend, Offset
            };
        }

        /// <summary>
        /// Get all protection command IDs
        /// </summary>
        public static int[] GetProtectionCommands()
        {
            return new[]
            {
                Pin, Unpin
            };
        }

        /// <summary>
        /// Get all sync command IDs
        /// </summary>
        public static int[] GetSyncCommands()
        {
            return new[]
            {
                SyncWithCentral, ReloadLatest, RelinquishAllMine, SaveToCentral, Worksets
            };
        }

        /// <summary>
        /// Get all monitored command IDs (50+ commands)
        /// </summary>
        public static int[] GetAllMonitoredCommands()
        {
            return new[]
            {
                // Critical
                Delete, Cut, Demolish, DeleteFromProject,
                
                // Modification
                Move, Rotate, Mirror, MirrorProject, Copy, Align,
                Array, ArrayRadial, Scale, Split, TrimExtend, Offset,
                
                // Protection
                Pin, Unpin,
                
                // Sync
                SyncWithCentral, ReloadLatest, RelinquishAllMine, SaveToCentral, Worksets,
                
                // View
                CloseHiddenWindows, CloseInactiveViews, DuplicateView,
                ApplyViewTemplate, CreateViewTemplate,
                
                // Parameter
                EditType, TypeProperties, InstanceProperties, FamilyEditor,
                
                // Element Editing
                EditElement, EditProfile, EditBoundary, EditPath,
                EditWorkPlane, PickNewHost,
                
                // Group
                Group, Ungroup, EditGroup,
                
                // Level/Grid
                Level, Grid, ReferencePlane,
                
                // Phase
                Phases,
                
                // Design Options
                DesignOptions,
                
                // Sheet
                NewSheet, PlaceView,
                
                // File
                Save, SaveAs, Export, Import
            };
        }
    }
}