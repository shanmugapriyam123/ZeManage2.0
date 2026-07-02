using System.Text.Json;
using System.Threading;
using Autodesk.Revit.UI;

namespace BIManage.AI.Terminal.Execution;

/// <summary>
/// Base class for MCP tool commands that need to run on Revit's UI thread.
/// Wraps an IWaitableExternalEventHandler and exposes a synchronous Execute() that
/// raises the event and waits for completion. Adapted from RevitMCPSDK.
/// </summary>
/// <remarks>
/// <para>
/// Parameters are passed as <see cref="JsonElement"/> rather than Newtonsoft's JObject,
/// matching the rest of the BIManage.AI namespace which uses System.Text.Json.
/// (Newtonsoft.Json is deliberately not referenced from BIManageRevit.csproj — see
/// the csproj comments around the BuildAddons / RemoveFlatNewtonsoftJson targets.)
/// </para>
/// <para>
/// <b>Threading constraint:</b> <see cref="RaiseAndWaitForCompletion"/> MUST be called from a
/// background thread. Revit only delivers external events when the UI thread returns to its
/// message loop, so calling Raise() and then blocking from the UI thread itself causes a
/// guaranteed deadlock (handler never fires → timeout). This is by design — the MCP server
/// (Phase 3c) will dispatch tool calls from background threads, which is the supported path.
/// For UI-thread call sites (e.g. the temporary <c>TestSayHelloCommand</c> ribbon button),
/// invoke the handler's logic directly via <see cref="ExecuteOnUiThread"/> instead.
/// </para>
/// </remarks>
public abstract class ExternalEventCommandBase
{
    protected readonly UIApplication UiApp;
    protected readonly ExternalEvent ExternalEvent;
    protected readonly IWaitableExternalEventHandler Handler;

    /// <summary>
    /// Thread that constructed this command. Revit's IExternalCommand pipeline always runs on
    /// the UI thread, so commands constructed from a ribbon button capture the UI thread ID
    /// here. Commands constructed by the MCP server (Phase 3c) on a background thread will
    /// capture a different ID, so derived classes can route correctly via <see cref="IsCalledFromConstructionThread"/>.
    /// </summary>
    private readonly int _constructionThreadId;

    /// <summary>The tool name (e.g. "say_hello"). Must be unique within a <see cref="ToolRegistry"/>.</summary>
    public abstract string CommandName { get; }

    /// <summary>
    /// Human-readable description shown to the LLM in the OpenAI <c>tools</c> array.
    /// The LLM uses this to decide when to call the tool, so be specific about what it does
    /// and when it's the right choice. Defaults to the command name if not overridden.
    /// </summary>
    public virtual string Description => CommandName;

    /// <summary>
    /// Parameter schema declared as a JSON-schema object. Override in derived classes that
    /// accept input. Default returns an empty object schema (i.e. no parameters expected),
    /// suitable for tools like <c>get_current_view_info</c> that have no inputs.
    /// </summary>
    /// <remarks>
    /// Schema is consumed by the OpenAI function-calling adapter and (later) the MCP server.
    /// Both clients use the same JSON-schema dialect, so one definition serves both.
    /// </remarks>
    public virtual OpenAi.OpenAiParametersSchema ParameterSchema => new();

    protected ExternalEventCommandBase(IWaitableExternalEventHandler handler, UIApplication uiApp)
    {
        Handler = handler;
        UiApp = uiApp;
        ExternalEvent = ExternalEvent.Create(handler);
        _constructionThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    /// <summary>
    /// True when the calling thread matches the thread that constructed this command.
    /// Use this in <see cref="Execute"/> to choose between <see cref="ExecuteOnUiThread"/>
    /// (when on UI thread) and <see cref="RaiseAndWaitForCompletion"/> (when on background thread).
    /// </summary>
    protected bool IsCalledFromConstructionThread()
        => Thread.CurrentThread.ManagedThreadId == _constructionThreadId;

    /// <summary>
    /// Synchronously executes the command by raising the external event and waiting for the handler.
    /// Override this in derived commands; call <see cref="RaiseAndWaitForCompletion"/> after
    /// configuring handler inputs.
    /// </summary>
    /// <param name="parameters">
    /// JSON-encoded MCP tool parameters. Pass <see cref="JsonElement"/> with <see cref="JsonValueKind.Undefined"/>
    /// when no parameters are provided.
    /// </param>
    public abstract object Execute(JsonElement parameters, string requestId);

    /// <summary>
    /// Raises the external event and waits up to the given timeout for the handler to signal completion.
    /// Returns true if the handler completed in time; false on timeout.
    /// </summary>
    /// <remarks>
    /// <b>Background-thread only.</b> Will deadlock if called from Revit's UI thread because the
    /// raised event cannot be processed until the UI thread returns to its message loop, but this
    /// call is blocking that very thread. Use <see cref="ExecuteOnUiThread"/> from UI-thread call sites.
    /// </remarks>
    protected bool RaiseAndWaitForCompletion(int timeoutMilliseconds)
    {
        ExternalEvent.Raise();
        return Handler.WaitForCompletion(timeoutMilliseconds);
    }

    /// <summary>
    /// Synchronously invokes the handler's <see cref="IExternalEventHandler.Execute"/> directly,
    /// bypassing the ExternalEvent dispatcher. Use this only when you know the caller is already
    /// on Revit's UI thread (e.g. inside an <see cref="IExternalCommand"/>).
    /// </summary>
    protected void ExecuteOnUiThread()
    {
        Handler.Execute(UiApp);
    }
}
