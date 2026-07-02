namespace BIManage.AI.Terminal.Safety;

/// <summary>
/// Result of running AI-generated C# through the static safety analyzer.
/// Either <see cref="IsAllowed"/> is true (compile and execute may proceed) or it's false
/// (every reason for rejection is enumerated in <see cref="Rejections"/>).
/// </summary>
/// <remarks>
/// The analyzer returns ALL rejection reasons, not just the first one, so the LLM can
/// fix multiple issues in one retry rather than playing whack-a-mole across many rounds.
/// </remarks>
public sealed class SafetyVerdict
{
    /// <summary>True when no rejection rules were tripped.</summary>
    public bool IsAllowed => Rejections.Count == 0;

    /// <summary>Every rule that the supplied code violated. Empty when <see cref="IsAllowed"/> is true.</summary>
    public List<SafetyRejection> Rejections { get; } = new();

    /// <summary>
    /// Single-line summary suitable for putting in a tool-result JSON. Includes the count
    /// and the first reason, plus a "and N more" suffix when multiple rejections were collected.
    /// </summary>
    public string SummaryForLlm()
    {
        if (IsAllowed) return "Code passed all safety checks.";
        var first = Rejections[0];
        var suffix = Rejections.Count > 1 ? $" (plus {Rejections.Count - 1} more issue(s))" : "";
        return $"Rejected by safety analyzer: {first.RuleId} at line {first.Line} — {first.Message}{suffix}";
    }

    /// <summary>
    /// Full multi-line report listing every rejection. Sent back to the LLM in the tool
    /// result so it can fix all issues in one retry instead of one-at-a-time.
    /// </summary>
    public string DetailedReport()
    {
        if (IsAllowed) return "All safety checks passed.";
        var lines = new List<string>(Rejections.Count + 1)
        {
            $"Safety analyzer rejected the code ({Rejections.Count} issue(s)):"
        };
        for (int i = 0; i < Rejections.Count; i++)
        {
            var r = Rejections[i];
            lines.Add($"  {i + 1}. [{r.RuleId}] line {r.Line}: {r.Message}");
        }
        return string.Join("\n", lines);
    }
}

/// <summary>
/// A single rejection emitted by the static analyzer. Each carries the rule id (stable
/// identifier for the rule that fired), the source line number, and a human-readable
/// explanation suitable for showing to the LLM so it can correct the code.
/// </summary>
/// <remarks>
/// Uses ordinary <c>init</c>-only properties (not <c>required</c>) because the <c>required</c>
/// keyword needs <c>CompilerFeatureRequiredAttribute</c> which net48's BCL does not ship.
/// All three properties have defaults that make construction without them obviously wrong,
/// so the lack of compiler-enforced "you must set this" isn't a real safety hazard.
/// </remarks>
public sealed class SafetyRejection
{
    /// <summary>
    /// Stable id for the rule that fired, e.g. <c>"SAFE001_NoTransaction"</c>. Caller-facing —
    /// don't rename existing ids casually; downstream telemetry and audit logs key on them.
    /// </summary>
    public string RuleId { get; init; } = "SAFE_UNSPECIFIED";

    /// <summary>1-based source line where the offending construct appears. 0 when not known.</summary>
    public int Line { get; init; }

    /// <summary>Human-readable explanation of what was rejected and (briefly) why.</summary>
    public string Message { get; init; } = string.Empty;
}
