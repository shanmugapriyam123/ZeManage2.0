using System;
using Autodesk.Revit.UI;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Execution;

namespace BIManage.Revit.Services
{
    /// <summary>
    ///     Wraps external event orchestration for queued Revit API calls.
    /// </summary>
    public class RevitExecutionService : IRevitExecutionService
    {
        private readonly IExternalEventOrchestrator _externalEventOrchestrator;
        private readonly ILogger? _logger;

        public RevitExecutionService(IExternalEventOrchestrator externalEventOrchestrator, ILogger? logger)
        {
            _externalEventOrchestrator = externalEventOrchestrator ?? throw new ArgumentNullException(nameof(externalEventOrchestrator));
            _logger = logger;
        }

        public ExternalEventRequest Queue<THandler>() where THandler : IExternalEventHandler
        {
            _logger?.LogDebug($"Queueing external event handler {typeof(THandler).Name}");
            return _externalEventOrchestrator.Raise<THandler>();
        }
    }
}
