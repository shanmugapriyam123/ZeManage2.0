using System;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Tools.SendCodeToRevit;

namespace BIManageRevit.Commands.RibbonCommands
{
    /// <summary>
    /// TEMPORARY DEV TEST — verifies the Phase 3d safety layer end-to-end by feeding two
    /// hand-crafted snippets through <see cref="SendCodeToRevitCommand"/>:
    ///   1. A safe read-only snippet that counts walls — expected to compile and run
    ///   2. An unsafe snippet that tries to create a Transaction — expected to be rejected
    ///      by the static analyzer BEFORE it ever reaches the compiler
    /// </summary>
    /// <remarks>
    /// REMOVE THIS COMMAND once Phase 3g (real Ze AI integration of send_code_to_revit) lands.
    /// Real invocation will happen through the chat dialog with OpenAI function calling.
    /// </remarks>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class TestSendCodeCommand : IExternalCommand
    {
        // The safe snippet — should pass analyzer + compiler + execution and return a count.
        // Kept simple so we can verify the round-trip without depending on what's in the model.
        private const string SafeSnippet = @"
var walls = new FilteredElementCollector(doc)
    .OfCategory(BuiltInCategory.OST_Walls)
    .WhereElementIsNotElementType()
    .ToList();
return new {
    wallCount = walls.Count,
    sampleNames = walls.Take(3).Select(w => w.Name).ToList(),
    documentTitle = doc.Title
};";

        // The unsafe snippet — should be rejected by SAFE020_NoTransaction at the analyzer stage.
        // We deliberately include multiple violations so the verdict shows the list-them-all behavior.
        private const string UnsafeSnippet = @"
using (var tx = new Transaction(doc, ""evil""))
{
    tx.Start();
    doc.Delete(new ElementId(123));
    tx.Commit();
}
return ""this should never run"";";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;

            try
            {
                var report = new System.Text.StringBuilder();
                report.AppendLine("Phase 3d Safety Layer Smoke Test");
                report.AppendLine("=================================");
                report.AppendLine();

                // ── Test 1: Safe snippet ──────────────────────────────────────
                report.AppendLine("TEST 1: Safe snippet (counts walls)");
                report.AppendLine("------------------------------------");
                var safeResult = InvokeSendCode(commandData.Application, SafeSnippet, "test-safe");
                report.AppendLine(JsonSerializer.Serialize(safeResult, new JsonSerializerOptions { WriteIndented = true }));
                report.AppendLine();

                // ── Test 2: Unsafe snippet ────────────────────────────────────
                report.AppendLine("TEST 2: Unsafe snippet (Transaction + Delete)");
                report.AppendLine("---------------------------------------------");
                var unsafeResult = InvokeSendCode(commandData.Application, UnsafeSnippet, "test-unsafe");
                report.AppendLine(JsonSerializer.Serialize(unsafeResult, new JsonSerializerOptions { WriteIndented = true }));
                report.AppendLine();

                report.AppendLine("Expected behavior:");
                report.AppendLine("  Test 1 → success:true, with wallCount populated from the live model.");
                report.AppendLine("  Test 2 → success:false, with errorMessage starting with");
                report.AppendLine("           'Safety analyzer rejected the code' and listing SAFE020_NoTransaction.");

                TaskDialog.Show("AI Terminal — Phase 3d Smoke Test", report.ToString());
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show(
                    "AI Terminal — Phase 3d Smoke Test FAILED",
                    $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}");
                return Result.Failed;
            }
        }

        /// <summary>
        /// Constructs a fresh <see cref="SendCodeToRevitCommand"/> per call and invokes it
        /// with the supplied snippet. Mirrors what the OpenAI function-calling dispatcher
        /// will do at runtime, minus the JSON-RPC plumbing.
        /// </summary>
        private static object InvokeSendCode(UIApplication uiApp, string code, string requestId)
        {
            // Build the parameters JsonElement directly — same shape OpenAI sends.
            var paramsJson = JsonSerializer.Serialize(new { code });
            using var doc = JsonDocument.Parse(paramsJson);

            var command = new SendCodeToRevitCommand(uiApp);
            return command.Execute(doc.RootElement.Clone(), requestId);
        }
    }
}
