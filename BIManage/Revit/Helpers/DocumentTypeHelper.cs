using System;
using Autodesk.Revit.DB;

namespace BIManage.Revit.Helpers
{
    /// <summary>
    ///     Helper for detecting document type (Local, Workshared, Cloud)
    /// </summary>
    public static class DocumentTypeHelper
    {
        /// <summary>
        ///     Check if document is workshared (multi-user)
        /// </summary>
        public static bool IsWorkshared(Document doc)
        {
            if (doc == null) return false;

            try
            {
                return doc.IsWorkshared;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        ///     Check if document is cloud-based (BIM 360 / Autodesk Docs / ACC / Revit Cloud).
        ///     Prefers the Revit API (doc.IsModelInCloud and the central path's CloudPath flag)
        ///     and falls back to legacy URL-prefix matching for older Revit versions. The
        ///     prefix list deliberately includes "Autodesk Docs://" — without it, modern ACC
        ///     models that didn't satisfy the API path got mis-detected as on-prem and the
        ///     session log printed "Cloud: False" for an obvious cloud file.
        /// </summary>
        public static bool IsCloud(Document doc)
        {
            if (doc == null) return false;

            try
            {
                if (doc.IsModelInCloud) return true;
                var msp = doc.GetWorksharingCentralModelPath();
                if (msp != null && msp.CloudPath) return true;
            }
            catch
            {
                // Older Revit API surface (or worksharing not initialised) — fall through
                // to the path-prefix check below.
            }

            try
            {
                var path = doc.PathName;
                if (string.IsNullOrEmpty(path)) return false;

                return path.StartsWith("BIM 360://",       StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("RSN://",           StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("A360://",          StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("Autodesk Docs://", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        ///     Get document type classification
        /// </summary>
        public static DocumentType GetDocumentType(Document doc)
        {
            if (IsCloud(doc)) return DocumentType.Cloud;
            if (IsWorkshared(doc)) return DocumentType.Workshared;
            return DocumentType.Local;
        }

        /// <summary>
        ///     Get document type as string
        /// </summary>
        public static string GetDocumentTypeName(Document doc)
        {
            return GetDocumentType(doc).ToString();
        }
    }

    /// <summary>
    ///     Document type classification
    /// </summary>
    public enum DocumentType
    {
        /// <summary>Single-user, non-workshared document</summary>
        Local,

        /// <summary>Multi-user workshared document (local server)</summary>
        Workshared,

        /// <summary>Cloud-based document (BIM360/Revit Cloud)</summary>
        Cloud
    }
}
