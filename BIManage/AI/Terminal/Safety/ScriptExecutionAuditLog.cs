using System.IO;
using System.Text;

namespace BIManage.AI.Terminal.Safety;

/// <summary>
/// Append-only file writer for the AI Terminal's script-execution audit log. One daily log
/// file under <c>%LOCALAPPDATA%\BIManageRevit\AI\Logs\ai_script_executions_YYYYMMDD.log</c>.
/// </summary>
/// <remarks>
/// <para>
/// Intentionally simple and isolated. Doesn't share infrastructure with <c>AiDiagLog</c>
/// or <c>ProtectionAuditEntry</c> — the AI script audit has different retention, different
/// sensitivity (it contains AI-generated source code), and different access patterns
/// (occasional incident review rather than continuous monitoring).
/// </para>
/// <para>
/// Failure mode is "silently swallow" — audit writes must never break the pipeline they're
/// auditing. If the disk is full or the directory is unwritable, the script execution still
/// returns its result; only the audit row is lost.
/// </para>
/// </remarks>
public static class ScriptExecutionAuditLog
{
    private static readonly object _writeLock = new();
    private static readonly string _logDir;

    static ScriptExecutionAuditLog()
    {
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BIManageRevit", "AI", "Logs");

        try { Directory.CreateDirectory(_logDir); }
        catch { /* non-critical */ }
    }

    private static string LogFilePath
        => Path.Combine(_logDir, $"ai_script_executions_{DateTime.Now:yyyyMMdd}.log");

    /// <summary>
    /// Writes a single audit entry to today's log file. Multi-line, plaintext, human-readable —
    /// designed for forensic review with grep/notepad, not machine parsing. If a structured
    /// sink becomes useful later, add a second writer here.
    /// </summary>
    public static void Append(ScriptExecutionAuditEntry entry)
    {
        if (entry == null) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("════════════════════════════════════════════════════════════");
            sb.AppendLine($"  SCRIPT EXECUTION  {entry.ExecutedAtUtc:yyyy-MM-dd HH:mm:ss}Z  ({entry.ElapsedMs}ms)");
            sb.AppendLine("════════════════════════════════════════════════════════════");
            sb.AppendLine($"  Outcome    : {entry.Outcome}");
            sb.AppendLine($"  User       : {entry.UserName ?? "(unknown)"}");
            sb.AppendLine($"  Document   : {entry.DocumentTitle ?? "(none)"}");
            sb.AppendLine($"  TxMode     : {entry.TransactionMode}");
            sb.AppendLine("────────────────────────────────────────────────────────────");
            sb.AppendLine("  Code:");
            sb.AppendLine(IndentLines(entry.Code, "    "));

            if (!string.IsNullOrEmpty(entry.AnalyzerReport))
            {
                sb.AppendLine("  Analyzer report:");
                sb.AppendLine(IndentLines(entry.AnalyzerReport!, "    "));
            }
            if (!string.IsNullOrEmpty(entry.CompileReport))
            {
                sb.AppendLine("  Compile report:");
                sb.AppendLine(IndentLines(entry.CompileReport!, "    "));
            }
            if (!string.IsNullOrEmpty(entry.RuntimeError))
            {
                sb.AppendLine("  Runtime error:");
                sb.AppendLine(IndentLines(entry.RuntimeError!, "    "));
            }
            if (!string.IsNullOrEmpty(entry.ResultPreview))
            {
                sb.AppendLine("  Result preview:");
                sb.AppendLine(IndentLines(entry.ResultPreview!, "    "));
            }

            lock (_writeLock)
            {
                File.AppendAllText(LogFilePath, sb.ToString());
            }
        }
        catch
        {
            // Audit writes never break the caller. Lost audit entries are tolerable;
            // a crashed Revit due to a logger exception is not.
        }
    }

    /// <summary>Indents every line of a multi-line string by the given prefix.</summary>
    private static string IndentLines(string text, string prefix)
    {
        if (string.IsNullOrEmpty(text)) return prefix + "(empty)";
        var lines = text.Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.Append(prefix).Append(line.TrimEnd('\r')).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }
}
