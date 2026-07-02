using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;

namespace BIManage.AI.Terminal.Tools.SayHello;

/// <summary>
/// External event handler for the say_hello MCP tool.
/// Runs on Revit's UI thread, displays a TaskDialog confirming MCP connectivity.
/// </summary>
/// <remarks>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
internal sealed class SayHelloEventHandler : IWaitableExternalEventHandler
{
    private readonly System.Threading.ManualResetEvent _resetEvent = new(false);

    public string Message { get; set; } = "Hello from ZeManage AI Terminal!";

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            TaskDialog.Show("ZeManage AI Terminal", Message);
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    public string GetName() => "ZeManage Say Hello";
}
