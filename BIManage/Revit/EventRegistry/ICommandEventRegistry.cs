using System;

namespace BIManage.Revit.EventRegistry
{
    /// <summary>
    ///     Interface for command event wiring.
    /// </summary>
    public interface ICommandEventRegistry : IDisposable
    {
        /// <summary>
        ///     Registers command event handlers.
        /// </summary>
        void Register();

        /// <summary>
        ///     Unregisters command event handlers.
        /// </summary>
        void Unregister();

        /// <summary>
        ///     Attempt to initialize command bindings if UIApplication is available
        /// </summary>
        void TryInitialize();
    }
}
