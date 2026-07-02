using System.Threading;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.Models;

namespace BIManage.AI.Terminal.Tools.GetCurrentViewInfo;

/// <summary>
/// External event handler for the <c>get_current_view_info</c> MCP tool.
/// Snapshots the active view's id, name, type, scale, detail level on Revit's UI thread.
/// </summary>
/// <remarks>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
internal sealed class GetCurrentViewInfoEventHandler : IWaitableExternalEventHandler
{
    private readonly ManualResetEvent _resetEvent = new(false);

    public CurrentViewInfo? ResultInfo { get; private set; }

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            var uiDoc = app.ActiveUIDocument;
            if (uiDoc == null) { ResultInfo = null; return; }

            var activeView = uiDoc.Document.ActiveView;
            if (activeView == null) { ResultInfo = null; return; }

            ResultInfo = new CurrentViewInfo
            {
#if REVIT2024_OR_GREATER
                Id = activeView.Id.Value,
#else
                Id = activeView.Id.IntegerValue,
#endif
                UniqueId = activeView.UniqueId,
                Name = activeView.Name,
                ViewType = activeView.ViewType.ToString(),
                IsTemplate = activeView.IsTemplate,
                Scale = activeView.Scale,
                DetailLevel = activeView.DetailLevel.ToString(),
            };
        }
        catch
        {
            ResultInfo = null;
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    public string GetName() => "ZeManage Get Current View Info";
}
