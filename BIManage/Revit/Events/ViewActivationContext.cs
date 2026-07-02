using System;
using Autodesk.Revit.DB;

namespace BIManage.Revit.Events
{
    /// <summary>
    ///     Context for view activation events (productivity tracking)
    /// </summary>
    public class ViewActivationContext
    {
        public View? PreviousActiveView { get; }
        public View CurrentActiveView { get; }
        public DateTime Timestamp { get; }
        public string CurrentViewName => CurrentActiveView?.Name ?? "Unknown";
        public string PreviousViewName => PreviousActiveView?.Name ?? "None";

        public ViewActivationContext(View? previousActiveView, View currentActiveView)
        {
            PreviousActiveView = previousActiveView;
            CurrentActiveView = currentActiveView ?? throw new ArgumentNullException(nameof(currentActiveView));
            Timestamp = DateTime.UtcNow;
        }

        public override string ToString() =>
            $"View changed: '{PreviousViewName}' → '{CurrentViewName}' at {Timestamp:HH:mm:ss}";
    }
}
