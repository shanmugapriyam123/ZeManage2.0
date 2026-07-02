using System;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal;
using BIManage.AI.Terminal.OpenAi;

namespace BIManageRevit.Commands.RibbonCommands
{
    /// <summary>
    /// TEMPORARY DEV TEST — exercises the full Phase 3c-NEW pipeline:
    /// ToolRegistry discovery → ToolSchemaBuilder → ToolDispatcher → live tool execution.
    /// </summary>
    /// <remarks>
    /// REMOVE THIS COMMAND once Phase 3g (real Ze AI integration) lands — real invocation
    /// will go through the chat dialog with OpenAI function calling, not a ribbon button.
    /// </remarks>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class TestSayHelloCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;

            try
            {
                // 1. Discover and register all 10 tool classes.
                var registry = ToolRegistry.DiscoverAndCreate(commandData.Application);

                // 2. Build the OpenAI tools array — what we'd send to the model.
                var toolDefs = ToolSchemaBuilder.Build(registry);
                var toolsJson = JsonSerializer.Serialize(toolDefs, new JsonSerializerOptions { WriteIndented = true });

                // 3. Dispatch a tool call exactly the way OpenAI's response would deliver it:
                //    function name + JSON-string arguments + per-call id.
                var dispatcher = new ToolDispatcher(registry);
                var resultJson = dispatcher.Dispatch(
                    functionName: "say_hello",
                    argumentsJson: """{ "message": "Phase 3c-NEW smoke test — registry + dispatcher reached the live tool." }""",
                    toolCallId: $"test-{Guid.NewGuid():N}");

                // The TaskDialog from SayHelloEventHandler.Execute() will already have appeared
                // by the time we reach here. We additionally surface the dispatcher's serialized
                // return value plus the discovery report.
                var registeredNames = string.Join(", ", registry.ToolNames.OrderBy(n => n, StringComparer.Ordinal));

                TaskDialog.Show(
                    "AI Terminal — Phase 3c-NEW Pipeline Test",
                    $"Registry discovered {registry.Count} tool(s):\n  {registeredNames}\n\n" +
                    $"Built {toolDefs.Count} OpenAI function definition(s) ({toolsJson.Length} chars of schema).\n\n" +
                    $"Dispatched 'say_hello' through ToolDispatcher.\n" +
                    $"Result JSON ({resultJson.Length} chars):\n{resultJson}\n\n" +
                    "If you saw a 'ZeManage AI Terminal' dialog before this one, the full Phase 3c-NEW pipeline works:\n" +
                    "• ToolRegistry.DiscoverAndCreate (reflection scan)\n" +
                    "• ToolSchemaBuilder.Build (OpenAI function definitions)\n" +
                    "• ToolDispatcher.Dispatch (JSON args → tool execution → JSON result)\n" +
                    "• ExternalEventCommandBase.IsCalledFromConstructionThread routing");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show(
                    "AI Terminal — Phase 3c-NEW Pipeline Test FAILED",
                    $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}");
                return Result.Failed;
            }
        }
    }
}
