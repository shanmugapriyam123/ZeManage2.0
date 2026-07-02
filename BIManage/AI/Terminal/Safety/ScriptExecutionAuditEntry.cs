namespace BIManage.AI.Terminal.Safety;

/// <summary>
/// One row of the AI Terminal's script-execution audit log. Records every attempt to
/// run AI-generated C# via <c>send_code_to_revit</c>, whether it succeeded, failed safety
/// analysis, or threw at runtime. Kept entirely within <c>BIManage/AI/Terminal/Safety/</c>
/// — deliberately NOT joined to <c>ProtectionAuditEntry</c> so the AI audit can evolve
/// independently of the protection/rules audit story.
/// </summary>
/// <remarks>
/// <para>
/// Per the Phase 3d decisions, this audit is isolated to the AI folder. If we later decide
/// to surface it in the company-wide audit dashboard, that's a one-time projection — but
/// it does NOT share schema or storage with the protection audit pipeline.
/// </para>
/// <para>
/// Storage today is a daily append-only log file under
/// <c>%LOCALAPPDATA%\BIManageRevit\AI\Logs\ai_script_executions_YYYYMMDD.log</c>.
/// A future enhancement can mirror entries to SQLite or to a backend endpoint; the
/// <see cref="ScriptExecutionAuditLog"/> writer is the only place that needs to change.
/// </para>
/// </remarks>
public sealed class ScriptExecutionAuditEntry
{
    /// <summary>UTC timestamp the execution attempt started.</summary>
    public DateTime ExecutedAtUtc { get; set; }

    /// <summary>Revit application username if available, null when unreadable.</summary>
    public string? UserName { get; set; }

    /// <summary>Title of the active document at execution time, null when no doc.</summary>
    public string? DocumentTitle { get; set; }

    /// <summary>Full untruncated user code as submitted by the LLM.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Transaction mode parameter passed in (preserved for telemetry only).</summary>
    public string TransactionMode { get; set; } = "auto";

    /// <summary>Terminal state of the pipeline. Drives which optional fields below are populated.</summary>
    public ScriptExecutionOutcome Outcome { get; set; } = ScriptExecutionOutcome.Unknown;

    /// <summary>End-to-end wall time including analyzer, compile, and invocation.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>Analyzer rejection report (multi-line). Set only when Outcome == RejectedByAnalyzer.</summary>
    public string? AnalyzerReport { get; set; }

    /// <summary>Compiler diagnostics (multi-line). Set only when Outcome == CompileFailed.</summary>
    public string? CompileReport { get; set; }

    /// <summary>Stack-traced exception text. Set only when Outcome == RuntimeException or PipelineException.</summary>
    public string? RuntimeError { get; set; }

    /// <summary>Truncated JSON preview of the return value. Set only when Outcome == Success.</summary>
    public string? ResultPreview { get; set; }
}

/// <summary>
/// Terminal states for a script execution attempt. Used to slice the audit log without
/// needing to parse free-text fields.
/// </summary>
public enum ScriptExecutionOutcome
{
    /// <summary>Default — should never appear in a written audit entry.</summary>
    Unknown = 0,

    /// <summary>Script ran to completion and produced a serialized return value.</summary>
    Success = 1,

    /// <summary>No active Revit document at execution time.</summary>
    NoActiveDocument = 2,

    /// <summary>User submitted empty / whitespace-only code.</summary>
    EmptyCode = 3,

    /// <summary>One or more <see cref="RoslynStaticAnalyzer"/> rules fired.</summary>
    RejectedByAnalyzer = 4,

    /// <summary>Code passed the analyzer but did not compile.</summary>
    CompileFailed = 5,

    /// <summary>Compiled assembly was missing the expected entry type or method.</summary>
    EntryPointMissing = 6,

    /// <summary>User code threw at runtime (TargetInvocationException unwrapped).</summary>
    RuntimeException = 7,

    /// <summary>Pipeline itself threw — bug in our code, not in the user's snippet.</summary>
    PipelineException = 8,
}
