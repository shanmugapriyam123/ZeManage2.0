using System;

namespace BIManage.Revit.Commands
{
    /// <summary>
    ///     Master command catalog entry
    ///     Phase 3: Stored in SQLite database
    ///     Tracks Revit version availability per command
    /// </summary>
    public class MasterCommandInfo
    {
        public MasterCommandInfo(
            string commandCode,
            string displayName,
            int? postableCommandEnumValue,
            string? minRevitVersion = null,
            string? maxRevitVersion = null,
            string? deprecatedInAppVersion = null)
        {
            CommandCode = commandCode;
            DisplayName = displayName;
            PostableCommandEnumValue = postableCommandEnumValue;
            MinRevitVersion = minRevitVersion;
            MaxRevitVersion = maxRevitVersion;
            DeprecatedInAppVersion = deprecatedInAppVersion;
        }

        /// <summary>
        ///     Unique command code (e.g., "ID_EDIT_DELETE", "DELETE_CMD")
        /// </summary>
        public string CommandCode { get; set; }

        /// <summary>
        ///     Human-readable command name (e.g., "Delete", "Sync to Central")
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        ///     PostableCommand enum value (null if command uses string ID)
        ///     Example: 92 = PostableCommand.Delete
        /// </summary>
        public int? PostableCommandEnumValue { get; set; }

        /// <summary>
        ///     Minimum Revit version where this command is available (e.g., "2021")
        ///     Null = available in all versions
        /// </summary>
        public string? MinRevitVersion { get; set; }

        /// <summary>
        ///     Maximum Revit version where this command is available (e.g., "2024")
        ///     Null = available in all future versions
        /// </summary>
        public string? MaxRevitVersion { get; set; }

        /// <summary>
        ///     BIManage app version where this command was deprecated
        ///     Example: "1.2.0" means command is hidden in v1.2.0 and later
        ///     Null = not deprecated
        /// </summary>
        public string? DeprecatedInAppVersion { get; set; }

        /// <summary>
        ///     Check if command is available in the current Revit version
        /// </summary>
        public bool IsAvailableInRevitVersion(int revitVersion)
        {
            // Check minimum version
            if (MinRevitVersion != null)
            {
                if (!int.TryParse(MinRevitVersion, out var minVersion))
                    return false;

                if (revitVersion < minVersion)
                    return false;
            }

            // Check maximum version
            if (MaxRevitVersion != null)
            {
                if (!int.TryParse(MaxRevitVersion, out var maxVersion))
                    return false;

                if (revitVersion > maxVersion)
                    return false;
            }

            return true;
        }

        /// <summary>
        ///     Check if command is deprecated in current BIManage version
        ///     Compare app version against DeprecatedInAppVersion
        /// </summary>
        public bool IsDeprecated(string currentAppVersion)
        {
            if (string.IsNullOrEmpty(DeprecatedInAppVersion))
                return false;

            // Simple version comparison (e.g., "1.2.0" vs "1.3.0")
            // For production: Use System.Version for proper comparison
            return string.Compare(currentAppVersion, DeprecatedInAppVersion, StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        ///     Create a core command entry (always available, never deprecated)
        /// </summary>
        public static MasterCommandInfo CreateCoreCommand(string commandCode, string displayName, int postableCommandEnumValue)
        {
            return new MasterCommandInfo(commandCode, displayName, postableCommandEnumValue);
        }

        /// <summary>
        ///     Create a version-specific command entry
        /// </summary>
        public static MasterCommandInfo CreateVersionSpecific(
            string commandCode,
            string displayName,
            int? postableCommandEnumValue,
            string minRevitVersion,
            string? maxRevitVersion = null)
        {
            return new MasterCommandInfo(
                commandCode,
                displayName,
                postableCommandEnumValue,
                minRevitVersion,
                maxRevitVersion);
        }
    }
}
