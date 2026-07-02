using System;
using Autodesk.Revit.DB;

namespace BIManage.Revit.Helpers
{
    /// <summary>
    /// Helper for extracting document metadata (project name, model location, etc.)
    /// </summary>
    public static class DocumentInformationHelper
    {
        /// <summary>
        /// Get normalized model location (forward slashes, lowercase)
        /// </summary>
        public static string GetNormalizedPath(Document doc)
        {
            if (doc == null) return null;

            try
            {
                var path = doc.PathName;
                if (string.IsNullOrEmpty(path)) return null;

                // Normalize: forward slashes, lowercase
                return path.Replace('\\', '/').ToLowerInvariant();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Get project name from Project Information
        /// </summary>
        public static string GetProjectInfoName(Document doc)
        {
            if (doc == null) return null;

            try
            {
                var projectInfo = doc.ProjectInformation;
                if (projectInfo == null) return doc.Title;

                // Try "Project Name" parameter
                var param = projectInfo.LookupParameter("Project Name");
                if (param != null && param.HasValue)
                {
                    var value = param.AsString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }

                // Fallback to document title
                return doc.Title;
            }
            catch
            {
                return doc.Title;
            }
        }

        /// <summary>
        /// Get cloud project name from cloud path
        /// Returns null if not a cloud model
        /// </summary>
        public static string GetCloudProjectName(Document doc)
        {
            if (doc == null) return null;
            if (!DocumentTypeHelper.IsCloud(doc)) return null;

            try
            {
                var path = doc.PathName;
                if (string.IsNullOrEmpty(path)) return null;

                // BIM 360 format: "BIM 360://ProjectName/FolderPath/FileName.rvt"
                if (path.StartsWith("BIM 360://", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = path.Substring(10).Split('/');
                    if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                        return parts[0];
                }

                // RSN format: "RSN://ProjectGUID|ProjectName/..."
                if (path.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase))
                {
                    var afterPrefix = path.Substring(6);
                    var pipeIndex = afterPrefix.IndexOf('|');
                    if (pipeIndex > 0)
                    {
                        var slashIndex = afterPrefix.IndexOf('/');
                        if (slashIndex > pipeIndex)
                        {
                            var projectName = afterPrefix.Substring(pipeIndex + 1, slashIndex - pipeIndex - 1);
                            if (!string.IsNullOrWhiteSpace(projectName))
                                return projectName;
                        }
                    }
                }

                // A360 format: similar to BIM 360
                if (path.StartsWith("A360://", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = path.Substring(7).Split('/');
                    if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                        return parts[0];
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Get project name (cloud preferred, fallback to Project Information)
        /// </summary>
        public static string GetProjectName(Document doc)
        {
            if (doc == null) return null;

            // Try cloud project name first
            var cloudName = GetCloudProjectName(doc);
            if (!string.IsNullOrEmpty(cloudName))
                return cloudName;

            // Fallback to Project Information
            return GetProjectInfoName(doc);
        }

        /// <summary>
        /// Get central model path for workshared documents
        /// Returns null for non-workshared documents
        /// </summary>
        public static string GetCentralModelPath(Document doc)
        {
            if (doc == null) return null;

            try
            {
                // Only workshared documents have central model path
                if (!DocumentTypeHelper.IsWorkshared(doc))
                    return null;

                // Get central model path
                var centralPath = doc.GetWorksharingCentralModelPath();
                if (centralPath == null || centralPath.Empty)
                    return null;

                // Convert to user-visible path string
                return ModelPathUtils.ConvertModelPathToUserVisiblePath(centralPath);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Get central model filename from path
        /// </summary>
        public static string GetCentralModelName(Document doc)
        {
            try
            {
                var centralPath = GetCentralModelPath(doc);
                if (string.IsNullOrEmpty(centralPath))
                    return null;

                // Extract filename from path
                return System.IO.Path.GetFileName(centralPath);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Determine if document is a local copy (vs central opened directly)
        /// </summary>
        public static bool IsLocalCopy(Document doc)
        {
            if (doc == null || !DocumentTypeHelper.IsWorkshared(doc))
                return false;

            try
            {
                var centralPath = GetCentralModelPath(doc);
                var documentPath = doc.PathName;

                if (string.IsNullOrEmpty(centralPath) || string.IsNullOrEmpty(documentPath))
                    return false;

                // If paths are different (case-insensitive), it's a local copy
                return !string.Equals(
                    NormalizePath(centralPath),
                    NormalizePath(documentPath),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Normalize path for comparison (forward slashes, lowercase)
        /// </summary>
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return path.Replace('\\', '/').ToLowerInvariant();
        }
    }
}
