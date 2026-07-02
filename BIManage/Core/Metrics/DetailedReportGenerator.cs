using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BIManage.Infrastructure.Api;

namespace BIManage.Core.Metrics
{
    public static class DetailedReportGenerator
    {
        public static string GenerateAndOpen(
            string modelName, string? modelPath, string? modelGuid, string capturedBy,
            FastMetrics? fast, MediumMetrics? medium, ExpensiveMetrics? expensive,
            DetailedMetrics? detailed = null,
            HealthMonitorProtection? healthGoals = null)
        {
            var dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(dl)) dl = Path.GetTempPath();
            var safe = string.Join("_", (modelName ?? "Model").Split(Path.GetInvalidFileNameChars()));
            // Single timestamp shared across the HTML and PDF filenames so the
            // Download PDF link inside the HTML resolves to a sibling file with
            // the matching stamp.
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var baseName = $"ZeManage_DetailedReport_{safe}_{ts}";
            var fp = Path.Combine(dl, baseName + ".html");
            var pdfFileName = baseName + ".pdf";
            var pdfPath = Path.Combine(dl, pdfFileName);

            // Two-pass HTML generation so the Download PDF button can trigger
            // a real download (Blob URL → programmatic <a download> click)
            // instead of opening the PDF in a new browser tab — which is what
            // Chrome/Edge do when an <a download> link points at a file:// PDF
            // (the `download` attribute is silently ignored for cross-origin
            // file:// resources).
            //
            // Pass 1: write HTML without embedded PDF data. Edge headless reads
            //         this file to render the PDF (the button is hidden in
            //         @media print so it doesn't appear inside the PDF).
            // Pass 2: read the PDF bytes from disk, base64-encode them, and
            //         regenerate the HTML with that base64 baked into a JS
            //         constant. The button's onclick decodes the base64 in
            //         memory, builds a Blob, creates a Blob URL, and triggers
            //         download. This is the same pattern html2pdf.js used
            //         internally — just with the PDF coming from Edge instead
            //         of from jsPDF.
            var htmlPass1 = Generate(modelName, modelPath, modelGuid, capturedBy, fast, medium, expensive, detailed, pdfFileName, healthGoals: healthGoals);
            File.WriteAllText(fp, htmlPass1, Encoding.UTF8);

            // Render the HTML to a PDF by shelling out to Microsoft Edge in
            // headless mode. Edge ships preinstalled on every Windows 10/11
            // machine and uses Chromium's full rendering engine — the same
            // engine that paints the HTML on screen and the same engine that
            // window.print() invokes when the user picks "Save as PDF". This
            // means anything that looks right in the browser prints right:
            // grid layouts, gradients, base64 images, web fonts, custom CSS —
            // all of it.
            //
            // The PDF is vector (each glyph is drawn as a primitive on a
            // discrete page) so text is never sliced through a page boundary,
            // unlike html2pdf.js which paints a giant bitmap then slices it.
            //
            // No NuGet package, no AGPL — just a Process.Start.
            string? pdfBase64 = null;
            try
            {
                var edgePath = LocateChromiumBrowser();
                if (edgePath == null)
                {
                    throw new FileNotFoundException(
                        "Neither Microsoft Edge nor Google Chrome was found in Program Files or "
                        + "Program Files (x86). Edge ships preinstalled with Windows 10/11; "
                        + "install it from https://www.microsoft.com/edge or install Chrome from "
                        + "https://www.google.com/chrome. The HTML report will still open and "
                        + "the in-page html2pdf.js fallback will generate a (bitmap-based) PDF.");
                }

                // --headless           run without a window
                // --disable-gpu        avoid GPU init in headless mode (recommended by Chromium docs)
                // --no-pdf-header-footer  drop the auto-injected URL/date header & page-number footer
                // --print-to-pdf=<out> write a vector PDF directly to disk, no print dialog
                // <input.html>         absolute path to the HTML we just wrote
                //
                // Both paths are quoted to handle spaces in usernames / model names.
                var psi = new ProcessStartInfo
                {
                    FileName               = edgePath,
                    Arguments              = $"--headless --disable-gpu --no-pdf-header-footer "
                                           + $"--print-to-pdf=\"{pdfPath}\" \"{fp}\"",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardError  = true,
                    RedirectStandardOutput = true,
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null)
                        throw new InvalidOperationException("Process.Start returned null for msedge.exe.");

                    // 60s budget covers cold-start (~1-3s) + render time for the
                    // current report size. If Edge hasn't finished by then,
                    // something is wrong — kill it and surface the timeout.
                    if (!proc.WaitForExit(60_000))
                    {
                        try { proc.Kill(); } catch { }
                        throw new TimeoutException(
                            "Edge headless did not finish PDF generation within 60 seconds.");
                    }

                    if (proc.ExitCode != 0)
                    {
                        var stderr = proc.StandardError.ReadToEnd();
                        throw new InvalidOperationException(
                            $"Edge headless exited with code {proc.ExitCode}. stderr: {stderr}");
                    }
                }

                // Read the PDF back and base64-encode it for embedding in
                // pass-2 HTML. After embedding, the sibling .pdf file on disk
                // is redundant — the JS download button uses the in-memory
                // Blob — so we delete it to keep Downloads tidy.
                if (File.Exists(pdfPath))
                {
                    pdfBase64 = Convert.ToBase64String(File.ReadAllBytes(pdfPath));
                    try { File.Delete(pdfPath); } catch { /* sibling cleanup is best-effort */ }
                }
            }
            catch (Exception ex)
            {
                // Don't crash the report flow if PDF generation fails — the
                // HTML still opens (the button will be disabled with a tooltip).
                // Write a sibling .error.txt with the exception so the user
                // can see exactly what went wrong (Debug.WriteLine alone is
                // invisible outside a debugger).
                try
                {
                    var errPath = Path.Combine(dl, baseName + ".error.txt");
                    File.WriteAllText(errPath,
                        $"Edge headless PDF generation failed at {DateTime.Now:O}\r\n\r\n" +
                        $"Exception type: {ex.GetType().FullName}\r\n" +
                        $"Message: {ex.Message}\r\n\r\n" +
                        $"Full ToString:\r\n{ex}\r\n");
                }
                catch { /* never let diagnostics itself crash */ }
                System.Diagnostics.Debug.WriteLine($"[ZeManage] Edge PDF generation failed: {ex}");
            }

            // Pass 2: regenerate HTML with the base64 PDF baked in (or null
            // if generation failed → button will show "PDF unavailable").
            var htmlPass2 = Generate(modelName, modelPath, modelGuid, capturedBy, fast, medium, expensive, detailed, pdfFileName, pdfBase64, healthGoals);
            File.WriteAllText(fp, htmlPass2, Encoding.UTF8);

            Process.Start(new ProcessStartInfo { FileName = fp, UseShellExecute = true });
            return fp;
        }

        // Locates a Chromium-based browser to run in --headless --print-to-pdf
        // mode. Tries Microsoft Edge first (preinstalled on Windows 10/11), then
        // Google Chrome (user-installed; common on BIM workstations because of
        // cloud tools like BIM 360 / Bluebeam Studio).
        //
        // Both browsers are Chromium under the hood and accept the EXACT same
        // command-line flags, so once we have a path to either one the rest of
        // the PDF-generation code is unchanged.
        //
        // Returns null if neither is installed; the caller then falls back to
        // the in-page html2pdf.js client-side path inside the HTML report.
        private static string? LocateChromiumBrowser()
        {
            var candidates = new[]
            {
                // Microsoft Edge — preinstalled on Win10 21H2+ / Win11.
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                             "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             "Microsoft", "Edge", "Application", "msedge.exe"),
                // Google Chrome — user-installed fallback for the rare machine
                // that has had Edge stripped by IT but still has a Chromium browser.
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                             "Google", "Chrome", "Application", "chrome.exe"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        static string V(int? v) => v?.ToString("N0") ?? "0";
        static string FS(long? b) { if (!b.HasValue) return "N/A"; double mb = b.Value / (1024.0 * 1024); return mb >= 1024 ? $"{mb / 1024:F2} GB" : $"{mb:F1} MB"; }
        static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "N/A");

        public static string Generate(
            string modelName, string? modelPath, string? modelGuid, string capturedBy,
            FastMetrics? fast, MediumMetrics? medium, ExpensiveMetrics? expensive,
            DetailedMetrics? detailed = null,
            string? pdfFileName = null,
            string? pdfBase64 = null,
            HealthMonitorProtection? healthGoals = null)
        {
            var f = fast ?? new FastMetrics();
            var m = medium ?? new MediumMetrics();
            var e = expensive ?? new ExpensiveMetrics();
            var d = detailed ?? new DetailedMetrics();
            var now = DateTime.Now;

            // Prefer admin-configured API goal when available; fall back to the default that
            // the dashboard uses so the report and dashboard show the same threshold for a metric.
            // Match the dashboard's null-or-zero semantics: a server value of 0 means
            // "admin has not configured this goal" → use the default, NOT a literal 0
            // (which previously produced "Goal: < 0 FAIL" on the report while the dashboard
            // showed the proper fallback goal — e.g. Warnings 200).
            int Goal(int? apiVal, int defaultVal) => apiVal.HasValue && apiVal.Value > 0 ? apiVal.Value : defaultVal;

            int warnGoal = Goal(healthGoals?.MaximumWarningCount, 200);
            int dupGoal  = Goal(healthGoals?.MaxDuplicateElementsCount, 50);
            int idwGoal  = Goal(healthGoals?.MaxImportedDwgCount, 1);
            int nnGoal   = Goal(healthGoals?.MaxNonNativeObjectStylesCount, 1);
            int inpGoal  = Goal(healthGoals?.MaxInPlaceFamilyCount, 1);
            int ovrGoal  = Goal(healthGoals?.MaxFamiliesOver5MbCount, 1);
            int prgGoal  = Goal(healthGoals?.MaxPurgeableElementsCount, 100);
            // Model Quality Scan + Disconnects per-row goals. When the admin
            // hasn't configured the threshold on web we fall back to 1, which
            // produces the existing "Goal: 0" display + "any > 0 → FAIL" rule.
            int upGoal   = Goal(healthGoals?.MaxUnplacedRoomsCount,    1);
            int ueGoal   = Goal(healthGoals?.MaxUnenclosedRoomsCount,  1);
            int wlGoal   = Goal(healthGoals?.MaxWallsNotConnectedCount, 1);
            int ppGoal   = Goal(healthGoals?.MaxPipesNotConnectedCount, 1);
            int dcGoal   = Goal(healthGoals?.MaxDuctsNotConnectedCount, 1);
            // Additional API-only goals — null/zero means "admin didn't configure",
            // in which case the row stays info-only (no Goal line, no PASS/FAIL pill),
            // matching the dashboard's behaviour for unconfigured thresholds.
            int lvlApi   = healthGoals?.MaxLevelsCount        ?? 0;
            int grdApi   = healthGoals?.MaxGridsCount         ?? 0;
            int lrevApi  = healthGoals?.MaxLinkedRevitCount   ?? 0;
            int wkApi    = healthGoals?.MaxTotalWorksetsCount ?? 0;
            int fmApi    = healthGoals?.MaxTotalFamiliesCount ?? 0;
            int tvApi    = healthGoals?.MaxTotalViewsCount    ?? 0;
            int vnsApi   = healthGoals?.ViewsNotOnSheet.HasValue == true ? (int)healthGoals!.ViewsNotOnSheet!.Value : 0;
            int lDwgApi  = healthGoals?.MaxLinkedDwgCount     ?? 0;
            int rastApi  = healthGoals?.MaxRasterImagesCount  ?? 0;

            // Health score
            int tp = 0, tf = 0;
            bool PF(int v, int g) { bool p = v < g; if (p) tp++; else tf++; return p; }
            // Optional PF used by rows whose threshold is admin-configurable.
            // When the admin hasn't set a value (apiGoal == 0) the row is NOT
            // counted in tp/tf, so the overall health score reflects only the
            // metrics the admin actually opted into — same rule the dashboard uses.
            bool PFOpt(int v, int apiGoal, out bool counted)
            {
                if (apiGoal <= 0) { counted = false; return true; }
                counted = true;
                bool p = v < apiGoal;
                if (p) tp++; else tf++;
                return p;
            }
            bool warnP = PF(f.WarningsCount ?? 0, warnGoal);
            bool dupP = PF(f.DuplicateElementsCount ?? 0, dupGoal);
            bool grpP = PF((f.ModelGroupsCount ?? 0) + (f.DetailGroupsCount ?? 0), 50);
            bool inpP = PF(m.InplaceFamiliesCount ?? 0, inpGoal);
            bool prgP = PF(e.PurgeableElementsCount ?? 0, prgGoal);
            bool ovrP = PF(e.FamiliesOver5MbCount ?? 0, ovrGoal);
            bool nnP = PF(f.NonNativeObjectStylesCount ?? 0, nnGoal);
            bool idwP = PF(f.ImportedDwgCount ?? 0, idwGoal);
            bool wlP = PF(m.WallsNotConnectedCount ?? 0, wlGoal);
            bool ppP = PF(m.PipesNotConnectedCount ?? 0, ppGoal);
            bool dcP = PF(m.DuctsNotConnectedCount ?? 0, dcGoal);
            bool upP = PF(m.UnplacedRoomsCount ?? 0, upGoal);
            bool ueP = PF(m.UnenclosedRoomsCount ?? 0, ueGoal);
            // Optional rows — only contribute to score when admin set a threshold.
            bool lvlP   = PFOpt(f.LevelsCount         ?? 0, lvlApi,  out _);
            bool grdP   = PFOpt(f.GridsCount          ?? 0, grdApi,  out _);
            bool lrevP  = PFOpt(f.LinkedRevitCount    ?? 0, lrevApi, out _);
            bool wkP    = PFOpt(f.TotalWorksetsCount  ?? 0, wkApi,   out _);
            bool fmP    = PFOpt(f.TotalFamiliesCount  ?? 0, fmApi,   out _);
            bool tvP    = PFOpt(f.TotalViewsCount     ?? 0, tvApi,   out _);
            bool vnsP   = PFOpt(m.ViewsNotOnSheetsCount ?? 0, vnsApi, out _);
            bool lDwgP  = PFOpt(f.LinkedDwgCount      ?? 0, lDwgApi, out _);
            bool rastP  = PFOpt(f.RasterImagesCount   ?? 0, rastApi, out _);
            int pct = (tp + tf) > 0 ? (int)(100.0 * tp / (tp + tf)) : 100;
            string pctClr = pct >= 75 ? "#16a34a" : pct >= 50 ? "#f59e0b" : "#dc2626";
            string pctLbl = pct >= 75 ? "Healthy" : pct >= 50 ? "Warning" : "Needs Attention";

            // Element breakdown
            int totalEl = m.TotalElementsCount ?? 0;
            int modEl = m.ModelElementsCount ?? 0;
            int annEl = m.AnnotativeElementsCount ?? 0;
            int othEl = Math.Max(0, totalEl - modEl - annEl);
            double modPct = totalEl > 0 ? 100.0 * modEl / totalEl : 0;
            double annPct = totalEl > 0 ? 100.0 * annEl / totalEl : 0;
            double othPct = totalEl > 0 ? 100.0 * othEl / totalEl : 0;

            // File size — MaxModelSize from API is in MB (matches the dashboard's
            // interpretation). When the admin hasn't set a goal, fall back to 500 MB
            // so the report still shows the same default the dashboard's File Size
            // ring uses for an unconfigured threshold.
            long fsGoalMB = (healthGoals?.MaxModelSize ?? 0) > 0 ? healthGoals!.MaxModelSize!.Value : 500L;
            long fsGoal = fsGoalMB * 1024 * 1024;
            double fsPctV = fsGoal > 0 && f.FileSizeBytes.HasValue ? Math.Min(1.0, (double)f.FileSizeBytes.Value / fsGoal) : 0;
            string fsClr = fsPctV < 0.5 ? "#16a34a" : fsPctV < 0.8 ? "#f59e0b" : "#dc2626";

            // Bar chart maximums
            int statMax = Math.Max(1, Math.Max(f.LevelsCount ?? 0, Math.Max(f.GridsCount ?? 0, f.LinkedRevitCount ?? 0)));
            int vsMax = Math.Max(1, Math.Max(f.TotalViewsCount ?? 0, f.SheetsCount ?? 0));
            int wfMax = Math.Max(1, Math.Max(f.TotalWorksetsCount ?? 0, f.TotalFamiliesCount ?? 0));
            int dcMax = Math.Max(1, Math.Max(m.WallsNotConnectedCount ?? 0, Math.Max(m.PipesNotConnectedCount ?? 0, m.DuctsNotConnectedCount ?? 0)));

            var sb = new StringBuilder();

            // ==================== HTML ====================
            sb.Append($@"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'/>
<link rel='icon' type='image/png' href='data:image/png;base64,{BIManageRevit.BIManage.Views.Metrics.ModelHealthDashboard.LOGO_BASE64}'/>
<title>ZeManage - Detailed Report</title>
<style>
*{{box-sizing:border-box;margin:0;padding:0;}}
body{{font-family:'Segoe UI',system-ui,sans-serif;background:#f0f4f8;color:#1e293b;font-size:14px;}}
.pg{{max-width:1200px;margin:0 auto;padding:16px;}}

/* Header */
.hdr{{background:linear-gradient(135deg,#E6EAF0,#E6EAF0);border-radius:14px;padding:20px 28px;margin-bottom:14px;color:#000000;display:flex;justify-content:space-between;align-items:center;}}
.hdr h1{{font-size:18px;font-weight:800;}}.hdr .sub{{font-size:11px;color:#000000;margin-top:2px;}}
.hdr-r{{display:flex;gap:24px;align-items:center;}}.hdr-meta{{font-size:10px;color:#000000;text-align:right;line-height:1.6;white-space:nowrap;}}
.score{{flex-shrink:0;width:72px;height:72px;border-radius:50%;display:flex;flex-direction:column;align-items:center;justify-content:center;border:3px solid;padding:4px;box-sizing:border-box;text-align:center;}}
.score-n{{font-size:20px;font-weight:900;line-height:1;margin-bottom:3px;}}.score-l{{font-size:8px;font-weight:700;text-transform:uppercase;letter-spacing:0.4px;line-height:1.15;}}

/* Grid */
.row{{display:grid;gap:14px;margin-bottom:14px;}}
.r1{{grid-template-columns:240px 1fr 280px 240px;}}
.r2{{grid-template-columns:240px 1fr 200px 1fr 200px 200px;}}

/* Cards */
.c{{background:white;border-radius:12px;padding:16px;border:1px solid #eef1f5;box-shadow:0 1px 3px rgba(0,0,0,.04);}}
.c-title{{font-size:12px;font-weight:800;color:#475569;text-transform:uppercase;letter-spacing:0.5px;margin-bottom:14px;}}

/* Stat items */
.si{{display:flex;align-items:center;gap:12px;padding:10px;border-radius:10px;margin-bottom:6px;}}
.si-icon{{width:38px;height:38px;border-radius:10px;display:flex;align-items:center;justify-content:center;font-size:14px;color:white;flex-shrink:0;}}
.si-label{{font-size:10px;font-weight:700;color:#64748b;text-transform:uppercase;letter-spacing:0.3px;}}.si-val{{font-size:20px;font-weight:800;color:#1e293b;line-height:1;}}
.si-val span{{font-size:12px;font-weight:600;color:#64748b;}}

/* Bar charts */
.bar-row{{display:flex;align-items:center;gap:8px;margin-bottom:8px;}}
.bar-label{{font-size:10px;font-weight:700;color:#64748b;width:70px;text-transform:uppercase;flex-shrink:0;}}
.bar-track{{flex:1;height:16px;background:#e9eef4;border-radius:4px;overflow:hidden;}}
.bar-fill{{height:100%;border-radius:4px;min-width:4px;transition:width .6s;}}
.bar-val{{font-size:13px;font-weight:800;color:#1e293b;min-width:28px;text-align:right;}}

/* Tiles */
.tile-grid{{display:grid;grid-template-columns:1fr 1fr;gap:8px;}}
.tile{{border-radius:12px;padding:12px;text-align:center;border:1px solid;}}
.tile-ok{{background:#f0fdf4;border-color:rgba(22,163,74,.15);}}.tile-bad{{background:#fef2f2;border-color:rgba(220,38,38,.15);}}
.tile-icon{{font-size:16px;margin-bottom:4px;}}.tile-val{{font-size:22px;font-weight:800;line-height:1;}}.tile-label{{font-size:9px;font-weight:700;color:#1e293b;margin-top:4px;}}.tile-sub{{font-size:10px;font-weight:600;color:#64748b;margin-top:2px;}}
.tile-ok .tile-val{{color:#16a34a;}}.tile-ok .tile-icon{{color:#16a34a;}}.tile-bad .tile-val{{color:#dc2626;}}.tile-bad .tile-icon{{color:#dc2626;}}

/* Info rows */
.info-row{{padding:6px 0;border-bottom:1px solid #f1f5f9;display:flex;justify-content:space-between;}}.info-row:last-child{{border-bottom:none;}}
.info-label{{font-size:10px;font-weight:700;color:#94a3b8;text-transform:uppercase;}}.info-val{{font-size:12px;font-weight:600;color:#1e293b;text-align:right;max-width:60%;word-break:break-all;}}

/* Donut */
.donut-wrap{{display:flex;align-items:center;gap:16px;}}
.donut{{width:100px;height:100px;border-radius:50%;flex-shrink:0;}}
.donut-cards{{display:flex;flex-direction:column;gap:6px;flex:1;}}
.dc{{padding:6px 10px;border-radius:8px;border-left:3px solid;}}
.dc-top{{display:flex;justify-content:space-between;font-size:9px;font-weight:700;text-transform:uppercase;}}.dc-val{{font-size:14px;font-weight:800;line-height:1;margin-top:2px;}}.dc-bar{{height:3px;background:rgba(0,0,0,.04);border-radius:2px;margin-top:3px;overflow:hidden;}}.dc-fill{{height:100%;border-radius:2px;}}

/* Meter */
.meter{{margin-bottom:12px;}}.meter-head{{display:flex;align-items:center;gap:8px;margin-bottom:4px;}}.meter-ico{{width:24px;height:24px;border-radius:6px;display:flex;align-items:center;justify-content:center;font-size:10px;color:white;}}
.meter-name{{font-size:12px;font-weight:700;color:#334155;flex:1;}}.meter-cnt{{font-size:16px;font-weight:800;}}
.meter-track{{height:6px;background:#f1f5f9;border-radius:10px;overflow:hidden;}}.meter-fill{{height:100%;border-radius:10px;}}

/* BP grid */
.bp-grid{{display:grid;grid-template-columns:1fr 1fr 1fr;gap:6px;}}.bp-card{{background:#f8fafc;border:1px solid #eef1f5;border-radius:8px;padding:10px;text-align:center;}}
.bp-val{{font-size:20px;font-weight:800;color:#1e293b;line-height:1;}}.bp-label{{font-size:9px;font-weight:700;color:#94a3b8;text-transform:uppercase;margin-top:4px;}}

/* Tables — render full height so long category lists never appear cut off mid-row.
   The 300px cap previously here clipped tables at an arbitrary boundary, which made
   rows like ""Wall Sweep - Trim"" look split across cards in the browser view. */
.tbl-wrap{{overflow:visible;border:1px solid #e2e8f0;border-radius:8px;margin-top:10px;page-break-inside:auto;break-inside:auto;}}
table{{width:100%;border-collapse:collapse;font-size:13px;}}
th{{background:#f8fafc;padding:8px 12px;text-align:left;font-weight:700;color:#475569;position:sticky;top:0;border-bottom:2px solid #e2e8f0;}}
td{{padding:6px 12px;border-bottom:1px solid #f1f5f9;}}
tr:hover td{{background:#f8fafc;}}
thead{{display:table-header-group;}}
tbody{{display:table-row-group;}}

/* Detail section */
.det{{background:white;border-radius:12px;padding:16px 20px;margin-bottom:14px;border:1px solid #eef1f5;box-shadow:0 1px 3px rgba(0,0,0,.04);}}
.det-title{{font-size:15px;font-weight:800;color:#475569;text-transform:uppercase;letter-spacing:0.5px;margin-bottom:12px;display:flex;justify-content:space-between;align-items:center;}}
.det-count{{font-size:12px;font-weight:600;color:#94a3b8;}}
.err-box{{background:#fef2f2;border:1px solid #fecaca;border-radius:6px;padding:10px 14px;margin-bottom:6px;font-size:13px;color:#991b1b;}}

.ftr{{text-align:center;padding:16px;font-size:10px;color:#94a3b8;}}

/* Download PDF button */
.dl-pdf-btn{{position:fixed;top:20px;right:20px;z-index:9999;background:#FF5500;color:white;border:none;padding:10px 18px;border-radius:8px;font-size:13px;font-weight:700;cursor:pointer;box-shadow:0 4px 14px rgba(255,85,0,0.3);display:flex;align-items:center;gap:8px;font-family:'Segoe UI',sans-serif;transition:all .2s ease;}}
.dl-pdf-btn:hover{{background:#E5611A;transform:translateY(-1px);box-shadow:0 6px 20px rgba(255,85,0,0.4);}}
.dl-pdf-btn:disabled{{opacity:0.6;cursor:wait;}}

/* Avoid splitting small atomic blocks across pages */
.det-row, tr, .info-row, .bar-row, .meter, .bp-card, .si, .tile{{page-break-inside:avoid;break-inside:avoid;}}
.det-title, .c-title{{page-break-after:avoid;break-after:avoid;}}

/* Print rules — used when the user clicks Download PDF and picks ""Save as PDF""
   from the print dialog.
   @page margin is set to 0 so the browser has NO margin area to inject its
   default URL footer (""file:///…html"") or date/title header (""5/4/26, 4:01 PM
   ZeManage - Detailed Report"") into. With zero @page margin, those auto-injected
   elements have no visible space and are suppressed by Chromium / Edge / Chrome
   print engines. We then push visible content margin onto .pg so the report still
   has comfortable whitespace inside each page — but the white border now belongs
   to the document, not to the browser's header/footer band. */
@media print{{
  @page {{ size: A4 portrait; margin: 0; }}
  html, body{{background:white !important;-webkit-print-color-adjust:exact;print-color-adjust:exact;margin:0 !important;padding:0 !important;}}
  /* Internal margin for visual whitespace, since @page margin is now 0 */
  .pg{{padding:12mm !important;background:white;}}
  .c,.det{{box-shadow:none !important;border:1px solid #e2e8f0 !important;}}
  .tbl-wrap{{max-height:none !important;overflow:visible !important;border-radius:0 !important;}}
  .dl-pdf-btn{{display:none !important;}}
  /* HTML-only blocks: shown when the user reviews the report in-browser, omitted
     from the printable PDF. Currently used for the long Warning Breakdown table —
     useful for drilling in on screen, but would blow up PDF length on models
     with thousands of warnings. */
  .screen-only{{display:none !important;}}
  th{{position:static !important;}}
  thead{{display:table-header-group;}}
  tbody{{display:table-row-group;}}
  .hdr{{-webkit-print-color-adjust:exact !important;print-color-adjust:exact !important;}}
}}
</style>
</head>
<body>
<div class='pg'>

<!-- HEADER -->
<div class='hdr'>
  <div style='display:flex;align-items:center;gap:14px;'>
    <img src='data:image/png;base64,{BIManageRevit.BIManage.Views.Metrics.ModelHealthDashboard.LOGO_BASE64}' alt='ZeManage' style='width:40px;height:40px;'/>
    <div><h1>ZeManage - Model Health Dashboard Report</h1><div class='sub'>Detailed Analysis Report</div></div>
  </div>
  <div class='hdr-r'>
    <div class='hdr-meta'>
      {H(modelName)}<br/>
      {now.ToString("dd MMM yyyy")} - {now.ToString("h:mm tt")} | By {H(capturedBy)}
    </div>
    <div class='score' style='border-color:{pctClr};color:{pctClr}'>
      <div class='score-n'>{pct}%</div>
      <div class='score-l'>{pctLbl}</div>
    </div>
  </div>
</div>

<!-- ==================== DETAILED CHECKS (Dashboard Order) ==================== -->
");
            // === 1. GENERAL STATISTICS ===
            sb.Append(DetSec("1. General Statistics", "File size and structural model overview.", 4));
            sb.Append(DetRow("&#128196;", "File Size", "Total file size of the Revit model on disk. Large file sizes slow down opening; saving; and syncing.", $"Result: {FS(f.FileSizeBytes)}", $"Goal: {fsGoalMB} MB", fsPctV < 1.0));
            sb.Append(DetRow(lvlApi > 0 ? (lvlP ? "&#9989;" : "&#10060;") : "&#127383;", "Levels", "Levels define floor-to-floor heights and organize the building vertically.", $"Count: {V(f.LevelsCount)}", lvlApi > 0 ? $"Goal: &lt; {lvlApi:N0}" : null, lvlP));
            sb.Append(Tbl1("Level Name", d.Levels));
            sb.Append(DetRow(grdApi > 0 ? (grdP ? "&#9989;" : "&#10060;") : "&#9638;", "Grids", "Grids define the structural layout and column locations.", $"Count: {V(f.GridsCount)}", grdApi > 0 ? $"Goal: &lt; {grdApi:N0}" : null, grdP));
            sb.Append(Tbl1("Grid Name", d.Grids));
            sb.Append(DetRow(lrevApi > 0 ? (lrevP ? "&#9989;" : "&#10060;") : "&#128279;", "Linked Revit Models", "External Revit models linked for coordination. Each adds to memory usage.", $"Count: {V(f.LinkedRevitCount)}", lrevApi > 0 ? $"Goal: &lt; {lrevApi:N0}" : null, lrevP));
            sb.Append(Tbl1("Linked Model", d.LinkedRevitModels));
            sb.Append(DetSecEnd());

            // === 2. ELEMENT BREAKDOWN ===
            sb.Append(DetSec("2. Element Breakdown", "Distribution of elements by category.", 4));
            sb.Append(DetRow("&#128202;", "Total Elements", "Sum of all elements in the model.", $"Count: {V(m.TotalElementsCount)}", null, true));
            sb.Append(DetRow("&#127975;", "Model Elements", "Physical geometry: walls; floors; roofs; columns; beams; doors; windows; MEP.", $"Count: {V(m.ModelElementsCount)} ({modPct:F1}%)", null, true));
            sb.Append(DetRow("&#128221;", "Annotative Elements", "Tags; dimensions; text notes; keynotes; symbols.", $"Count: {V(m.AnnotativeElementsCount)} ({annPct:F1}%)", null, true));
            sb.Append(DetRow("&#128300;", "Other Elements", "Datum; import instances; links; scope boxes; reference planes.", $"Count: {othEl:N0} ({othPct:F1}%)", null, true));
            sb.Append(DetSecEnd());

            // === 3. WORKSETS & FAMILIES ===
            sb.Append(DetSec("3. Worksets &amp; Families", "Worksharing configuration and family usage.", 4));
            sb.Append(DetRow(wkApi > 0 ? (wkP ? "&#9989;" : "&#10060;") : "&#128193;", "Worksets", "User worksets control visibility and organize for multi-user collaboration.", $"Count: {V(f.TotalWorksetsCount)}", wkApi > 0 ? $"Goal: &lt; {wkApi:N0}" : null, wkP));
            sb.Append(Tbl1("Workset Name", d.Worksets));
            sb.Append(DetRow(fmApi > 0 ? (fmP ? "&#9989;" : "&#10060;") : "&#127968;", "Total Families", "Loadable families define the parametric building components.", $"Count: {V(f.TotalFamiliesCount)}", fmApi > 0 ? $"Goal: &lt; {fmApi:N0}" : null, fmP));
            if (d.FamilySizes.Count > 0)
            {
                sb.Append(TblHdr($"Families by Size ({d.FamilySizes.Count} | Total: {d.TotalFamilySizeKB:N0} KB)"));
                foreach (var err in d.FamilySizeErrors)
                    sb.Append($"<div class='err-box'><strong>Error:</strong> {H(err)}</div>");
                sb.Append("<div class='tbl-wrap'><table><thead><tr><th>Family Name</th><th style='text-align:right;width:90px'>Size</th></tr></thead><tbody>");
                foreach (var (name, sizeKB) in d.FamilySizes)
                {
                    var clr = sizeKB >= 5120 ? "color:#dc2626;font-weight:700" : sizeKB >= 1024 ? "color:#d97706" : "";
                    sb.Append($"<tr><td>{H(name)}</td><td style='text-align:right;{clr}'>{sizeKB:N0} KB</td></tr>");
                }
                sb.Append("</tbody></table></div>");
            }
            sb.Append(DetRow("&#128196;", "View Templates", "View templates ensure consistent view settings.", $"Count: {V(f.ViewTemplatesCount)}", null, true));
            sb.Append(Tbl1("Template Name", d.ViewTemplates));
            sb.Append(DetRow("&#128204;", "Design Options", "Design options allow multiple design alternatives.", $"Count: {V(f.DesignOptionsCount)}", null, true));
            sb.Append(Tbl1("Design Option", d.DesignOptions));
            sb.Append(DetSecEnd());

            // === 4. PERFORMANCE IMPACTS ===
            sb.Append(DetSec("4. Performance Impacts", "Metrics that directly affect model performance.", 4));
            sb.Append(DetRow(warnP ? "&#9989;" : "&#10060;", "Warnings", "Unresolved warnings cause performance issues and data inconsistencies.", $"Count: {V(f.WarningsCount)}", $"Goal: &lt; {warnGoal}", warnP));
            if (d.WarningsByType.Count > 0)
            {
                int totalW = d.WarningsByType.Sum(w => w.Count);
                // Wrap in .screen-only so the HTML review shows the full breakdown table
                // (users want to drill into each warning type), but the PDF skips it —
                // a 2000+ warning model would otherwise produce a multi-page printable
                // dominated by warning rows. The summary tile above stays in both.
                sb.Append("<div class='screen-only'>");
                sb.Append(TblHdr($"Warning Breakdown ({totalW} total)"));
                sb.Append("<div class='tbl-wrap'><table><thead><tr><th style='width:70px'>Count</th><th>Warning Type</th></tr></thead><tbody>");
                foreach (var (msg, cnt) in d.WarningsByType)
                    sb.Append($"<tr><td style='font-weight:700;color:#dc2626'>{cnt}</td><td>{H(msg)}</td></tr>");
                sb.Append("</tbody></table></div>");
                sb.Append("</div>");
            }
            sb.Append(DetRow(dupP ? "&#9989;" : "&#10060;", "Duplicate Elements", "Duplicates waste space and cause scheduling errors.", $"Count: {V(f.DuplicateElementsCount)}", $"Goal: &lt; {dupGoal}", dupP));
            sb.Append(DetRow(idwP ? "&#9989;" : "&#10060;", "Imported DWG Files", "Imported CAD files add non-native styles and bloat file size.", $"Count: {V(f.ImportedDwgCount)}", $"Goal: {(idwGoal <= 1 ? "0" : "&lt; " + idwGoal)}", idwP));
            sb.Append(Tbl1("Imported DWG", d.ImportedDwgFiles));
            sb.Append(DetRow(nnP ? "&#9989;" : "&#10060;", "Non-Native Object Styles", "Custom/imported styles from CAD data.", $"Count: {V(f.NonNativeObjectStylesCount)}", $"Goal: {(nnGoal <= 1 ? "0" : "&lt; " + nnGoal)}", nnP));
            sb.Append(Tbl1("Style Name", d.NonNativeObjectStyles));
            sb.Append(DetSecEnd());

            // === 5. VIEWS & SHEETS / VIEWS NOT ON SHEETS ===
            sb.Append(DetSec("5. Views &amp; Sheets / Views Not on Sheets", "View management and sheet placement.", 3));
            sb.Append(DetRow(tvApi > 0 ? (tvP ? "&#9989;" : "&#10060;") : "&#128065;", "Total Views", "All non-template views: floor plans; ceiling plans; sections; elevations; 3D; drafting.", $"Count: {V(f.TotalViewsCount)}", tvApi > 0 ? $"Goal: &lt; {tvApi:N0}" : null, tvP));
            sb.Append(Tbl2("View Name", "Type", d.Views.Select(v => (v.Name, v.Type)).ToList()));
            sb.Append(DetRow("&#128196;", "Sheets", "Sheets used for printing and documentation.", $"Count: {V(f.SheetsCount)}", null, true));
            sb.Append(Tbl1("Sheet", d.Sheets));
            sb.Append(DetRow(vnsApi > 0 ? (vnsP ? "&#9989;" : "&#10060;") : "&#9888;", "Views Not on Sheets", "Views not placed on any sheet. May be unnecessary.", $"Count: {V(m.ViewsNotOnSheetsCount)}", vnsApi > 0 ? $"Goal: &lt; {vnsApi:N0}" : null, vnsP));
            sb.Append(Tbl1("View", d.ViewsNotOnSheets));
            sb.Append(DetSecEnd());

            // === 6. CLEANUP OPPORTUNITIES ===

            sb.Append(DetSec("6. Cleanup Opportunities", "Elements that can be cleaned up to reduce file size and improve performance. Regular cleanup maintains model health.", 2));
            sb.Append(DetRow(ovrP ? "&#9989;" : "&#10060;", "Oversized Families (&gt;5 MB)", "COUNT and LIST of loadable families that exceed 5 MB in file size. Oversized families significantly increase model file size; slow down loading; and consume excessive memory. Consider simplifying geometry or reducing detail level.", $"Count: {V(e.FamiliesOver5MbCount)}", ovrGoal <= 1 ? "Goal: 0" : $"Goal: &lt; {ovrGoal:N0}", ovrP));
            // Show oversized families table (only families >= 5MB)
            {
                var oversized = d.FamilySizes.Where(x => x.SizeKB >= 5120).ToList();
                if (oversized.Count > 0)
                {
                    sb.Append($"<div style='font-size:12px;font-weight:700;color:#dc2626;margin:8px 0 6px;'>Oversized Families ({oversized.Count} families &gt; 5 MB)</div>");
                    sb.Append("<div class='tbl-wrap'><table><thead><tr><th>Family Name</th><th style='text-align:right;width:80px'>Size</th></tr></thead><tbody>");
                    foreach (var (name, sizeKB) in oversized)
                        sb.Append($"<tr><td>{H(name)}</td><td style='text-align:right;color:#dc2626;font-weight:700'>{sizeKB:N0} KB</td></tr>");
                    sb.Append("</tbody></table></div>");
                }
            }
            // Purgeable Elements: count only, no goal/PASS-FAIL badge to match the Model Health
            // Dashboard (which displays the count without a target). Passing goal=null suppresses
            // both the "Goal: ..." sub-line and the PASS/FAIL pill in DetRow.
            sb.Append(DetRow(prgP ? "&#9989;" : "&#10060;", "Purgeable Elements", "COUNT of unused element types that can be purged from the model. Unused family symbols; wall types; floor types; line patterns; and fill patterns accumulate over time and bloat the file with no benefit.", $"Count: {V(e.PurgeableElementsCount)}", (healthGoals?.MaxPurgeableElementsCount ?? 0) > 0 ? $"Goal: &lt; {prgGoal:N0}" : null, prgP));
            sb.Append(DetSecEnd());

            // === 7. BEST PRACTICE ===
            sb.Append(DetSec("7. Best Practice", "Modeling standards and best practice compliance.", 6));
            sb.Append(DetRow(grpP ? "&#9989;" : "&#10060;", "Model Groups", "Model groups duplicate geometry. Excessive use indicates copy-paste workflows.", $"Count: {V(f.ModelGroupsCount)}", "Goal: &lt; 50 combined", grpP));
            sb.Append(Tbl1("Model Group (instances)", d.ModelGroups));
            sb.Append(DetRow("&#128202;", "Detail Groups", "2D annotation groups used in drafting views.", $"Count: {V(f.DetailGroupsCount)}", null, true));
            sb.Append(Tbl1("Detail Group (instances)", d.DetailGroups));
            sb.Append(DetRow(lDwgApi > 0 ? (lDwgP ? "&#9989;" : "&#10060;") : "&#128279;", "Linked DWG Files", "External DWG files linked. Preferred over imports.", $"Count: {V(f.LinkedDwgCount)}", lDwgApi > 0 ? $"Goal: &lt; {lDwgApi:N0}" : null, lDwgP));
            sb.Append(Tbl1("Linked DWG", d.LinkedDwgFiles));
            sb.Append(DetRow(rastApi > 0 ? (rastP ? "&#9989;" : "&#10060;") : "&#128247;", "Raster Images", "Raster images increase file size.", $"Count: {V(f.RasterImagesCount)}", rastApi > 0 ? $"Goal: &lt; {rastApi:N0}" : null, rastP));
            sb.Append(Tbl1("Image", d.RasterImages));
            sb.Append(DetRow("&#128204;", "Design Options", "Each option creates separate elements.", $"Count: {V(f.DesignOptionsCount)}", null, true));
            sb.Append(DetRow("&#128196;", "View Templates", "Templates ensure consistent settings.", $"Count: {V(f.ViewTemplatesCount)}", null, true));
            sb.Append(DetSecEnd());

            // === 8. MODEL QUALITY SCAN ===
            sb.Append(DetSec("8. Model Quality Scan", "Room integrity and spatial checks.", 3));
            sb.Append(DetRow(upP ? "&#9989;" : "&#10060;", "Unplaced Rooms", "Rooms not placed in any view. Appear in schedules but have no area.", $"Count: {V(m.UnplacedRoomsCount)}", upGoal <= 1 ? "Goal: 0" : $"Goal: &lt; {upGoal:N0}", upP));
            sb.Append(Tbl1("Room Name", d.UnplacedRooms));
            sb.Append(DetRow(ueP ? "&#9989;" : "&#10060;", "Unenclosed Rooms", "Rooms not fully bounded. Cannot compute area correctly.", $"Count: {V(m.UnenclosedRoomsCount)}", ueGoal <= 1 ? "Goal: 0" : $"Goal: &lt; {ueGoal:N0}", ueP));
            sb.Append(Tbl1("Room Name", d.UnenclosedRooms));
            sb.Append(DetRow(inpP ? "&#9989;" : "&#10060;", "In-Place Families", "In-place families are unique; cannot be reused; impact performance.", $"Count: {V(m.InplaceFamiliesCount)}", inpGoal <= 1 ? "Goal: 0" : $"Goal: &lt; {inpGoal:N0}", inpP));
            sb.Append(Tbl1("In-Place Family", d.InPlaceFamilyNames));
            sb.Append(DetSecEnd());

            // === 9. DISCONNECTS ===
            sb.Append(DetSec("9. Disconnects", "MEP and structural connectivity. Disconnected elements cause analysis errors.", 3));
            sb.Append(DetRow(wlP ? "&#9989;" : "&#10060;", "Walls Not Connected", "Disconnected walls create gaps in room boundaries and affect area calculations.", $"Count: {V(m.WallsNotConnectedCount)}", wlGoal <= 1 ? "Goal: 0" : $"Goal: &lt; {wlGoal:N0}", wlP));
            sb.Append(DetRow(ppP ? "&#9989;" : "&#10060;", "Pipes Not Connected", "Open pipe ends break piping system continuity.", $"Count: {V(m.PipesNotConnectedCount)}", ppGoal <= 1 ? "Goal: 0" : $"Goal: &lt; {ppGoal:N0}", ppP));
            sb.Append(DetRow(dcP ? "&#9989;" : "&#10060;", "Ducts Not Connected", "Open duct ends break ductwork system continuity.", $"Count: {V(m.DuctsNotConnectedCount)}", dcGoal <= 1 ? "Goal: 0" : $"Goal: &lt; {dcGoal:N0}", dcP));
            sb.Append(DetSecEnd());

            // === 10. SURVEY POINT ===
            if (f.SharedCoordNs.HasValue || f.SharedCoordEw.HasValue || f.SharedCoordElevation.HasValue)
            {
                sb.Append(DetSec("10. Survey Point - Shared Coordinates", "Survey point coordinate values for geo-referencing. These values should match the project's survey control points.", 3));
                var coordUnit = !string.IsNullOrEmpty(f.SharedCoordUnit) ? f.SharedCoordUnit! : "ft";
                sb.Append(CoordCheck("Survey Point - N/S", "The North/South coordinate value for the Survey Point. This should match the designated survey control point location.", f.SharedCoordNs, "N/S", coordUnit));
                sb.Append(CoordCheck("Survey Point - E/W", "The East/West coordinate value for the Survey Point. This should match the designated survey control point location.", f.SharedCoordEw, "E/W", coordUnit));
                sb.Append(CoordCheck("Survey Point - Elev", "The Elevation coordinate value for the Survey Point. This should match the designated survey datum.", f.SharedCoordElevation, "Elev", coordUnit));
                sb.Append(DetSecEnd());
            }

            // FOOTER
            sb.Append($"<div class='ftr'>Generated by ZeManage | {now.ToString("dd MMMM yyyy")} | ZestineTech</div></div>");

            // Download PDF button. If pdfBase64 was supplied (the normal
            // GenerateAndOpen flow), the button decodes that base64 into a
            // Blob in JS memory and triggers a download via a programmatic
            // <a download> click on a Blob URL — exactly the same pattern
            // html2pdf.js's .save() method uses internally. The browser shows
            // the file in its download bar; NO new window or tab opens.
            //
            // If pdfBase64 is null (PDF generation failed, or someone called
            // Generate() directly without the two-pass flow), the button is
            // disabled and displays a tooltip explaining the situation.
            var safeName = string.Join("_", (modelName ?? "Model").Split(Path.GetInvalidFileNameChars()));
            var pdfFile = pdfFileName ?? $"ZeManage_DetailedReport_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";

            if (!string.IsNullOrEmpty(pdfBase64))
            {
                // JS string-escape for the filename: backslashes and single
                // quotes are the only chars that can appear in a Path-safe
                // name (we ran the model name through Path.GetInvalidFileNameChars
                // already). base64 contains only [A-Za-z0-9+/=] — none need
                // escaping inside a JS single-quoted string.
                var pdfFileJs = (pdfFile ?? "report.pdf").Replace("\\", "\\\\").Replace("'", "\\'");

                sb.Append($@"
<button id='dlPdfBtn' class='dl-pdf-btn' onclick='downloadPdf()' title='Download the PDF report.'>⬇ Download PDF</button>
<script>
(function(){{
  // Embedded base64 PDF — the bytes Edge headless wrote to a temp .pdf
  // file, read back and inlined here so the download can happen entirely
  // in memory (no second file:// fetch which Chrome would block).
  var PDF_BASE64 = '{pdfBase64}';
  var PDF_FILENAME = '{pdfFileJs}';

  window.downloadPdf = function() {{
    try {{
      var bin = atob(PDF_BASE64);
      var len = bin.length;
      var arr = new Uint8Array(len);
      for (var i = 0; i < len; i++) arr[i] = bin.charCodeAt(i);
      var blob = new Blob([arr], {{type: 'application/pdf'}});
      var url = URL.createObjectURL(blob);
      var a = document.createElement('a');
      a.href = url;
      a.download = PDF_FILENAME;
      a.style.display = 'none';
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      // Revoke after a tick so the click has time to grab the blob.
      setTimeout(function() {{ URL.revokeObjectURL(url); }}, 1000);
    }} catch (err) {{
      alert('Could not download the PDF: ' + (err && err.message || err));
    }}
  }};
}})();
</script>
</body></html>");
            }
            else
            {
                // Edge AND Chrome were both missing on the user's machine, so
                // the C# side couldn't pre-render a server-side vector PDF.
                // Fall back to html2pdf.js — pure-JavaScript client-side PDF
                // generation that runs entirely inside the user's browser (any
                // modern browser will do, including Firefox/Brave).
                //
                // Trade-offs vs. the primary (Chromium-headless) path:
                //   - PDF is a BITMAP (text isn't selectable, may slice past page 13).
                //   - But still one-click — no print dialog, no new tab.
                //
                // The library is loaded from a multi-CDN script with fallback:
                // cdnjs → jsDelivr → unpkg. Requires the user to have internet
                // on first run, which the rare "no Edge AND no Chrome" user
                // almost certainly does.
                var safeName2 = string.Join("_", (modelName ?? "Model").Split(Path.GetInvalidFileNameChars()));
                var pdfFile2 = pdfFileName ?? $"ZeManage_DetailedReport_{safeName2}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
                var pdfFileJs2 = pdfFile2.Replace("\\", "\\\\").Replace("'", "\\'");
                sb.Append($@"
<button id='dlPdfBtn' class='dl-pdf-btn' onclick='downloadReportPdf()' title='Generate the PDF report (rendered in-browser since Edge/Chrome are not installed).'>⬇ Download PDF</button>
<script>
// Multi-CDN loader for html2pdf.js — try cdnjs first, then jsDelivr, then
// unpkg. If all three are blocked (corporate firewall), window.__html2pdfLoadFailed
// is set and the button alerts the user.
(function loadHtml2Pdf(){{
  var sources=[
    'https://cdnjs.cloudflare.com/ajax/libs/html2pdf.js/0.10.1/html2pdf.bundle.min.js',
    'https://cdn.jsdelivr.net/npm/html2pdf.js@0.10.1/dist/html2pdf.bundle.min.js',
    'https://unpkg.com/html2pdf.js@0.10.1/dist/html2pdf.bundle.min.js'
  ];
  var i=0;
  function tryNext(){{
    if(i>=sources.length){{ window.__html2pdfLoadFailed=true; return; }}
    var s=document.createElement('script');
    s.src=sources[i++];
    s.async=false;
    s.onerror=function(){{ tryNext(); }};
    document.head.appendChild(s);
  }}
  tryNext();
}})();

function waitForHtml2Pdf(maxMs, cb){{
  var start=Date.now();
  (function poll(){{
    if(typeof html2pdf!=='undefined'){{ cb(true); return; }}
    if(window.__html2pdfLoadFailed||(Date.now()-start)>maxMs){{ cb(false); return; }}
    setTimeout(poll, 100);
  }})();
}}

function downloadReportPdf(){{
  var btn=document.getElementById('dlPdfBtn');
  if(btn){{ btn.disabled=true; btn.textContent='Generating PDF...'; }}
  function resetBtn(){{
    if(btn){{ btn.disabled=false; btn.textContent='⬇ Download PDF'; }}
  }}
  // Reset scroll BEFORE html2canvas snapshots — otherwise a user who scrolled
  // the report before clicking gets a blank first page.
  window.scrollTo(0,0);
  requestAnimationFrame(function(){{ requestAnimationFrame(function(){{
    waitForHtml2Pdf(6000,function(loaded){{
      if(!loaded){{
        alert('PDF library could not load. Check your internet connection (corporate firewall may be blocking cdnjs/jsdelivr/unpkg).');
        resetBtn();
        return;
      }}
      var target=document.querySelector('.pg');
      var opt={{
        margin:[10,10,12,10],
        filename:'{pdfFileJs2}',
        image:{{type:'jpeg',quality:0.95}},
        // scale:1 keeps the html2canvas bitmap under Chrome's max canvas height
        // (~32767px). For our report size, scale:2 would silently produce a
        // blank canvas (48 blank pages symptom).
        html2canvas:{{scale:1,useCORS:true,letterRendering:true,backgroundColor:'#ffffff',logging:false}},
        jsPDF:{{unit:'mm',format:'a4',orientation:'portrait',compress:true,putOnlyUsedFonts:true}},
        pagebreak:{{mode:['avoid-all','css'],avoid:['tr','td','.det-row','.info-row','.bar-row','.meter','.bp-card','.si','.tile']}}
      }};
      try{{
        html2pdf().from(target).set(opt).save()
          .then(function(){{ resetBtn(); }})
          .catch(function(e){{ alert('Failed to generate PDF: '+(e&&e.message||e)); resetBtn(); }});
      }}catch(e){{
        alert('Failed to generate PDF: '+(e&&e.message||e));
        resetBtn();
      }}
    }});
  }}); }});
}}
</script>
</body></html>");
            }

            return sb.ToString();
        }

        // ==================== Component Helpers ====================

        static string Bar(string label, int val, int max, string color)
        {
            int w = max > 0 ? Math.Max(3, (int)(100.0 * val / max)) : 3;
            return $"<div class='bar-row'><span class='bar-label'>{label}</span><div class='bar-track'><div class='bar-fill' style='width:{w}%;background:linear-gradient(90deg,{color},{color}dd)'></div></div><span class='bar-val'>{val:N0}</span></div>";
        }

        static string VBar(int val, int max, string label, string color)
        {
            int h = max > 0 ? Math.Max(8, (int)(100.0 * val / max)) : 8;
            return $"<div style='display:flex;flex-direction:column;align-items:center;flex:1;justify-content:flex-end;'><div style='font-size:14px;font-weight:800;color:#1e293b;margin-bottom:4px'>{val:N0}</div><div style='width:100%;max-width:50px;height:{h}%;border-radius:8px 8px 0 0;background:linear-gradient(180deg,{color}dd,{color});min-height:8px;'></div><div style='font-size:9px;font-weight:700;color:#64748b;margin-top:6px;text-transform:uppercase;text-align:center;'>{label}</div></div>";
        }

        static string Tile(string icon, string val, string label, string sub, bool ok)
        {
            string cls = ok ? "tile-ok" : "tile-bad";
            var subHtml = !string.IsNullOrEmpty(sub) ? $"<div class='tile-sub'>{sub}</div>" : "";
            return $"<div class='tile {cls}'><div class='tile-icon'>{icon}</div><div class='tile-val'>{val}</div><div class='tile-label'>{label}</div>{subHtml}</div>";
        }

        static string Meter(string label, int val, int max, bool ok)
        {
            int w = max > 0 && val > 0 ? Math.Max(6, (int)(100.0 * val / max)) : (val > 0 ? 100 : 0);
            string bg = ok ? "linear-gradient(135deg,#6a9568,#7db37a)" : "linear-gradient(135deg,#c47c7c,#d49a9a)";
            string fill = ok ? "background:linear-gradient(90deg,#7db37a,#6a9568)" : "background:linear-gradient(90deg,#d49a9a,#c47c7c)";
            string clr = ok ? "#6a9568" : "#c47c7c";
            return $"<div class='meter'><div class='meter-head'><div class='meter-ico' style='background:{bg}'>&#9679;</div><span class='meter-name'>{label}</span><span class='meter-cnt' style='color:{clr}'>{val:N0}</span></div><div class='meter-track'><div class='meter-fill' style='width:{w}%;{fill}'></div></div></div>";
        }

        static string BpCard(string val, string label) =>
            $"<div class='bp-card'><div class='bp-val'>{val}</div><div class='bp-label'>{label}</div></div>";

        // Detail section helpers
        static string DetSec(string title, string desc, int checkCount) =>
            $"<div class='det'><div class='det-title'><span>{title}</span><span class='det-count'>{checkCount} Checks</span></div><div style='font-size:14px;color:#64748b;margin-bottom:14px;line-height:1.6;'>{desc}</div>\n";
        static string DetSecEnd() => "</div>\n";
        static string DetRow(string icon, string title, string desc, string result, string? goal, bool ok)
        {
            // Per-row Goal sub-line and PASS/FAIL pill suppressed: server-side threshold sync
            // (via /health-monitor-protections/by-model + StaticModelInfo) does not propagate
            // admin-configured goals reliably to Revit, so rows displayed "Goal: 0 FAIL" against
            // stale defaults instead of the values the admin set on the web. Upstream Goal()/PF()/
            // PFOpt() helpers and the overall health-score ring are left untouched; only the
            // per-row Goal/PASS-FAIL UI is removed.
            _ = goal; _ = ok;
            var goalHtml = "";
            var statusHtml = "";
            return $@"<div class='det-row' style='display:grid;grid-template-columns:auto 1fr auto;gap:14px;padding:14px 16px;border:1px solid #f1f5f9;border-radius:8px;margin-bottom:8px;align-items:start;'>
  <div style='font-size:20px;margin-top:2px;'>{icon}</div>
  <div><div style='font-size:15px;font-weight:700;color:#1e293b;'>{title}</div><div style='font-size:13px;color:#64748b;margin-top:4px;line-height:1.6;'>{desc}</div></div>
  <div style='text-align:right;min-width:120px;'><div style='font-size:16px;font-weight:800;color:#1e293b;'>{result}</div>{goalHtml}{statusHtml}</div>
</div>";
        }

        static string TblHdr(string title) => $"<div style='font-size:12px;font-weight:700;color:#475569;margin:8px 0 6px;'>{title}</div>";

        static string Tbl1(string header, System.Collections.Generic.List<string> rows)
        {
            if (rows == null || rows.Count == 0) return "";
            var sb2 = new StringBuilder($"<div class='tbl-wrap'><table><thead><tr><th>{header}</th></tr></thead><tbody>");
            foreach (var r in rows) sb2.Append($"<tr><td>{H(r)}</td></tr>");
            sb2.Append("</tbody></table></div>");
            return sb2.ToString();
        }

        static string Tbl2(string h1, string h2, System.Collections.Generic.List<(string, string)> rows)
        {
            if (rows == null || rows.Count == 0) return "";
            var sb2 = new StringBuilder($"<div class='tbl-wrap'><table><thead><tr><th>{h1}</th><th>{h2}</th></tr></thead><tbody>");
            foreach (var (c1, c2) in rows) sb2.Append($"<tr><td>{H(c1)}</td><td>{H(c2)}</td></tr>");
            sb2.Append("</tbody></table></div>");
            return sb2.ToString();
        }

        static string CoordCheck(string title, string desc, double? value, string axis, string unit)
        {
            var valStr = value.HasValue ? $"{value.Value:N4} {unit}" : "N/A";
            return $@"<div style='border:1px solid #e2e8f0;border-radius:8px;padding:14px;margin-bottom:8px;'>
  <div style='display:flex;align-items:start;gap:12px;'>
    <div style='font-size:16px;margin-top:2px;'>&#128204;</div>
    <div style='flex:1;'>
      <div style='font-size:13px;font-weight:700;color:#1e293b;'>{title}</div>
      <div style='font-size:11px;color:#64748b;margin-top:2px;line-height:1.4;'>{desc}</div>
      <div style='font-size:13px;font-weight:700;color:#1e293b;margin-top:8px;'>Value: {valStr}</div>
      <div class='tbl-wrap' style='margin-top:8px;max-height:none;'>
        <table>
          <thead><tr><th>Category</th><th>Name</th><th>Value</th></tr></thead>
          <tbody><tr><td>Survey Point</td><td>Survey Point - {axis}</td><td style='font-weight:700'>{valStr}</td></tr></tbody>
        </table>
      </div>
    </div>
  </div>
</div>";
        }
    }
}
