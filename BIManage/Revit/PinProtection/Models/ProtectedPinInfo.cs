using System;
using System.Text.Json.Serialization;
using Autodesk.Revit.DB;
using BIManage.Common.Helpers;

namespace BIManage.Revit.PinProtection.Models
{
    /// <summary>
    /// Data model for pin protection metadata
    /// Stores all configuration and audit information for protected elements
    /// </summary>
    public class ProtectedPinInfo
    {
        #region Element Identification

        /// <summary>
        /// UniqueId of the protected element (persistent across sessions)
        /// </summary>
        public string ElementGuid { get; set; } = string.Empty;

        /// <summary>
        /// ElementId value (may change after sync/copy)
        /// </summary>
        public long ElementId { get; set; }

        /// <summary>
        /// Display name of element
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Revit category name of the element (e.g. "Walls", "Floors")
        /// </summary>
        public string Category { get; set; } = string.Empty;

        #endregion

        #region Protection Metadata

        /// <summary>
        /// Username of admin who protected this element
        /// </summary>
        public string ProtectedBy { get; set; } = string.Empty;

        /// <summary>
        /// When this protection was created (UTC)
        /// </summary>
        public DateTime? DateUTC { get; set; }

        /// <summary>
        /// Admin's comment explaining why element is protected (plain text)
        /// </summary>
        public string AdminComment { get; set; } = string.Empty;

        #endregion

        #region Protection Configuration

        /// <summary>
        /// Protection mode: 0=Monitor, 1=Guide, 2=Prevent
        /// </summary>
        public int ProtectionMode { get; set; }

        /// <summary>
        /// Require user to provide comment when unpinning
        /// </summary>
        public bool IsRequireCommentForUnpin { get; set; }

        /// <summary>
        /// Send notification to admin when element is unpinned
        /// </summary>
        public bool IsInstantlyNotifyOnUnpin { get; set; }

        #endregion

        #region Position Tracking (Bypass Detection)

        /// <summary>
        /// Original X position when protection was applied (in feet)
        /// </summary>
        public double? OriginalPositionX { get; set; }

        /// <summary>
        /// Original Y position when protection was applied (in feet)
        /// </summary>
        public double? OriginalPositionY { get; set; }

        /// <summary>
        /// Original Z position when protection was applied (in feet)
        /// </summary>
        public double? OriginalPositionZ { get; set; }

        /// <summary>
        /// When the position was captured (UTC)
        /// </summary>
        public DateTime? PositionCapturedAt { get; set; }

        #endregion

        #region Runtime Properties (Not Serialized)

        /// <summary>
        /// Current pin state of element (not persisted)
        /// </summary>
        [JsonIgnore]
        public bool IsPinned { get; set; }

        /// <summary>
        /// Reference to Revit element (not persisted)
        /// IMPORTANT: Must be JsonIgnore to prevent serialization of Revit objects
        /// which would cause WorksharingCentralGUID errors on non-workshared models
        /// </summary>
        [JsonIgnore]
        public Element? RevitElement { get; set; }

        /// <summary>
        /// Soft delete flag (not persisted)
        /// </summary>
        [JsonIgnore]
        public bool IsDeleted { get; set; }

        /// <summary>
        /// Change tracking flag (not persisted)
        /// </summary>
        [JsonIgnore]
        public bool IsDirty { get; set; }

        #endregion

        #region Computed Properties (Not Serialized)

        /// <summary>
        /// Protection mode as enum
        /// </summary>
        [JsonIgnore]
        public ProtectionMode ProtectionModeType => (ProtectionMode)ProtectionMode;

        /// <summary>
        /// Date converted to local time
        /// </summary>
        [JsonIgnore]
        public DateTime? DateLocalTime => DateUTC?.ToLocalTime();

        /// <summary>
        /// User-friendly date string
        /// </summary>
        [JsonIgnore]
        public string FriendlyDate => DateLocalTime?.ToString("g") ?? "Unknown";

        /// <summary>
        /// Tooltip text: "Protected by Username on Date"
        /// </summary>
        [JsonIgnore]
        public string TooltipText => $"Protected by {ProtectedBy} on {FriendlyDate}";

        #endregion

        #region Methods

        /// <summary>
        /// Create protection info for new protection
        /// Automatically captures element position for bypass detection
        /// </summary>
        public static ProtectedPinInfo Create(
            Element element,
            string protectedBy,
            string adminComment,
            ProtectionMode mode,
            bool requireComment = false,
            bool notifyOnUnpin = false)
        {
            var info = new ProtectedPinInfo
            {
                ElementGuid = element.UniqueId,
                ElementId = element.Id.GetIdValue(),
                Name = element.Name,
                Category = element.Category?.Name,
                ProtectedBy = protectedBy,
                DateUTC = DateTime.UtcNow,
                AdminComment = adminComment,
                ProtectionMode = (int)mode,
                IsRequireCommentForUnpin = requireComment,
                IsInstantlyNotifyOnUnpin = notifyOnUnpin,
                IsPinned = element.Pinned,
                RevitElement = element
            };

            // Capture initial position for bypass detection
            CaptureElementPosition(element, info);

            return info;
        }

        /// <summary>
        /// Capture element position for bypass detection
        /// </summary>
        private static void CaptureElementPosition(Element element, ProtectedPinInfo info)
        {
            try
            {
                XYZ? position = null;

                // Try LocationPoint first (works for most family instances)
                if (element.Location is LocationPoint locPt)
                {
                    position = locPt.Point;
                }
                // Try LocationCurve (for walls, beams, etc.)
                else if (element.Location is LocationCurve locCurve)
                {
                    position = locCurve.Curve.Evaluate(0.5, true); // Midpoint
                }
                // Fallback to BoundingBox center
                else
                {
                    var bbox = element.get_BoundingBox(null);
                    if (bbox != null)
                    {
                        position = (bbox.Min + bbox.Max) / 2;
                    }
                }

                if (position != null)
                {
                    info.OriginalPositionX = position.X;
                    info.OriginalPositionY = position.Y;
                    info.OriginalPositionZ = position.Z;
                    info.PositionCapturedAt = DateTime.UtcNow;
                }
            }
            catch
            {
                // Fail silently - position capture is optional
            }
        }

        /// <summary>
        /// Update element reference (for runtime use)
        /// </summary>
        public void UpdateElementReference(Element element)
        {
            RevitElement = element;
            IsPinned = element.Pinned;
            ElementId = element.Id.GetIdValue();
        }

        #endregion
    }

    /// <summary>
    /// Protection mode enumeration
    /// </summary>
    public enum ProtectionMode
    {
        /// <summary>
        /// Notify mode - Log action but allow unpin
        /// </summary>
        Notify = 0,

        /// <summary>
        /// Assist mode - Show warning but allow unpin after acknowledgment
        /// </summary>
        Assist = 1,

        /// <summary>
        /// Protect mode - Block unpin for non-admins
        /// </summary>
        Protect = 2
    }
}
