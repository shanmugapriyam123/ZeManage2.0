using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using BIManage.Common.Helpers;
using BIManage.Revit.PinProtection.Models;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.PinProtection
{
    /// <summary>
    /// Handles persistence of pin protection metadata using Revit's Extensible Storage
    /// </summary>
    public static class PinProtectionStorage
    {
        #region Schema Configuration

        // Stable GUIDs for BIManageRevit Pin Protection
        private static readonly Guid SchemaGuid = new Guid("A8B7C6D5-E4F3-4A2B-9C8D-7E6F5A4B3C2D");
        private static readonly Guid ApplicationGuid = new Guid("B9C8D7E6-F5A4-4B3C-8D9E-6F7A8B9C0D1E");
        private const string SchemaName = "BIManageProtectedPinInfo";
        private const string FieldName = "ProtectedPinInfoData";
        private const string VendorId = "BIManage";

        private static Schema? _cachedSchema;
        private static ILogger? _logger;

        /// <summary>
        /// Initialize storage with logger
        /// </summary>
        public static void Initialize(ILogger logger)
        {
            _logger = logger;
        }

        #endregion

        #region Schema Creation

        /// <summary>
        /// Create or retrieve the Extensible Storage schema
        /// </summary>
        private static Schema GetOrCreateSchema()
        {
            if (_cachedSchema != null)
                return _cachedSchema;

            // Try to get existing schema
            _cachedSchema = Schema.Lookup(SchemaGuid);

            if (_cachedSchema == null)
            {
                // Create new schema
                SchemaBuilder builder = new SchemaBuilder(SchemaGuid);

                builder.SetSchemaName(SchemaName);
                builder.SetApplicationGUID(ApplicationGuid);
                builder.SetVendorId(VendorId);
                builder.SetReadAccessLevel(AccessLevel.Public);
                builder.SetWriteAccessLevel(AccessLevel.Public);

                // Add field to store JSON serialized ProtectedPinInfo
                builder.AddSimpleField(FieldName, typeof(string))
                    .SetDocumentation("Protected pin info serialized as JSON");

                _cachedSchema = builder.Finish();
                _logger?.LogInfo($"Created Extensible Storage schema: {SchemaName}");
            }

            return _cachedSchema;
        }

        #endregion

        #region Set Protection

        /// <summary>
        /// Protect elements with pin protection metadata
        /// Must be called within a transaction
        /// </summary>
        public static void SetProtection(
            Document document,
            ProtectedPinInfo protectionInfo,
            IEnumerable<Element> elements)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (protectionInfo == null)
                throw new ArgumentNullException(nameof(protectionInfo));
            if (elements == null || !elements.Any())
                throw new ArgumentException("No elements provided", nameof(elements));

            if (!document.IsModifiable)
                throw new InvalidOperationException("Document is not modifiable - must be called within a transaction");

            Schema schema = GetOrCreateSchema();

            foreach (Element element in elements)
            {
                if (element == null || !element.IsValidObject)
                    continue;

                // Handle special element types that need explicit pinning
                HandleSpecialElementTypes(element);

                // Create protection info specific to this element
                var elementProtection = ProtectedPinInfo.Create(
                    element,
                    protectionInfo.ProtectedBy,
                    protectionInfo.AdminComment,
                    protectionInfo.ProtectionModeType,
                    protectionInfo.IsRequireCommentForUnpin,
                    protectionInfo.IsInstantlyNotifyOnUnpin);

                // Store in extensible storage
                SetProtectionOnElement(element, schema, elementProtection);

                _logger?.LogDebug($"Protected element {element.Id.GetIdValue()} with mode {elementProtection.ProtectionModeType}");
            }

            _logger?.LogInfo($"Applied pin protection to {elements.Count()} elements (Mode: {protectionInfo.ProtectionModeType})");
        }

        /// <summary>
        /// Store protection metadata on single element
        /// </summary>
        private static void SetProtectionOnElement(
            Element element,
            Schema schema,
            ProtectedPinInfo protectionInfo)
        {
            // Serialize to JSON
            string json = JsonSerializer.Serialize(protectionInfo, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            // Get or create entity
            Entity entity = element.GetEntity(schema);
            if (!entity.IsValid())
            {
                entity = new Entity(schema);
            }

            // Set field value
            entity.Set(FieldName, json);

            // Store on element
            element.SetEntity(entity);
        }

        /// <summary>
        /// Handle special element types that need explicit pinning
        /// </summary>
        private static void HandleSpecialElementTypes(Element element)
        {
            switch (element)
            {
                case ViewSchedule _:
                case View _:
                case FamilySymbol _:
                case GroupType _:
                    // These element types may not respond to normal pin operations
                    // Explicitly set pinned state
                    if (!element.Pinned)
                    {
                        element.Pinned = true;
                    }
                    break;
            }
        }

        #endregion

        #region Get Protection

        /// <summary>
        /// Check if element has pin protection
        /// </summary>
        public static bool IsProtected(Element element)
        {
            if (element == null || !element.IsValidObject)
                return false;

            Schema schema = GetOrCreateSchema();
            Entity entity = element.GetEntity(schema);

            return entity.IsValid() && entity.Schema.GUID == schema.GUID;
        }

        /// <summary>
        /// Get protection info for single element
        /// </summary>
        public static ProtectedPinInfo? GetProtection(Element element)
        {
            if (element == null || !element.IsValidObject)
                return null;

            Schema schema = GetOrCreateSchema();
            Entity entity = element.GetEntity(schema);

            if (!entity.IsValid())
                return null;

            try
            {
                string json = entity.Get<string>(FieldName);
                if (string.IsNullOrEmpty(json))
                    return null;

                var protectionInfo = JsonSerializer.Deserialize<ProtectedPinInfo>(json);
                if (protectionInfo != null)
                {
                    // Update element reference for runtime use
                    protectionInfo.UpdateElementReference(element);
                }

                return protectionInfo;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to deserialize protection info for element {element.Id.GetIdValue()}: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Get all protected elements in document
        /// </summary>
        public static List<ProtectedPinInfo> GetAllProtectedElements(Document document)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            List<ProtectedPinInfo> protectedElements = new List<ProtectedPinInfo>();
            Schema schema = GetOrCreateSchema();

            // Use ExtensibleStorageFilter for efficient querying
            ExtensibleStorageFilter filter = new ExtensibleStorageFilter(schema.GUID);
            FilteredElementCollector collector = new FilteredElementCollector(document)
                .WherePasses(filter);

            foreach (Element element in collector)
            {
                var protectionInfo = GetProtection(element);
                if (protectionInfo != null)
                {
                    protectedElements.Add(protectionInfo);
                }
            }

            _logger?.LogDebug($"Found {protectedElements.Count} protected elements in document");
            return protectedElements;
        }

        /// <summary>
        /// Get protected elements from selection
        /// </summary>
        public static List<ProtectedPinInfo> GetProtectedFromSelection(IEnumerable<Element> elements)
        {
            if (elements == null)
                return new List<ProtectedPinInfo>();

            return elements
                .Select(GetProtection)
                .Where(p => p != null)
                .Cast<ProtectedPinInfo>()
                .ToList();
        }

        #endregion

        #region Clear Protection

        /// <summary>
        /// Remove pin protection from elements
        /// Must be called within a transaction
        /// </summary>
        public static void ClearProtection(Document document, IEnumerable<Element> elements)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (elements == null)
                return;

            if (!document.IsModifiable)
                throw new InvalidOperationException("Document is not modifiable - must be called within a transaction");

            Schema schema = GetOrCreateSchema();
            int clearedCount = 0;

            foreach (Element element in elements)
            {
                if (element == null || !element.IsValidObject)
                    continue;

                Entity entity = element.GetEntity(schema);
                if (entity.IsValid())
                {
                    element.DeleteEntity(schema);
                    clearedCount++;
                    _logger?.LogDebug($"Cleared protection from element {element.Id.GetIdValue()}");
                }
            }

            _logger?.LogInfo($"Cleared pin protection from {clearedCount} elements");
        }

        /// <summary>
        /// Remove protection from single element
        /// </summary>
        public static void ClearProtection(Document document, Element element)
        {
            ClearProtection(document, new[] { element });
        }

        /// <summary>
        /// Clear all protections in document (admin only)
        /// </summary>
        public static void ClearAllProtections(Document document)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            var allProtected = GetAllProtectedElements(document);
            var elements = allProtected
                .Select(p => p.RevitElement)
                .Where(e => e != null)
                .Cast<Element>();

            ClearProtection(document, elements);
        }

        #endregion

        #region Position Tracking (Bypass Detection)

        /// <summary>
        /// Tolerance for position comparison (0.001 feet ≈ 0.3mm)
        /// </summary>
        private const double PositionTolerance = 0.001;

        /// <summary>
        /// Get element's current position for comparison
        /// Uses LocationPoint for point-based elements, LocationCurve midpoint, or BoundingBox center
        /// </summary>
        public static XYZ? GetElementPosition(Element element)
        {
            if (element == null || !element.IsValidObject)
                return null;

            try
            {
                // Try LocationPoint first (works for most family instances)
                if (element.Location is LocationPoint locPt)
                    return locPt.Point;

                // Try LocationCurve (for walls, beams, etc.)
                if (element.Location is LocationCurve locCurve)
                    return locCurve.Curve.Evaluate(0.5, true); // Midpoint

                // Fallback to BoundingBox center
                var bbox = element.get_BoundingBox(null);
                if (bbox != null)
                    return (bbox.Min + bbox.Max) / 2;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Failed to get position for element {element.Id.GetIdValue()}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Capture and store element's current position in protection metadata
        /// Must be called within a transaction
        /// </summary>
        public static void CapturePosition(Document document, Element element)
        {
            if (element == null || !element.IsValidObject)
                return;

            if (!document.IsModifiable)
                throw new InvalidOperationException("Document is not modifiable - must be called within a transaction");

            var existing = GetProtection(element);
            if (existing == null)
                return;

            var position = GetElementPosition(element);
            if (position == null)
            {
                _logger?.LogDebug($"Could not capture position for element {element.Id.GetIdValue()} - no valid position found");
                return;
            }

            existing.OriginalPositionX = position.X;
            existing.OriginalPositionY = position.Y;
            existing.OriginalPositionZ = position.Z;
            existing.PositionCapturedAt = DateTime.UtcNow;
            existing.IsDirty = true;

            Schema schema = GetOrCreateSchema();
            SetProtectionOnElement(element, schema, existing);

            _logger?.LogDebug($"Captured position for element {element.Id.GetIdValue()}: ({position.X:F3}, {position.Y:F3}, {position.Z:F3})");
        }

        /// <summary>
        /// Get the stored original position for a protected element
        /// Returns null if no position was captured
        /// </summary>
        public static XYZ? GetStoredPosition(Element element)
        {
            var protection = GetProtection(element);
            if (protection == null)
                return null;

            if (!protection.OriginalPositionX.HasValue ||
                !protection.OriginalPositionY.HasValue ||
                !protection.OriginalPositionZ.HasValue)
                return null;

            return new XYZ(
                protection.OriginalPositionX.Value,
                protection.OriginalPositionY.Value,
                protection.OriginalPositionZ.Value);
        }

        /// <summary>
        /// Check if element has moved from its protected position
        /// Returns false if no position was captured or element is invalid
        /// </summary>
        public static bool HasMoved(Element element, double? customTolerance = null)
        {
            if (element == null || !element.IsValidObject)
                return false;

            var storedPosition = GetStoredPosition(element);
            if (storedPosition == null)
                return false; // No position captured, can't determine if moved

            var currentPosition = GetElementPosition(element);
            if (currentPosition == null)
                return false; // Can't get current position

            var tolerance = customTolerance ?? PositionTolerance;
            var distance = storedPosition.DistanceTo(currentPosition);

            if (distance > tolerance)
            {
                _logger?.LogDebug($"Element {element.Id.GetIdValue()} has moved: distance = {distance:F6} feet (tolerance = {tolerance})");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Update stored position to current position (after authorized move)
        /// Must be called within a transaction
        /// </summary>
        public static void UpdateStoredPosition(Document document, Element element)
        {
            CapturePosition(document, element);
            _logger?.LogInfo($"Updated stored position for element {element.Id.GetIdValue()}");
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Update protection comment (admin only)
        /// </summary>
        public static void UpdateProtectionComment(
            Document document,
            Element element,
            string newComment)
        {
            if (!document.IsModifiable)
                throw new InvalidOperationException("Document is not modifiable - must be called within a transaction");

            var existing = GetProtection(element);
            if (existing == null)
                return;

            existing.AdminComment = newComment;
            existing.IsDirty = true;

            Schema schema = GetOrCreateSchema();
            SetProtectionOnElement(element, schema, existing);

            _logger?.LogInfo($"Updated protection comment for element {element.Id.GetIdValue()}");
        }

        /// <summary>
        /// Update protection mode (admin only)
        /// </summary>
        public static void UpdateProtectionMode(
            Document document,
            Element element,
            ProtectionMode newMode)
        {
            if (!document.IsModifiable)
                throw new InvalidOperationException("Document is not modifiable - must be called within a transaction");

            var existing = GetProtection(element);
            if (existing == null)
                return;

            existing.ProtectionMode = (int)newMode;
            existing.IsDirty = true;

            Schema schema = GetOrCreateSchema();
            SetProtectionOnElement(element, schema, existing);

            _logger?.LogInfo($"Updated protection mode for element {element.Id.GetIdValue()} to {newMode}");
        }

        #endregion
    }
}
