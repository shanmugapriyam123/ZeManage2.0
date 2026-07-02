using Autodesk.Revit.UI;

namespace BIManage.Revit.Services
{
    /// <summary>
    ///     Interface for wrapping external event orchestration for queued Revit API calls.
    /// </summary>
    public interface IRevitExecutionService
    {
        /// <summary>
        ///     Queue an external event handler for execution on the Revit API thread.
        /// </summary>
        /// <typeparam name="THandler">The handler type</typeparam>
        /// <returns>The external event request result</returns>
        ExternalEventRequest Queue<THandler>() where THandler : IExternalEventHandler;
    }
}
