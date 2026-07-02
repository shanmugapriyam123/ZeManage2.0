using System.Threading;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.Models;

namespace BIManage.AI.Terminal.Tools.GetSelectedElements;

/// <summary>
/// External event handler for the <c>get_selected_elements</c> MCP tool.
/// Reads the active UIDocument's current selection on Revit's UI thread.
/// </summary>
/// <remarks>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
internal sealed class GetSelectedElementsEventHandler : IWaitableExternalEventHandler
{
    private readonly ManualResetEvent _resetEvent = new(false);

    /// <summary>Maximum number of elements to return. Null = no limit.</summary>
    public int? Limit { get; set; }

    /// <summary>Result populated by <see cref="Execute"/>. Always non-null after a completed run.</summary>
    public List<ElementInfo> ResultElements { get; private set; } = new();

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
            if (uiDoc == null)
            {
                ResultElements = new List<ElementInfo>();
                return;
            }

            var doc = uiDoc.Document;
            var selectedIds = uiDoc.Selection.GetElementIds();
            var selectedElements = selectedIds.Select(id => doc.GetElement(id)).Where(e => e != null).ToList();

            if (Limit.HasValue && Limit.Value > 0)
            {
                selectedElements = selectedElements.Take(Limit.Value).ToList();
            }

            ResultElements = selectedElements.Select(element => new ElementInfo
            {
#if REVIT2024_OR_GREATER
                Id = element.Id.Value,
#else
                Id = element.Id.IntegerValue,
#endif
                UniqueId = element.UniqueId,
                Name = element.Name,
                Category = element.Category?.Name
            }).ToList();
        }
        catch
        {
            // Swallow — surfacing exceptions from a background ExternalEvent handler crashes Revit.
            // Caller observes empty result and a successful WaitForCompletion → can detect and report.
            ResultElements = new List<ElementInfo>();
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    public string GetName() => "ZeManage Get Selected Elements";
}
