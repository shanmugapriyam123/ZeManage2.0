using Autodesk.Revit.UI;

namespace BIManage.AI.Terminal.Execution;

/// <summary>
/// External event handler that can be awaited for completion.
/// Adapted from RevitMCPSDK to avoid external NuGet dependency.
/// </summary>
public interface IWaitableExternalEventHandler : IExternalEventHandler
{
    bool WaitForCompletion(int timeoutMilliseconds = 10000);
}
