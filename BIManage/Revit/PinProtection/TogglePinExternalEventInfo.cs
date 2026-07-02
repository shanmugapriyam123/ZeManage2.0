using Autodesk.Revit.DB;
using System.Collections.Generic;

namespace BIManage.Revit.PinProtection
{
    /// <summary>
    /// Singleton to pass data from DocumentChanged event to TogglePinExternalEvent.
    /// Used for floating pushpin icon protection.
    /// </summary>
    public class TogglePinExternalEventInfo
    {
        private static TogglePinExternalEventInfo? _instance;
        private static readonly object _lock = new object();

        public static TogglePinExternalEventInfo Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new TogglePinExternalEventInfo();
                        }
                    }
                }
                return _instance;
            }
        }

        private TogglePinExternalEventInfo() { }

        /// <summary>
        /// Elements modified by the "Toggle Pin" transaction
        /// </summary>
        public IEnumerable<Element>? AllModifiedElements { get; set; }

        /// <summary>
        /// Document where toggle occurred
        /// </summary>
        public Document? Document { get; set; }

        /// <summary>
        /// Clear data after processing
        /// </summary>
        public void Clear()
        {
            AllModifiedElements = null;
            Document = null;
        }
    }
}
