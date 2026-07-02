using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BIManage.Revit.UITracking
{
    /// <summary>
    ///     Interface for selection tracking service
    /// </summary>
    public interface ISelectionTrackingService : IDisposable
    {
        /// <summary>
        ///     Currently selected element IDs
        /// </summary>
        ICollection<ElementId> CurrentSelection { get; }

        /// <summary>
        ///     Event fired when selection changes
        /// </summary>
        event EventHandler<SelectionChangedEventArgs>? SelectionChanged;

        /// <summary>
        ///     Register selection tracking with UIApplication
        /// </summary>
        void Register(UIApplication uiApplication);

        /// <summary>
        ///     Unregister selection tracking
        /// </summary>
        void Unregister();
    }
}
