using System;
using Autodesk.Revit.ApplicationServices;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.EventRegistry
{
    /// <summary>
    ///     Placeholder for document-level event wiring.
    ///     Currently logs activation while legacy registry handles subscriptions.
    /// </summary>
    public class DocumentEventRegistry : IDocumentEventRegistry
    {
        private readonly ControlledApplication _controlledApplication;
        private readonly IEventTogglePolicy _togglePolicy;
        private readonly ILogger? _logger;
        private bool _disposed;

        public DocumentEventRegistry(
            ControlledApplication controlledApplication,
            IEventTogglePolicy togglePolicy,
            ILogger? logger)
        {
            _controlledApplication = controlledApplication;
            _togglePolicy = togglePolicy;
            _logger = logger;
        }

        public void Register()
        {
            if (!_togglePolicy.ShouldMonitorDocuments()) return;
            _logger?.LogDebug("Document event registry is active.");
            // Document lifecycle events are currently handled by EventRegistryService.
        }

        public void Unregister()
        {
            if (!_togglePolicy.ShouldMonitorDocuments()) return;
            _logger?.LogDebug("Document event registry is shutting down.");
        }

        public void Dispose()
        {
            if (_disposed) return;

            Unregister();
            _disposed = true;
        }
    }
}
