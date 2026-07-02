using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BIManage.AI
{
    /// <summary>
    /// Lightweight diagnostic logger for the AI model-context pipeline.
    /// Writes a structured plaintext trace to:
    ///   %LOCALAPPDATA%\BIManageRevit\AI\Logs\ai_diag_YYYYMMDD.log
    ///
    /// Each line is prefixed with a timestamp and a step tag so you can
    /// follow the exact flow from GUID extraction → API call → data mapping → prompt injection.
    /// </summary>
    public static class AiDiagLog
    {
        private static readonly object _lock = new object();
        private static readonly string _logDir;

        static AiDiagLog()
        {
            _logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BIManageRevit", "AI", "Logs");

            try { Directory.CreateDirectory(_logDir); }
            catch { /* non-critical */ }
        }

        private static string LogFilePath
            => Path.Combine(_logDir, $"ai_diag_{DateTime.Now:yyyyMMdd}.log");

        // ──────────────────────────────────────────────────────────────────────
        // Public helpers — one per pipeline stage
        // ──────────────────────────────────────────────────────────────────────

        public static void SessionStart(string providerName, string? modelGuid, string? docTitle)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("════════════════════════════════════════════════════════════");
            sb.AppendLine($"  AI SESSION STARTED  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("════════════════════════════════════════════════════════════");
            sb.AppendLine($"  Provider   : {providerName}");
            sb.AppendLine($"  Document   : {docTitle ?? "(null)"}");
            sb.AppendLine($"  Model GUID : {modelGuid ?? "── NOT EXTRACTED ──"}");
            if (string.IsNullOrEmpty(modelGuid))
                sb.AppendLine("  ⚠ GUID is null — context enrichment will be SKIPPED for all queries");
            sb.AppendLine("────────────────────────────────────────────────────────────");
            Write(sb.ToString());
        }

        public static void QueryReceived(string userText, bool isModelQuery, string? modelGuid)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"[{Ts()}] ▶ QUERY RECEIVED");
            sb.AppendLine($"           Text     : \"{Truncate(userText, 200)}\"");
            sb.AppendLine($"           IsModel? : {isModelQuery}");
            sb.AppendLine($"           GUID     : {modelGuid ?? "(null — will skip)"}");
            if (!isModelQuery)
                sb.AppendLine("           ℹ  Not a model-analysis query — no API fetch, no context injected");
            Write(sb.ToString());
        }

        public static void AuthCheck(bool isAuthenticated)
        {
            var icon = isAuthenticated ? "✓" : "ℹ";
            var note = isAuthenticated
                ? "  (token will be attached to requests)"
                : "  (no token — open endpoint will still be called; locked endpoint will get 401)";
            Write($"[{Ts()}] {icon} AUTH STATE — IsAuthenticated={isAuthenticated}{note}");
        }

        public static void ApiCallStart(string endpoint)
            => Write($"[{Ts()}]   → API GET {endpoint}");

        public static void ApiCallResult(string endpoint, int httpStatus, int totalRecords, int matchingRecords, DateTime? latestDate)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{Ts()}]   ← {endpoint}");
            sb.AppendLine($"             HTTP {httpStatus}  |  total records={totalRecords}  |  matching GUID={matchingRecords}");
            if (matchingRecords == 0)
                sb.AppendLine("             ⚠ NO RECORDS matched the current model GUID — this endpoint returned nothing useful");
            else
                sb.AppendLine($"             Latest capturedAt = {latestDate:yyyy-MM-dd HH:mm} UTC");
            Write(sb.ToString());
        }

        public static void ApiCallFailed(string endpoint, int httpStatus, string body)
            => Write($"[{Ts()}]   ✗ {endpoint} FAILED — HTTP {httpStatus}\n             Body: {Truncate(body, 300)}");

        public static void ApiCallError(string endpoint, string exMessage)
            => Write($"[{Ts()}]   ✗ {endpoint} EXCEPTION — {exMessage}");

        public static void ContextResult(bool hasManual, bool hasPeriodic, bool hasSyncSave)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{Ts()}] ℹ CONTEXT FETCH RESULT");
            sb.AppendLine($"           Manual   : {(hasManual   ? "✓ data available" : "✗ null")}");
            sb.AppendLine($"           Periodic : {(hasPeriodic ? "✓ data available" : "✗ null")}");
            sb.AppendLine($"           SyncSave : {(hasSyncSave ? "✓ data available" : "✗ null")}");
            if (!hasManual && !hasPeriodic && !hasSyncSave)
                sb.AppendLine("           ⚠ ALL NULL — AI will get NO model data. Check GUID match and API records.");
            Write(sb.ToString());
        }

        public static void ContextInjected(int charCount)
            => Write($"[{Ts()}] ✓ CONTEXT INJECTED into system prompt — {charCount} chars");

        public static void ContextSkipped(string reason)
            => Write($"[{Ts()}] ⚠ CONTEXT SKIPPED — {reason}");

        public static void AiResponse(string response, long elapsedMs)
        {
            var preview = Truncate(response.Replace("\n", " "), 250);
            Write($"[{Ts()}] ✓ AI RESPONSE ({elapsedMs}ms) — \"{preview}\"");
        }

        /// <summary>
        /// Logs the full question and response text (untruncated) to a separate
        /// conversation log file for debugging and review:
        ///   %LOCALAPPDATA%\BIManageRevit\AI\Logs\ai_conversations_YYYYMMDD.log
        /// </summary>
        public static void FullConversationEntry(
            string userMessage,
            string aiResponse,
            long elapsedMs,
            string providerName,
            string? modelGuid = null)
        {
            try
            {
                var conversationLogPath = Path.Combine(_logDir, $"ai_conversations_{DateTime.Now:yyyyMMdd}.log");
                var sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine($"══ [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Provider: {providerName} | Model: {modelGuid ?? "N/A"} | {elapsedMs}ms ══");
                sb.AppendLine();
                sb.AppendLine("USER:");
                sb.AppendLine(userMessage);
                sb.AppendLine();
                sb.AppendLine("ASSISTANT:");
                sb.AppendLine(aiResponse);
                sb.AppendLine();
                sb.AppendLine(new string('─', 80));

                lock (_lock)
                {
                    File.AppendAllText(conversationLogPath, sb.ToString());
                }
            }
            catch { /* non-critical */ }
        }

        public static void Separator()
            => Write("────────────────────────────────────────────────────────────");

        // ──────────────────────────────────────────────────────────────────────
        // Tool-calling pipeline helpers (Phase 3c-NEW: OpenAI function calling)
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Logs a single OpenAI tool call as it's about to be dispatched. Use this to
        /// confirm which tool the LLM picked and what arguments it passed.
        /// </summary>
        public static void ToolCall(int round, string toolName, string argumentsJson)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{Ts()}] 🔧 TOOL CALL — round {round}, tool '{toolName}'");
            sb.AppendLine($"           Arguments: {Truncate(argumentsJson.Replace("\n", " "), 500)}");
            Write(sb.ToString());
        }

        /// <summary>
        /// Logs the result of a tool call after the dispatcher returns. Use this to
        /// confirm the tool executed correctly and see what the LLM will receive next.
        /// </summary>
        public static void ToolResult(int round, string toolName, string resultJson, long elapsedMs)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{Ts()}] ↩ TOOL RESULT — round {round}, tool '{toolName}', {elapsedMs}ms");
            sb.AppendLine($"           Result   : {Truncate(resultJson.Replace("\n", " "), 500)}");
            Write(sb.ToString());
        }

        // ──────────────────────────────────────────────────────────────────────
        // Visibility diagnosis pipeline helpers
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Written at the start of every visibility diagnosis run.
        /// </summary>
        public static void VisibilityDiagStart(
            IList<int> elementIds,
            string? activeViewName,
            string? activeViewType)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"[{Ts()}] ▶ VISIBILITY DIAGNOSIS START");
            sb.AppendLine($"           Element IDs parsed : [{string.Join(", ", elementIds)}] ({elementIds.Count} element(s))");
            sb.AppendLine($"           Active view        : {activeViewName ?? "(null)"} [{activeViewType ?? "—"}]");
            Write(sb.ToString());
        }

        /// <summary>Written when no element ID was found in the user message.</summary>
        public static void VisibilityNoId()
            => Write($"[{Ts()}] ⚠ VISIBILITY DIAGNOSIS — no element ID found in query; returning generic guidance block");

        /// <summary>
        /// Written each time a view resolution decision is made.
        /// <paramref name="requestedName"/> is the view name from the user message (null if not mentioned).
        /// <paramref name="resolution"/> describes why this view was chosen/skipped.
        /// <paramref name="resolvedName"/> is the actual view name used (null if none).
        /// </summary>
        public static void VisibilityViewResolved(
            string? requestedName,
            string resolution,
            string? resolvedName)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{Ts()}]   VIEW RESOLUTION");
            if (requestedName != null)
                sb.AppendLine($"             Requested : \"{requestedName}\"");
            sb.AppendLine($"             Decision  : {resolution}");
            sb.AppendLine($"             Using view: {resolvedName ?? "(none — generic guidance only)"}");
            Write(sb.ToString());
        }

        /// <summary>
        /// Written after each element is diagnosed.
        /// <paramref name="issueCount"/> = -1 means view was null (no checks run);
        /// 0 means all 17 checks passed; > 0 means issues were confirmed.
        /// </summary>
        public static void VisibilityElementResult(
            int elementId,
            string? elementName,
            string? category,
            string? viewName,
            int issueCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{Ts()}]   ELEMENT {elementId}");
            sb.AppendLine($"             Name     : {elementName ?? "(not found)"}");
            sb.AppendLine($"             Category : {category ?? "—"}");
            sb.AppendLine($"             View     : {viewName ?? "(none)"}");

            if (issueCount == -2)
                sb.AppendLine("             Result   : ✗ NOT FOUND in document (element ID does not exist)");
            else if (issueCount == -1)
                sb.AppendLine("             Result   : ⚠ NO VIEW — generic guidance only (no live checks run)");
            else if (issueCount == 0)
                sb.AppendLine("             Result   : ✓ CLEAN — all 17 checks passed, no visibility issue detected");
            else
                sb.AppendLine($"             Result   : ✗ {issueCount} ISSUE(S) FOUND");

            Write(sb.ToString());
        }

        // ──────────────────────────────────────────────────────────────────────

        private static void Write(string text)
        {
            lock (_lock)
            {
                try { File.AppendAllText(LogFilePath, text + "\n"); }
                catch { /* non-critical */ }
            }
        }

        private static string Ts() => DateTime.Now.ToString("HH:mm:ss.fff");

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
