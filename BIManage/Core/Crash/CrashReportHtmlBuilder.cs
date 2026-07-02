using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;

namespace BIManage.Core.Crash
{
    /// <summary>
    /// Self-contained HTML renderer for the journal-analyzer's analysis JSON.
    ///
    /// History: the plugin originally fetched the analyzer website's dashboard HTML and
    /// injected the analysis JSON via global JS render functions (renderSummaryCards,
    /// renderSessionInfo, renderIssues, renderTimeline, renderAddins, renderWorkflow,
    /// renderKbArticles). That worked while the website's bundled script.js exposed
    /// exactly those names. Two field events broke that approach:
    ///   1. The website was rebuilt with renamed/removed render functions, so injection
    ///      silently rendered only the header cards — the Issues / Timeline / Add-ins /
    ///      Workflow / KB sections were empty in both the on-screen dashboard and the
    ///      Edge-headless PDF.
    ///   2. The fallback server endpoint /generate-pdf started returning HTTP 504/500 for
    ///      both application/json and text/plain payloads — verified in the BIManage log.
    /// Both server-side fixes are outside this repo's reach. This builder makes the plugin
    /// self-sufficient: parse the analysis JSON we already fetched from /upload/result/{id}
    /// and render a Zestine-branded HTML report directly. Edge headless then prints that
    /// HTML to a populated PDF — no dependency on the website's JS or PDF endpoint.
    ///
    /// Parsing is defensive: every section gracefully handles missing fields, missing
    /// arrays, or unexpected types so we never throw on schema drift.
    /// </summary>
    public static class CrashReportHtmlBuilder
    {
        /// <summary>
        /// Build a complete self-contained HTML report from the analyzer's result JSON.
        /// </summary>
        /// <param name="analysisJson">Either the unwrapped result body or the
        /// <c>{"result": {...}}</c> wrapper — both are accepted.</param>
        /// <param name="journalFileName">Display name for the file header (e.g. "journal.1447.txt").</param>
        public static string Build(string analysisJson, string journalFileName)
        {
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(analysisJson ?? "{}");
                // Unwrap { "result": { ... } } envelope if present.
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("result", out var inner)
                    && inner.ValueKind == JsonValueKind.Object)
                {
                    root = inner.Clone();
                }
                else
                {
                    root = doc.RootElement.Clone();
                }
            }
            catch
            {
                root = JsonDocument.Parse("{}").RootElement;
            }

            var summary = TryGetObject(root, "summary");
            var sessionInfo = TryGetObject(root, "session_info");
            var errors = TryGetArray(root, "errors");
            var timeline = TryGetArray(root, "timeline");
            var addins = TryGetArray(root, "addins");
            var workflow = TryGetObject(root, "workflow");
            var kbArticles = TryGetArray(root, "kb_articles");

            // Status banner: prefer summary.session_status, fall back to any top-level "status".
            var sessionStatus = TryGetString(summary, "session_status")
                ?? TryGetString(root, "status")
                ?? "Unknown";
            var isCrashed = string.Equals(sessionStatus, "Crashed", StringComparison.OrdinalIgnoreCase);

            // Summary card data — try a few common field names per metric for resilience.
            var revitVersion = TryGetString(sessionInfo, "revit_version")
                ?? TryGetString(summary, "revit_version")
                ?? TryGetString(root, "revit_version")
                ?? "Unknown";
            var totalErrors = TryGetInt(summary, "total_errors")
                ?? TryGetInt(summary, "error_count")
                ?? errors.Count;
            var unsavedWork = TryGetString(summary, "unsaved_work")
                ?? TryGetString(summary, "unsaved_duration")
                ?? TryGetString(root, "unsaved_work")
                ?? "N/A";
            var sessionDuration = TryGetString(summary, "session_duration")
                ?? TryGetString(sessionInfo, "session_duration")
                ?? "N/A";

            var sb = new StringBuilder(64 * 1024);
            sb.Append(@"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='UTF-8'>
<title>Revit Journal Analyzer · Comprehensive Analysis Report</title>
<style>
*{box-sizing:border-box;margin:0;padding:0;}
html,body{font-family:'Segoe UI',Arial,sans-serif;color:#1e293b;background:#ffffff;font-size:14px;line-height:1.5;}
body{padding:24px;}
.report{max-width:900px;margin:0 auto;}

/* Hero header — navy gradient matching the analyzer website's PDF */
.hero{position:relative;background:linear-gradient(135deg,#1e3a5f 0%,#2563aa 100%);color:#fff;padding:36px 32px 28px;border-radius:12px;text-align:center;overflow:hidden;}
.hero::before{content:'';position:absolute;left:0;right:0;top:0;height:4px;background:linear-gradient(90deg,#22c55e 0%,#16a34a 100%);}
.hero .zlogo{position:absolute;top:14px;right:18px;display:flex;align-items:center;gap:6px;color:#fff;font-size:13px;font-weight:700;}
.hero .zlogo .z{display:inline-block;width:18px;height:18px;background:#22c55e;border-radius:4px;}
.hero h1{font-size:30px;font-weight:800;letter-spacing:.3px;margin-bottom:6px;}
.hero .sub{font-size:13px;color:#cbd5e1;font-weight:500;letter-spacing:.4px;margin-bottom:18px;}
.hero .meta{font-size:12px;color:#e2e8f0;margin-bottom:18px;line-height:1.7;}
.hero .meta .label{color:#94a3b8;font-weight:500;margin-right:4px;}
.hero .badge{display:inline-block;padding:10px 36px;border-radius:24px;font-size:14px;font-weight:800;letter-spacing:.6px;}
.hero .badge.ok{background:#22c55e;color:#fff;}
.hero .badge.bad{background:#dc2626;color:#fff;}
.hero .pwd{margin-top:16px;font-size:10px;color:#94a3b8;font-weight:500;letter-spacing:.3px;}

/* Status cards row */
.cards{display:grid;grid-template-columns:repeat(5,1fr);gap:0;margin-top:22px;border:1px solid #e2e8f0;border-radius:8px;overflow:hidden;background:#fff;}
.card{padding:18px 12px;text-align:center;background:#fff;border-right:1px solid #e2e8f0;}
.card:last-child{border-right:0;}
.card .label{font-size:9px;color:#64748b;text-transform:uppercase;letter-spacing:.8px;font-weight:700;margin-bottom:8px;}
.card .val{font-size:18px;font-weight:800;color:#1e293b;}
.card .val.bad{color:#dc2626;}
.card .val.ok{color:#16a34a;}

/* Report Contents TOC — numbered list matching website's PDF */
.toc{margin-top:26px;}
.toc h2{font-size:20px;font-weight:800;color:#1e293b;margin-bottom:14px;}
.toc ol{list-style:none;counter-reset:tocn;}
.toc ol li{counter-increment:tocn;display:grid;grid-template-columns:34px 200px 1fr;gap:14px;padding:10px 0;border-bottom:1px solid #e2e8f0;align-items:start;}
.toc ol li::before{content:counter(tocn) '.';color:#64748b;font-weight:700;font-size:14px;}
.toc .name{color:#2563aa;font-weight:700;font-size:14px;text-decoration:underline;}
.toc .desc{color:#475569;font-size:12px;line-height:1.5;}

/* Section content blocks (page 2 onwards) */
.section{margin:18px 0;padding:18px 22px;border:1px solid #e2e8f0;border-radius:10px;background:#fff;page-break-inside:auto;}
.section h2{font-size:14px;font-weight:800;color:#0f172a;letter-spacing:.4px;text-transform:uppercase;margin-bottom:12px;border-bottom:2px solid #2563aa;padding-bottom:8px;}
.kv{display:grid;grid-template-columns:repeat(2,1fr);gap:10px 24px;}
.kv .row{display:flex;justify-content:space-between;border-bottom:1px solid #f1f5f9;padding:6px 0;}
.kv .row .k{color:#64748b;font-size:12px;font-weight:600;}
.kv .row .v{color:#1e293b;font-size:12px;font-weight:600;text-align:right;}
table{width:100%;border-collapse:collapse;font-size:12px;}
thead th{background:#f1f5f9;color:#0f172a;text-align:left;padding:8px 10px;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:.4px;border-bottom:1px solid #e2e8f0;}
tbody td{padding:8px 10px;border-bottom:1px solid #f1f5f9;vertical-align:top;color:#334155;}
tbody tr:last-child td{border-bottom:0;}
.tag{display:inline-block;padding:2px 8px;border-radius:10px;font-size:10px;font-weight:700;letter-spacing:.3px;}
.tag.err{background:#fee2e2;color:#991b1b;}
.tag.warn{background:#fef3c7;color:#92400e;}
.tag.info{background:#dbeafe;color:#1e40af;}
.empty{color:#94a3b8;font-style:italic;font-size:12px;text-align:center;padding:20px;}

.page-break{page-break-before:always;}
@media print{
  body{background:#fff;padding:0;}
  .report{max-width:none;}
  .hero,.cards,.toc,.section{break-inside:avoid;}
  .section{margin:10px 0;}
}
</style>
</head>
<body>
<div class='report'>");

            // ───── Hero / cover header — matches the website's 'Comprehensive Analysis Report' PDF
            sb.Append("<div class='hero'>");
            sb.Append("<div class='zlogo'><span class='z'></span>Zestine<span style='color:#94a3b8;font-weight:400;margin-left:2px;'>Technologies</span></div>");
            sb.Append("<h1>Revit Journal Analyzer</h1>");
            sb.Append("<div class='sub'>Comprehensive Analysis Report</div>");
            sb.Append("<div class='meta'><span class='label'>File:</span>").Append(H(journalFileName)).Append("<br/>");
            sb.Append("<span class='label'>Generated:</span>").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("</div>");
            if (isCrashed)
                sb.Append("<div class='badge bad'>CRASH DETECTED</div>");
            else
                sb.Append("<div class='badge ok'>NO CRASH</div>");
            sb.Append("<div class='pwd'>Powered by ZestineTechnologies</div>");
            sb.Append("</div>");

            // ───── Summary cards (5-up row immediately under hero)
            sb.Append("<div class='cards'>");
            sb.Append(StatCard("Status", sessionStatus, isCrashed ? "bad" : "ok"));
            sb.Append(StatCard("Revit Version", revitVersion, ""));
            sb.Append(StatCard("Total Errors", totalErrors.ToString(), totalErrors > 0 ? "bad" : "ok"));
            sb.Append(StatCard("Unsaved Work", unsavedWork, ""));
            sb.Append(StatCard("Session Duration", sessionDuration, ""));
            sb.Append("</div>");

            // ───── Report Contents (numbered TOC, mirrors the website's PDF page 1)
            sb.Append("<div class='toc'><h2>Report Contents</h2><ol>");
            sb.Append("<li><span class='name'>Session Information</span><span class='desc'>Revit version, machine, OS, RAM, GPU, models, views/sheets, unsaved work</span></li>");
            sb.Append("<li><span class='name'>Issues &amp; Errors</span><span class='desc'>Fatal errors, errors, warnings, exceptions</span></li>");
            sb.Append("<li><span class='name'>Session Timeline</span><span class='desc'>Chronological event log with timestamps</span></li>");
            sb.Append("<li><span class='name'>Add-ins</span><span class='desc'>Failed, third-party and Autodesk add-ins</span></li>");
            sb.Append("<li><span class='name'>Workflow &mdash; Longest Delays</span><span class='desc'>Significant delays (&gt;5 sec) between consecutive events</span></li>");
            sb.Append("<li><span class='name'>Known Issues &amp; KB Articles</span><span class='desc'>Matched Autodesk knowledge base articles with links</span></li>");
            sb.Append("</ol></div>");

            // Page break — sections start on page 2 (matches website PDF)
            sb.Append("<div class='page-break'></div>");

            // Session Information
            sb.Append("<div class='section'><h2>Session Information</h2>");
            sb.Append("<div class='kv'>");
            // Iterate every primitive field on session_info; fall back to root if empty.
            var infoSource = sessionInfo.ValueKind == JsonValueKind.Object ? sessionInfo : root;
            int rendered = 0;
            if (infoSource.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in infoSource.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String
                        || prop.Value.ValueKind == JsonValueKind.Number
                        || prop.Value.ValueKind == JsonValueKind.True
                        || prop.Value.ValueKind == JsonValueKind.False)
                    {
                        sb.Append("<div class='row'><span class='k'>").Append(H(Humanize(prop.Name))).Append("</span><span class='v'>").Append(H(prop.Value.ToString())).Append("</span></div>");
                        rendered++;
                    }
                }
            }
            if (rendered == 0)
                sb.Append("<div class='empty' style='grid-column:span 2;'>No session details available.</div>");
            sb.Append("</div></div>");

            // Issues & Errors
            sb.Append("<div class='section'><h2>Issues &amp; Errors (").Append(errors.Count).Append(")</h2>");
            AppendItemTable(sb, errors, new[] { "severity", "type", "message", "timestamp", "line" }, new[] { "Severity", "Type", "Message", "Timestamp", "Line" }, severityCol: 0);
            sb.Append("</div>");

            // Timeline
            sb.Append("<div class='section'><h2>Session Timeline (").Append(timeline.Count).Append(" events)</h2>");
            AppendItemTable(sb, timeline, new[] { "timestamp", "event", "description", "category", "duration" }, new[] { "Timestamp", "Event", "Description", "Category", "Duration" });
            sb.Append("</div>");

            // Add-ins
            sb.Append("<div class='section'><h2>Add-ins (").Append(addins.Count).Append(")</h2>");
            AppendItemTable(sb, addins, new[] { "name", "vendor", "version", "status", "load_time", "path" }, new[] { "Name", "Vendor", "Version", "Status", "Load Time", "Path" });
            sb.Append("</div>");

            // Workflow / Longest Delays
            sb.Append("<div class='section'><h2>Workflow — Longest Delays</h2>");
            var delays = TryGetArray(workflow, "longest_delays");
            if (delays.Count == 0) delays = TryGetArray(workflow, "delays");
            if (delays.Count == 0 && workflow.ValueKind == JsonValueKind.Array)
                delays = workflow.EnumerateArray().ToList();
            AppendItemTable(sb, delays, new[] { "duration", "from_event", "to_event", "timestamp", "description" }, new[] { "Duration", "From", "To", "Timestamp", "Description" });
            sb.Append("</div>");

            // KB Articles
            sb.Append("<div class='section'><h2>Known Issues &amp; KB Articles (").Append(kbArticles.Count).Append(")</h2>");
            if (kbArticles.Count == 0)
                sb.Append("<div class='empty'>No matched Autodesk KB articles for this session.</div>");
            else
            {
                sb.Append("<table><thead><tr><th>Title</th><th>Issue</th><th>Link</th></tr></thead><tbody>");
                foreach (var a in kbArticles)
                {
                    var title = TryGetString(a, "title") ?? TryGetString(a, "name") ?? "—";
                    var issue = TryGetString(a, "issue") ?? TryGetString(a, "description") ?? "";
                    var url = TryGetString(a, "url") ?? TryGetString(a, "link") ?? "";
                    sb.Append("<tr><td>").Append(H(title)).Append("</td><td>").Append(H(issue)).Append("</td><td>");
                    if (!string.IsNullOrEmpty(url))
                        sb.Append("<a href='").Append(H(url)).Append("' target='_blank' rel='noopener'>Open ↗</a>");
                    sb.Append("</td></tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</div>");

            // Footer
            sb.Append("<div class='footer'><div class='brand'><span class='mark' style='color:#FF5500;'>Ze'</span><span class='text' style='color:#0f172a;'>Manage</span> · Journal Analyzer Report</div>Powered by ZestineTechnologies</div>");

            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        // ---- helpers ----

        private static string StatCard(string label, string value, string valueClass)
        {
            return $"<div class='card'><div class='label'>{H(label)}</div><div class='val {valueClass}'>{H(value)}</div></div>";
        }

        /// <summary>
        /// Render a table of objects from a JSON array. Picks the first matching key from
        /// <paramref name="candidateKeys"/> for each column so the same renderer works
        /// against minor schema variations. Empty array → friendly "no data" message.
        /// </summary>
        private static void AppendItemTable(StringBuilder sb, List<JsonElement> items, string[] candidateKeys, string[] headers, int severityCol = -1)
        {
            if (items == null || items.Count == 0)
            {
                sb.Append("<div class='empty'>No entries.</div>");
                return;
            }

            sb.Append("<table><thead><tr>");
            for (int i = 0; i < headers.Length; i++)
                sb.Append("<th>").Append(H(headers[i])).Append("</th>");
            sb.Append("</tr></thead><tbody>");

            foreach (var item in items)
            {
                sb.Append("<tr>");
                for (int i = 0; i < candidateKeys.Length; i++)
                {
                    var v = TryGetString(item, candidateKeys[i]) ?? "";
                    if (i == severityCol && !string.IsNullOrEmpty(v))
                    {
                        var sevClass = v.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ? "err"
                                     : v.IndexOf("warn", StringComparison.OrdinalIgnoreCase) >= 0 ? "warn"
                                     : "info";
                        sb.Append("<td><span class='tag ").Append(sevClass).Append("'>").Append(H(v)).Append("</span></td>");
                    }
                    else
                    {
                        sb.Append("<td>").Append(H(v)).Append("</td>");
                    }
                }
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table>");
        }

        private static JsonElement TryGetObject(JsonElement parent, string key)
        {
            if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object)
                return v;
            return JsonDocument.Parse("{}").RootElement;
        }

        private static List<JsonElement> TryGetArray(JsonElement parent, string key)
        {
            var result = new List<JsonElement>();
            if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in v.EnumerateArray())
                    result.Add(item);
            }
            return result;
        }

        private static string? TryGetString(JsonElement parent, string key)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            if (!parent.TryGetProperty(key, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => null,
            };
        }

        private static int? TryGetInt(JsonElement parent, string key)
        {
            if (parent.ValueKind != JsonValueKind.Object) return null;
            if (!parent.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
            return null;
        }

        /// <summary>
        /// "session_status" → "Session Status"; "revitVersion" → "Revit Version".
        /// </summary>
        private static string Humanize(string snakeOrCamel)
        {
            if (string.IsNullOrEmpty(snakeOrCamel)) return snakeOrCamel;
            var withSpaces = new StringBuilder(snakeOrCamel.Length + 8);
            for (int i = 0; i < snakeOrCamel.Length; i++)
            {
                var c = snakeOrCamel[i];
                if (c == '_') { withSpaces.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(snakeOrCamel[i - 1]))
                    withSpaces.Append(' ');
                withSpaces.Append(c);
            }
            var s = withSpaces.ToString().Trim();
            return s.Length == 0 ? snakeOrCamel : char.ToUpper(s[0]) + s.Substring(1);
        }

        private static string H(string? s) => string.IsNullOrEmpty(s) ? "" : WebUtility.HtmlEncode(s);
    }
}
