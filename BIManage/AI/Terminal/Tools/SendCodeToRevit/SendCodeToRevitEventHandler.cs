using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.Safety;

namespace BIManage.AI.Terminal.Tools.SendCodeToRevit;

/// <summary>
/// External event handler for the <c>send_code_to_revit</c> MCP tool. Pipes AI-generated
/// C# through the three-stage Phase 3d safety layer:
/// <list type="number">
///   <item><see cref="RoslynStaticAnalyzer"/> rejects unsafe patterns at AST level</item>
///   <item><see cref="RoslynCompiler"/> compiles with strictly restricted assembly references</item>
///   <item>Reflection-invoked execution against the active <c>Document</c> on the Revit UI thread</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// The Sparx version runs AI code with NO safety checks. The ZeManage version refuses anything
/// the analyzer rejects, can't compile anything the compiler reference-set doesn't allow, and
/// audits every execution attempt via <see cref="ScriptExecutionAuditLog"/>.
/// </para>
/// <para>
/// The <c>transactionMode</c> parameter is accepted for API compatibility with Sparx's tool
/// shape, but in practice the analyzer rejects every Transaction construct anyway — so the
/// only meaningful mode is "none". The parameter is preserved on the handler for telemetry.
/// </para>
/// </remarks>
internal sealed class SendCodeToRevitEventHandler : IWaitableExternalEventHandler
{
    public const string TransactionModeAuto = "auto";
    public const string TransactionModeNone = "none";

    /// <summary>Hard cap on execution wall time. Caller's wait timeout should be a bit higher.</summary>
    private const int ExecutionTimeoutMs = 30_000;

    private readonly ManualResetEvent _resetEvent = new(false);

    public string? Code { get; set; }
    public object[] ExecutionParameters { get; set; } = Array.Empty<object>();
    public string TransactionMode { get; set; } = TransactionModeAuto;

    public ExecutionResultInfo ResultInfo { get; private set; } = new();

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        var auditEntry = new ScriptExecutionAuditEntry
        {
            ExecutedAtUtc = DateTime.UtcNow,
            UserName = TryGetUserName(app),
            DocumentTitle = app.ActiveUIDocument?.Document?.Title,
            Code = Code ?? string.Empty,
            TransactionMode = TransactionMode
        };

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
            {
                ResultInfo = new ExecutionResultInfo
                {
                    Success = false,
                    ErrorMessage = "No active Revit document. Open a model and try again."
                };
                auditEntry.Outcome = ScriptExecutionOutcome.NoActiveDocument;
                return;
            }

            if (string.IsNullOrWhiteSpace(Code))
            {
                ResultInfo = new ExecutionResultInfo
                {
                    Success = false,
                    ErrorMessage = "No code supplied. Pass a C# snippet in the 'code' parameter."
                };
                auditEntry.Outcome = ScriptExecutionOutcome.EmptyCode;
                return;
            }

            // ── Stage 1: Static analyzer ──────────────────────────────────────
            var verdict = RoslynStaticAnalyzer.Analyze(Code!);
            if (!verdict.IsAllowed)
            {
                ResultInfo = new ExecutionResultInfo
                {
                    Success = false,
                    ErrorMessage = verdict.DetailedReport()
                };
                auditEntry.Outcome = ScriptExecutionOutcome.RejectedByAnalyzer;
                auditEntry.AnalyzerReport = verdict.DetailedReport();
                return;
            }

            // ── Stage 2: Compile with restricted references ───────────────────
            var compileResult = RoslynCompiler.Compile(Code!);
            if (!compileResult.IsSuccess)
            {
                ResultInfo = new ExecutionResultInfo
                {
                    Success = false,
                    ErrorMessage = compileResult.DetailedReport()
                };
                auditEntry.Outcome = ScriptExecutionOutcome.CompileFailed;
                auditEntry.CompileReport = compileResult.DetailedReport();
                return;
            }

            // ── Stage 3: Invoke entry point ───────────────────────────────────
            // We're already on Revit's UI thread (this method is invoked by ExternalEvent),
            // so direct invocation is correct. The 30-second cap is enforced by the caller's
            // WaitForCompletion timeout — Revit's API isn't reliably interruptible mid-call,
            // so a soft timeout via cooperative cancellation isn't worth the complexity.
            var entryType = compileResult.CompiledAssembly!.GetType(compileResult.EntryPointTypeName);
            if (entryType == null)
            {
                ResultInfo = new ExecutionResultInfo
                {
                    Success = false,
                    ErrorMessage = $"Generated assembly is missing the expected entry type '{compileResult.EntryPointTypeName}'."
                };
                auditEntry.Outcome = ScriptExecutionOutcome.EntryPointMissing;
                return;
            }

            var entryMethod = entryType.GetMethod(compileResult.EntryPointMethodName);
            if (entryMethod == null)
            {
                ResultInfo = new ExecutionResultInfo
                {
                    Success = false,
                    ErrorMessage = $"Generated type is missing the expected entry method '{compileResult.EntryPointMethodName}'."
                };
                auditEntry.Outcome = ScriptExecutionOutcome.EntryPointMissing;
                return;
            }

            // Invoke with positional args: (Document, UIApplication). TargetInvocationException
            // wraps any exception thrown by user code — unwrap so the LLM sees the real message.
            object? returnValue;
            try
            {
                returnValue = entryMethod.Invoke(obj: null, parameters: new object[] { doc, app });
            }
            catch (System.Reflection.TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                ResultInfo = new ExecutionResultInfo
                {
                    Success = false,
                    ErrorMessage = $"Script execution threw {inner.GetType().Name}: {inner.Message}"
                };
                auditEntry.Outcome = ScriptExecutionOutcome.RuntimeException;
                auditEntry.RuntimeError = inner.ToString();
                return;
            }

            // ── Success path ─────────────────────────────────────────────────
            // Serialize the return value. Anonymous types serialize fine via System.Text.Json
            // reflection — we don't need a typed contract here.
            string serialized;
            try
            {
                serialized = JsonSerializer.Serialize(
                    returnValue,
                    new JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        WriteIndented = false
                    });
            }
            catch (Exception serEx)
            {
                serialized = $"\"(unable to serialize return value: {serEx.Message})\"";
            }

            ResultInfo = new ExecutionResultInfo
            {
                Success = true,
                Result = serialized
            };
            auditEntry.Outcome = ScriptExecutionOutcome.Success;
            auditEntry.ResultPreview = serialized.Length > 1000 ? serialized.Substring(0, 1000) + "…" : serialized;
        }
        catch (Exception ex)
        {
            // Catch-all for pipeline failures (NOT user-code failures — those are caught above).
            ResultInfo = new ExecutionResultInfo
            {
                Success = false,
                ErrorMessage = $"Pipeline failure: {ex.GetType().Name}: {ex.Message}"
            };
            auditEntry.Outcome = ScriptExecutionOutcome.PipelineException;
            auditEntry.RuntimeError = ex.ToString();
        }
        finally
        {
            stopwatch.Stop();
            auditEntry.ElapsedMs = stopwatch.ElapsedMilliseconds;
            ScriptExecutionAuditLog.Append(auditEntry);
            _resetEvent.Set();
        }
    }

    public string GetName() => "ZeManage Send Code To Revit";

    private static string? TryGetUserName(UIApplication app)
    {
        try { return app.Application.Username; }
        catch { return null; }
    }
}
