using Autodesk.Revit.DB;
using System.Collections.Generic;

namespace BIManage.Revit.PinProtection
{
    /// <summary>
    /// Singleton to pass data from DocumentChanged event to PinAfterEventExternalEvent.
    /// Used for CAD Import Pin Prompt and Copy/Monitor Pin Protection.
    /// </summary>
    public class PinAfterEventExternalEventInfo
    {
        private static PinAfterEventExternalEventInfo? _instance;
        private static readonly object _lock = new object();

        public static PinAfterEventExternalEventInfo Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new PinAfterEventExternalEventInfo();
                        }
                    }
                }
                return _instance;
            }
        }

        private PinAfterEventExternalEventInfo() { }

        /// <summary>
        /// Elements to pin after event protection approval
        /// </summary>
        public List<ElementId>? ElementIds { get; set; }

        /// <summary>
        /// Document where the operation occurred
        /// </summary>
        public Document? Document { get; set; }

        /// <summary>
        /// Source protection that triggered the pin prompt
        /// (e.g., "Ze_CADImportPinPrompt", "Ze_CopyMonitorPinProtection")
        /// </summary>
        public string? SourceProtection { get; set; }

        /// <summary>
        /// Whether to auto-pin without prompting
        /// </summary>
        public bool AutoPinWithoutPrompt { get; set; }

        /// <summary>
        /// Clear data after processing
        /// </summary>
        public void Clear()
        {
            ElementIds = null;
            Document = null;
            SourceProtection = null;
            AutoPinWithoutPrompt = false;
        }
    }
}
