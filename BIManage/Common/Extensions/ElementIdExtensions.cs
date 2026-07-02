using Autodesk.Revit.DB;

namespace BIManage.Common.Helpers
{
    /// <summary>
    /// Extension methods for ElementId to handle API changes between Revit versions
    /// </summary>
    public static class ElementIdExtensions
    {
        /// <summary>
        /// Gets the integer value of an ElementId in a version-compatible way.
        /// Uses IntegerValue for Revit 2021-2025, Value for Revit 2026+
        /// </summary>
        /// <param name="elementId">The ElementId to get the value from</param>
        /// <returns>The element ID as a long (Int64)</returns>
        public static long GetIdValue(this ElementId elementId)
        {
            if (elementId == null)
                return -1;

#if REVIT2025_OR_GREATER
            // Revit 2025+: IntegerValue removed, use Value (Int64)
            return elementId.Value;
#else
            // Revit 2021-2024: Use IntegerValue (Int32), cast to Int64
            return elementId.IntegerValue;
#endif
        }

        /// <summary>
        /// Gets the integer value as Int32 for backward compatibility with database schemas.
        /// Safe to use when ElementId values are known to be within Int32 range (e.g., category IDs)
        /// </summary>
        /// <param name="elementId">The ElementId to get the value from</param>
        /// <returns>The element ID as an int (Int32)</returns>
        public static int GetIdValueAsInt32(this ElementId elementId)
        {
            if (elementId == null)
                return -1;

#if REVIT2025_OR_GREATER
            // Revit 2025+: IntegerValue removed, use Value (Int64) and cast to Int32
            // This is safe for category IDs and most built-in element IDs
            return (int)elementId.Value;
#else
            // Revit 2021-2024: Use IntegerValue (Int32) directly
            return elementId.IntegerValue;
#endif
        }

        /// <summary>
        /// Creates an ElementId from a long value in a version-compatible way.
        /// Revit 2025+ accepts long directly; Revit 2021-2024 requires int cast.
        /// </summary>
        public static ElementId CreateElementId(long idValue)
        {
#if REVIT2025_OR_GREATER
            return new ElementId(idValue);
#else
            return new ElementId((int)idValue);
#endif
        }
    }
}
