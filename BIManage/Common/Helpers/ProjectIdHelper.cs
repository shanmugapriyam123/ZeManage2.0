using Autodesk.Revit.DB;
using System;

namespace BIManage.Common.Helpers
{
    /// <summary>
    /// Helper class for consistent project ID generation across the application
    /// Ensures write and read paths use the same project ID format
    /// </summary>
    public static class ProjectIdHelper
    {
        /// <summary>
        /// Get consistent project ID for a document
        /// Priority: WorksharingCentralModelPath GUID > Document Title
        /// </summary>
        /// <param name="doc">Revit document</param>
        /// <returns>Consistent project ID string</returns>
        public static string GetProjectId(Document doc)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            // For workshared models, use central model GUID (most stable)
            if (doc.IsWorkshared)
            {
                var centralPath = doc.GetWorksharingCentralModelPath();
                if (centralPath != null)
                {
                    var guid = centralPath.GetModelGUID();
                    return guid.ToString();
                }
            }

            // Fallback to document title for non-workshared or if central path unavailable
            return doc.Title;
        }

        /// <summary>
        /// Get project name for display purposes
        /// </summary>
        /// <param name="doc">Revit document</param>
        /// <returns>Human-readable project name</returns>
        public static string GetProjectName(Document doc)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            return doc.Title;
        }

        /// <summary>
        /// Get model GUID if available (for workshared models)
        /// </summary>
        /// <param name="doc">Revit document</param>
        /// <returns>Model GUID string or null if not workshared</returns>
        public static string? GetModelGuid(Document doc)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            if (doc.IsWorkshared)
            {
                var centralPath = doc.GetWorksharingCentralModelPath();
                if (centralPath != null)
                {
                    var guid = centralPath.GetModelGUID();
                    return guid.ToString();
                }
            }

            return null;
        }
    }
}
