using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BIManage.Revit.UITracking
{
    /// <summary>
    ///     Interface for view tracking service
    /// </summary>
    public interface IViewTrackingService : IDisposable
    {
        /// <summary>
        ///     Currently active view
        /// </summary>
        View? CurrentView { get; }

        /// <summary>
        ///     Event fired when view changes
        /// </summary>
        event EventHandler<ViewActivatedEventArgs>? ViewChanged;

        /// <summary>
        ///     Register view tracking with UIApplication
        /// </summary>
        void Register(UIApplication uiApplication);

        /// <summary>
        ///     Unregister view tracking
        /// </summary>
        void Unregister();
    }
}
