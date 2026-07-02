using System;

namespace BIManage.Revit.EventRegistry
{
    /// <summary>
    ///     Interface for UI tracking event registry
    /// </summary>
    public interface IUITrackingEventRegistry : IDisposable
    {
        /// <summary>
        ///     Register UI tracking (view and selection)
        /// </summary>
        void Register();

        /// <summary>
        ///     Unregister UI tracking
        /// </summary>
        void Unregister();

        /// <summary>
        ///     Attempt to initialize if UIApplication is available
        /// </summary>
        void TryInitialize();
    }
}
