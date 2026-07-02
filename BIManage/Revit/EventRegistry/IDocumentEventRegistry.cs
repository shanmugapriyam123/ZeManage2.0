using System;

namespace BIManage.Revit.EventRegistry
{
    /// <summary>
    ///     Interface for document-level event wiring.
    /// </summary>
    public interface IDocumentEventRegistry : IDisposable
    {
        /// <summary>
        ///     Registers document event handlers.
        /// </summary>
        void Register();

        /// <summary>
        ///     Unregisters document event handlers.
        /// </summary>
        void Unregister();
    }
}
