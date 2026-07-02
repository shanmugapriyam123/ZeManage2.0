using System;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.Helpers
{
    /// <summary>
    /// Helper for extracting persistent model GUID
    /// </summary>
    public static class ModelGuidHelper
    {
        /// <summary>
        /// Get persistent model GUID
        /// For workshared models: Uses GetWorksharingCentralModelPath().GetModelGUID() (most reliable)
        /// Falls back to WorksharingCentralGUID, then deterministic GUID from path
        /// For non-workshared: Generates deterministic GUID from normalized path
        /// </summary>
        public static string GetModelGuid(Document doc, ILogger logger = null)
        {
            if (doc == null)
                return null;

            try
            {
                // Cloud models (workshared or not): use cloud API GUID — this is the
                // authoritative identifier from ACC/BIM 360, not a generated hash.
                // CRITICAL: for cloud models we MUST NOT fall through to workshared /
                // path-derived methods if the cloud API returns Empty. Workshared
                // methods (WorksharingCentralGUID, GetWorksharingCentralModelPath)
                // return per-Revit-instance values, so two users on the same cloud
                // model would generate different modelGuids and end up with duplicate
                // backend registrations (the v3-cloud bug). Path-derived hashes are
                // also unsafe — cloud paths can collide for files in nested folders.
                // Return null instead and let the caller retry once cloud sync has
                // populated the URN-based GUID (typically by the next
                // DocumentSynchronizedWithCentral event).
                if (doc.IsModelInCloud)
                {
                    try
                    {
                        var cloudPath = doc.GetCloudModelPath();
                        if (cloudPath != null)
                        {
                            var modelGuid = cloudPath.GetModelGUID();
                            if (modelGuid != Guid.Empty)
                            {
                                // DEBUG-level because this is the happy path and the helper
                                // is invoked by many code paths (cache, sync events, idling,
                                // metrics) — INFO would log 250+ lines per session and drown
                                // out real signals. WARN variants below still fire at WARN.
                                logger?.LogDebug($"[ModelGuid] cloud-api success: {modelGuid}");
                                return modelGuid.ToString();
                            }
                            logger?.LogWarning($"[ModelGuid] cloud-api returned Guid.Empty for cloud doc '{doc.Title}' — returning null; caller should retry after next sync");
                        }
                        else
                        {
                            logger?.LogWarning($"[ModelGuid] cloud-api: doc.GetCloudModelPath() returned null for cloud doc '{doc.Title}' — returning null");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning($"[ModelGuid] cloud-api threw for cloud doc '{doc.Title}': {ex.Message} — returning null");
                    }

                    // No fall-through. Cloud model + unreliable cloud API = null,
                    // and the caller's responsibility to retry on a later sync event.
                    return null;
                }

                // For workshared models, try central model path GUID
                if (DocumentTypeHelper.IsWorkshared(doc))
                {
                    // Method 1: GetWorksharingCentralModelPath().GetModelGUID() - most reliable
                    try
                    {
                        var centralPath = doc.GetWorksharingCentralModelPath();
                        if (centralPath != null && !centralPath.Empty)
                        {
                            var modelGuid = centralPath.GetModelGUID();
                            if (modelGuid != Guid.Empty)
                            {
                                logger?.LogDebug($"ModelGuid via GetModelGUID(): {modelGuid}");
                                return modelGuid.ToString();
                            }
                        }
                        logger?.LogDebug("Method 1 (GetModelGUID) returned Empty");
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug($"Method 1 failed: {ex.Message}");
                    }

                    // Method 2: WorksharingCentralGUID property
                    try
                    {
                        var centralGuid = doc.WorksharingCentralGUID;
                        if (centralGuid != Guid.Empty)
                        {
                            logger?.LogDebug($"ModelGuid via WorksharingCentralGUID: {centralGuid}");
                            return centralGuid.ToString();
                        }
                        logger?.LogDebug("Method 2 (WorksharingCentralGUID) returned Empty");
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug($"Method 2 failed: {ex.Message}");
                    }

                    // Fallback for workshared: generate from central path
                    var centralPathStr = DocumentInformationHelper.GetCentralModelPath(doc);
                    if (!string.IsNullOrEmpty(centralPathStr))
                    {
                        var deterministicGuid = GenerateDeterministicGuid(centralPathStr);
                        logger?.LogDebug($"ModelGuid via deterministic (central path): {deterministicGuid}");
                        return deterministicGuid;
                    }
                }

                // For non-workshared local models, generate deterministic GUID from path
                var normalizedPath = DocumentInformationHelper.GetNormalizedPath(doc);
                if (!string.IsNullOrEmpty(normalizedPath))
                {
                    var guid = GenerateDeterministicGuid(normalizedPath);
                    logger?.LogDebug($"ModelGuid via deterministic (path): {guid}");
                    return guid;
                }

                // Fallback: use document title (less reliable)
                var titleGuid = GenerateDeterministicGuid(doc.Title ?? "untitled");
                logger?.LogDebug($"ModelGuid via deterministic (title): {titleGuid}");
                return titleGuid;
            }
            catch (Exception ex)
            {
                logger?.LogError($"GetModelGuid failed: {ex.Message}", ex);
                // Fallback: generate from path or title
                var path = DocumentInformationHelper.GetNormalizedPath(doc);
                return GenerateDeterministicGuid(path ?? doc.Title ?? "untitled");
            }
        }

        /// <summary>
        /// Generate deterministic GUID from string (MD5 hash-based)
        /// </summary>
        private static string GenerateDeterministicGuid(string input)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
                var guid = new Guid(hash);
                return guid.ToString();
            }
        }
    }
}
