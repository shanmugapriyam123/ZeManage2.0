using System;

namespace BIManage.Revit.EventRegistry
{
    /// <summary>
    ///     Interface for idling event wiring.
    /// </summary>
    public interface IIdlingEventRegistry : IDisposable
    {
        /// <summary>
        ///     Registers idling event handlers.
        /// </summary>
        void Register();

        /// <summary>
        ///     Unregisters idling event handlers.
        /// </summary>
        void Unregister();

        /// <summary>
        ///     Attempt to initialize idling event if UIApplication is available
        /// </summary>
        void TryInitialize();
    }
}
