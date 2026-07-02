using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BIManage.Data.SQLite;

namespace BIManage.Revit.Commands
{
    /// <summary>
    /// Shared utility for resolving command display names to PostableCommand integer IDs.
    /// Single source of truth for name-to-ID mapping, consolidating PostableCommands constants
    /// and Revit internal command ID variants.
    /// </summary>
    public static class CommandNameResolver
    {
        private static readonly Dictionary<string, int> _nameToId;
        private static readonly Dictionary<int, string> _idToName;

        static CommandNameResolver()
        {
            // Primary display-name-to-ID mapping (case-insensitive)
            _nameToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                // Critical (Destructive)
                { "Delete", PostableCommands.Delete },
                { "Cut", PostableCommands.Cut },
                { "Demolish", PostableCommands.Demolish },
                { "DeleteFromProject", PostableCommands.DeleteFromProject },

                // Modification
                { "Move", PostableCommands.Move },
                { "Rotate", PostableCommands.Rotate },
                { "Mirror", PostableCommands.Mirror },
                { "MirrorProject", PostableCommands.MirrorProject },
                { "Copy", PostableCommands.Copy },
                { "Align", PostableCommands.Align },
                { "Array", PostableCommands.Array },
                { "ArrayRadial", PostableCommands.ArrayRadial },
                { "Scale", PostableCommands.Scale },
                { "Split", PostableCommands.Split },
                { "TrimExtend", PostableCommands.TrimExtend },
                { "Offset", PostableCommands.Offset },

                // Protection
                { "Pin", PostableCommands.Pin },
                { "Unpin", PostableCommands.Unpin },

                // Sync/Collaborate
                { "SyncWithCentral", PostableCommands.SyncWithCentral },
                { "Synchronize", PostableCommands.SyncWithCentral },
                { "ReloadLatest", PostableCommands.ReloadLatest },
                { "RelinquishAllMine", PostableCommands.RelinquishAllMine },
                { "SaveToCentral", PostableCommands.SaveToCentral },
                { "Worksets", PostableCommands.Worksets },

                // View
                { "CloseHiddenWindows", PostableCommands.CloseHiddenWindows },
                { "CloseInactiveViews", PostableCommands.CloseInactiveViews },
                { "DuplicateView", PostableCommands.DuplicateView },
                { "ApplyViewTemplate", PostableCommands.ApplyViewTemplate },
                { "CreateViewTemplate", PostableCommands.CreateViewTemplate },

                // Parameter
                { "EditType", PostableCommands.EditType },
                { "TypeProperties", PostableCommands.TypeProperties },
                { "InstanceProperties", PostableCommands.InstanceProperties },
                { "FamilyEditor", PostableCommands.FamilyEditor },

                // Element Editing
                { "EditElement", PostableCommands.EditElement },
                { "EditProfile", PostableCommands.EditProfile },
                { "EditBoundary", PostableCommands.EditBoundary },
                { "EditPath", PostableCommands.EditPath },
                { "EditWorkPlane", PostableCommands.EditWorkPlane },
                { "PickNewHost", PostableCommands.PickNewHost },

                // Group
                { "Group", PostableCommands.Group },
                { "Ungroup", PostableCommands.Ungroup },
                { "EditGroup", PostableCommands.EditGroup },

                // Level/Grid
                { "Level", PostableCommands.Level },
                { "Grid", PostableCommands.Grid },
                { "ReferencePlane", PostableCommands.ReferencePlane },

                // Phase
                { "Phases", PostableCommands.Phases },

                // Design Options
                { "DesignOptions", PostableCommands.DesignOptions },

                // Sheet
                { "NewSheet", PostableCommands.NewSheet },
                { "PlaceView", PostableCommands.PlaceView },

                // File
                { "Save", PostableCommands.Save },
                { "SaveAs", PostableCommands.SaveAs },
                { "Export", PostableCommands.Export },
                { "Import", PostableCommands.Import },
            };

            // Build reverse mapping (ID → primary display name)
            _idToName = new Dictionary<int, string>();
            foreach (var kvp in _nameToId)
            {
                // First entry for each ID wins as the canonical name
                if (!_idToName.ContainsKey(kvp.Value))
                {
                    _idToName[kvp.Value] = kvp.Key;
                }
            }

            // Add Revit internal command ID variants (sourced from Postable Command IDs.xlsx).
            // These won't appear in _idToName but allow resolution from Revit internal names.

            // Critical (Destructive)
            AddVariant("ID_BUTTON_DELETE", PostableCommands.Delete);
            AddVariant("ID_EDIT_DELETE", PostableCommands.Delete);
            AddVariant("ID_EDIT_CUT", PostableCommands.Cut);
            AddVariant("CutToClipboard", PostableCommands.Cut);     // PostableCommand enum name — used when LookupCommandId("ID_EDIT_CUT") falls back to LookupPostableCommandId
            AddVariant("ID_EDIT_DEMOLISH", PostableCommands.Demolish);
            AddVariant("ID_EDIT_DELETE_FROM_PROJECT", PostableCommands.DeleteFromProject);

            // Modification
            AddVariant("ID_EDIT_MOVE", PostableCommands.Move);
            AddVariant("ID_OBJECTS_MOVE", PostableCommands.Move);
            AddVariant("ID_EDIT_ROTATE", PostableCommands.Rotate);
            AddVariant("ID_OBJECTS_ROTATE", PostableCommands.Rotate);
            AddVariant("ID_EDIT_MIRROR", PostableCommands.Mirror);
            AddVariant("ID_EDIT_MIRROR_LINE", PostableCommands.Mirror);        // MirrorDrawAxis — both variants trigger Mirror rules
            AddVariant("ID_MIRROR_PROJECT", PostableCommands.MirrorProject);
            AddVariant("ID_OBJECTS_MIRROR_DRAW", PostableCommands.MirrorProject);
            AddVariant("ID_EDIT_MOVE_COPY", PostableCommands.Copy);
            AddVariant("ID_ALIGN", PostableCommands.Align);
            AddVariant("ID_EDIT_CREATE_PATTERN", PostableCommands.Array);
            AddVariant("ID_EDIT_SCALE", PostableCommands.Scale);
            AddVariant("ID_OBJECTS_SCALE", PostableCommands.Scale);
            AddVariant("ID_OFFSET", PostableCommands.Offset);
            AddVariant("ID_EDIT_OFFSET", PostableCommands.Offset);
            AddVariant("ID_OBJECTS_OFFSET", PostableCommands.Offset);
            AddVariant("ID_SPLIT", PostableCommands.Split);
            AddVariant("ID_SPLIT_WITH_GAP", PostableCommands.Split);
            AddVariant("ID_TRIM_EXTEND_CORNER", PostableCommands.TrimExtend);
            AddVariant("ID_TRIM_EXTEND_SINGLE", PostableCommands.TrimExtend);
            AddVariant("ID_TRIM_EXTEND_MULTIPLE", PostableCommands.TrimExtend);

            // Protection
            AddVariant("ID_LOCK_ELEMENTS", PostableCommands.Pin);
            AddVariant("ID_UNLOCK_ELEMENTS", PostableCommands.Unpin);

            // Sync / collaborate
            AddVariant("ID_FILE_SAVE_TO_CENTRAL", PostableCommands.SyncWithCentral);
            AddVariant("ID_FILE_SAVE_TO_CENTRAL_SHORTCUT", PostableCommands.SyncWithCentral);
            AddVariant("ID_FILE_SAVE_TO_MASTER", PostableCommands.SyncWithCentral);
            AddVariant("ID_WORKSETS_RELOAD_LATEST", PostableCommands.ReloadLatest);
            AddVariant("ID_RELINQUISH_ALL_MINE", PostableCommands.RelinquishAllMine);
            AddVariant("ID_SETTINGS_PARTITIONS", PostableCommands.Worksets);

            // View
            AddVariant("ID_PRJBROWSER_COPY", PostableCommands.DuplicateView);
            AddVariant("ID_DUPLICATE_WITH_DETAILING", PostableCommands.DuplicateView);
            AddVariant("ID_APPLY_VIEW_TEMPLATE", PostableCommands.ApplyViewTemplate);
            AddVariant("ID_CREATE_VIEW_TEMPLATE", PostableCommands.CreateViewTemplate);
            AddVariant("ID_CLOSE_HIDDEN_WINDOWS", PostableCommands.CloseHiddenWindows);
            AddVariant("ID_VIEW_CLOSE_INACTIVE", PostableCommands.CloseInactiveViews);

            // Parameter / type
            AddVariant("ID_EDIT_TYPE", PostableCommands.EditType);
            AddVariant("ID_EDIT_ELEMENT_TYPE", PostableCommands.EditType);
            AddVariant("ID_ELEMENT_PROPERTIES", PostableCommands.InstanceProperties);
            AddVariant("ID_PROPERTIES", PostableCommands.InstanceProperties);
            AddVariant("ID_FAMILY_EDITOR", PostableCommands.FamilyEditor);

            // Element editing
            AddVariant("ID_EDIT_ELEMENT", PostableCommands.EditElement);
            AddVariant("ID_EDIT_PROFILE", PostableCommands.EditProfile);
            AddVariant("ID_EDIT_BOUNDARY", PostableCommands.EditBoundary);
            AddVariant("ID_EDIT_PATH", PostableCommands.EditPath);
            AddVariant("ID_EDIT_WORKPLANE", PostableCommands.EditWorkPlane);
            AddVariant("ID_PICK_NEW_HOST", PostableCommands.PickNewHost);

            // Group
            AddVariant("ID_EDIT_GROUP", PostableCommands.Group);
            AddVariant("ID_UNGROUP", PostableCommands.Ungroup);
            AddVariant("ID_GROUPS_EDIT", PostableCommands.EditGroup);
            AddVariant("ID_GROUP_EDIT", PostableCommands.EditGroup);

            // Level / grid / reference plane
            AddVariant("ID_OBJECTS_LEVEL", PostableCommands.Level);
            AddVariant("ID_OBJECTS_GRID", PostableCommands.Grid);
            AddVariant("ID_OBJECTS_CLINE", PostableCommands.ReferencePlane);

            // Phase / design options
            AddVariant("ID_SETTINGS_PHASES", PostableCommands.Phases);
            AddVariant("ID_EDIT_DESIGNOPTIONS", PostableCommands.DesignOptions);

            // Sheet
            AddVariant("ID_VIEW_NEW_SHEET", PostableCommands.NewSheet);
            AddVariant("ID_VIEW_PLACE_VIEW", PostableCommands.PlaceView);

            // File
            AddVariant("ID_REVIT_FILE_SAVE", PostableCommands.Save);
            AddVariant("ID_REVIT_FILE_SAVE_AS", PostableCommands.SaveAs);
            AddVariant("ID_FILE_EXPORT", PostableCommands.Export);
            AddVariant("ID_FILE_IMPORT", PostableCommands.Import);
        }

        private static void AddVariant(string name, int id)
        {
            if (!_nameToId.ContainsKey(name))
                _nameToId[name] = id;
        }

        /// <summary>
        /// Resolve a command display name or Revit internal ID to its integer command ID.
        /// Returns 0 if not found.
        /// </summary>
        public static int ResolveNameToId(string commandName)
        {
            if (string.IsNullOrEmpty(commandName))
                return 0;

            return _nameToId.TryGetValue(commandName, out var id) ? id : 0;
        }

        /// <summary>
        /// Resolve a command integer ID to its primary display name.
        /// Returns null if not found.
        /// </summary>
        public static string? ResolveIdToName(int commandId)
        {
            return _idToName.TryGetValue(commandId, out var name) ? name : null;
        }

        /// <summary>
        /// Resolve a Revit internal command ID (e.g. "ID_BUTTON_DELETE") to its friendly display name (e.g. "Delete").
        /// If the command is not recognized, returns the original name.
        /// </summary>
        public static string GetFriendlyName(string commandName)
        {
            if (string.IsNullOrEmpty(commandName))
                return commandName;

            var id = ResolveNameToId(commandName);
            if (id != 0)
            {
                var friendly = ResolveIdToName(id);
                if (!string.IsNullOrEmpty(friendly))
                    return friendly;
            }

            return commandName;
        }

        /// <summary>
        /// Check if a command name is recognized.
        /// </summary>
        public static bool IsKnownCommand(string commandName)
        {
            return !string.IsNullOrEmpty(commandName) && _nameToId.ContainsKey(commandName);
        }

        /// <summary>
        /// Get all commands as ID → display name dictionary.
        /// Useful for populating command selection UI.
        /// </summary>
        public static Dictionary<int, string> GetAllCommands()
        {
            return new Dictionary<int, string>(_idToName);
        }

        /// <summary>
        /// Loads additional command mappings from the command_cache SQLite table.
        /// Parses member_name_24 (e.g. "PostableCommand.AddLeader") to resolve PostableCommand enum → int ID.
        /// Hardcoded entries take priority — cache entries are only added if not already present.
        /// </summary>
        public static async Task<int> LoadFromCacheAsync(CacheRepository cache)
        {
            if (cache == null) return 0;

            var commands = await cache.GetAllCommandsFromCacheAsync();
            if (commands == null || commands.Count == 0) return 0;

            int loaded = 0;
            foreach (var (commandName, memberName, _, _, _, _) in commands)
            {
                if (string.IsNullOrEmpty(commandName) || _nameToId.ContainsKey(commandName))
                    continue;

                int id = ResolvePostableCommandId(memberName);
                if (id > 0)
                {
                    _nameToId[commandName] = id;
                    if (!_idToName.ContainsKey(id))
                        _idToName[id] = commandName;
                    loaded++;
                }
            }

            return loaded;
        }

        /// <summary>
        /// Parses a member_name_24 value (e.g. "PostableCommand.AddLeader") into a PostableCommand integer ID.
        /// Returns 0 if the member name is null, empty, or doesn't map to a valid PostableCommand enum value.
        /// </summary>
        private static int ResolvePostableCommandId(string? memberName)
        {
            if (string.IsNullOrEmpty(memberName))
                return 0;

            // Strip "PostableCommand." prefix if present
            const string prefix = "PostableCommand.";
            var enumName = memberName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? memberName.Substring(prefix.Length)
                : memberName;

            if (string.IsNullOrEmpty(enumName))
                return 0;

            try
            {
                if (Enum.TryParse<Autodesk.Revit.UI.PostableCommand>(enumName, true, out var result))
                    return (int)result;
            }
            catch
            {
                // Enum.TryParse can throw on some framework versions with invalid input
            }

            return 0;
        }
    }
}
