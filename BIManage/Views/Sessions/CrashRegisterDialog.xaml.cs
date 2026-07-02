using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BIManage.Common.Helpers;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Crash;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Crash;
using BIManage.Views.Common;


namespace BIManageRevit.BIManage.Views.Sessions
{
    public partial class CrashRegisterDialog : Window
    {
        private readonly SessionRepository _sessionRepository;
        private readonly ILogger? _logger;
        // Externalized to App.config (BIManageRevit.dll.config) so staging/prod URLs are
        // swappable without a rebuild. Falls back to the PRODUCTION analyzer URL if the key
        // is missing — staging (journalparser-staging-…azurewebsites.net) was returning HTTP
        // 503 on /upload/sas and any plugin that lost its .config would silently hit it.
        // TrimEnd('/') normalizes the base so $"{AnalyzerBaseUrl}/upload" doesn't double-slash.
        private static readonly string AnalyzerBaseUrl =
            (AppConfigReader.Read("AnalyzerBaseUrl")
             ?? "https://zediag.zestinetech.com/")
            .TrimEnd('/');
        // Process-wide HttpClient for the post-analysis steps (PDF generation + dashboard HTML
        // fetch). Reused across clicks to avoid TIME_WAIT socket exhaustion that per-call
        // HttpClient instances cause. 2-min timeout accommodates the larger PDF response.
        private static readonly HttpClient ReportHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        private List<CrashedSessionViewModel> _allSessions = new List<CrashedSessionViewModel>();
        private string _currentFilter = "Crashed";
        private CancellationTokenSource? _activeCts;

        public CrashRegisterDialog(SessionRepository sessionRepository, ILogger? logger)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
            _logger = logger;

            Loaded += CrashRegisterDialog_Loaded;
            // Cancel any in-flight upload/analysis cleanly when the user closes the dialog.
            Closing += (_, _) => _activeCts?.Cancel();
        }

        private async void CrashRegisterDialog_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadCrashedSessionsAsync();
        }

        private DateTime ToLocalTime(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Utc)
                return dateTime.ToLocalTime();
            else if (dateTime.Kind == DateTimeKind.Local)
                return dateTime;
            else
                return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc).ToLocalTime();
        }

        private string FormatLocalDateTime(DateTime dateTime)
        {
            return ToLocalTime(dateTime).ToString("yyyy-MM-dd HH:mm:ss");
        }

        private async Task LoadCrashedSessionsAsync()
        {
            try
            {
                // Load all recent sessions (Active, Crashed, Closed) so filters work correctly
                var crashedSessions = await _sessionRepository.GetRecentSessionsAsync(50);

                if (crashedSessions == null || !crashedSessions.Any())
                {
                    GridCrashedSessions.Visibility = System.Windows.Visibility.Collapsed;
                    EmptyCrashed.Visibility = System.Windows.Visibility.Visible;
                    return;
                }

                int rowNum = 1;
                _allSessions = crashedSessions.Select(s => new CrashedSessionViewModel
                {
                    RowNumber = rowNum++,
                    SessionId = s.SessionId ?? "-",
                    StartedAtLocal = FormatLocalDateTime(s.StartedAt),
                    EndedAtLocal = s.EndedAt.HasValue ? FormatLocalDateTime(s.EndedAt.Value) : "-",
                    Duration = CalculateDuration(s.StartedAt, s.EndedAt),
                    Status = ResolveSessionStatus(s),
                    JournalFileName = s.JournalFileName ?? "-",
                    JournalFileNameShort = !string.IsNullOrEmpty(s.JournalFileName)
                        ? Path.GetFileName(s.JournalFileName)
                        : "-",
                    RevitVersion = s.RevitVersion
                }).ToList();

                ApplyStatusFilter();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load crashed sessions: {ex.Message}", ex);
                GridCrashedSessions.Visibility = System.Windows.Visibility.Collapsed;
                EmptyCrashed.Visibility = System.Windows.Visibility.Visible;
            }
        }

        /// <summary>
        /// Resolves the correct status for a session. Sessions that ended without being
        /// marked "Closed" by the normal shutdown handler are treated as crashed — a clean
        /// exit always sets "Closed", so any other ended session terminated abnormally.
        /// </summary>
        private string ResolveSessionStatus(RevitSession s)
        {
            var status = s.Status;

            // "Active" or "Closed" are definitive — trust them
            if (!string.IsNullOrEmpty(status)
                && (status.Equals("Active", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("Closed", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("Crashed", StringComparison.OrdinalIgnoreCase)))
                return status;

            // Session has ended (has duration) but was NOT marked "Closed" by OnShutdown.
            // This means Revit did not exit normally — treat as Crashed.
            if (s.EndedAt.HasValue)
                return "Crashed";

            // Session still in progress (no EndedAt) with null/Unknown status
            // Re-analyze journal to see if it's actually crashed
            if (!string.IsNullOrEmpty(s.JournalFileName) && !string.IsNullOrEmpty(s.RevitVersion))
            {
                var evidence = RevitJournalCrashDetector.Analyze(s.JournalFileName, s.RevitVersion);
                if (evidence.IsCrashed)
                    return "Crashed";
            }

            return status ?? "Unknown";
        }

        private string CalculateDuration(DateTime startedAt, DateTime? endedAt)
        {
            if (!endedAt.HasValue)
                return "In Progress";

            var duration = endedAt.Value - startedAt;

            if (duration.TotalSeconds < 60)
                return $"{(int)duration.TotalSeconds}s";
            else if (duration.TotalMinutes < 60)
                return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
            else if (duration.TotalHours < 24)
                return $"{(int)duration.TotalHours}h {duration.Minutes}m";
            else
                return $"{(int)duration.TotalDays}d {duration.Hours}h";
        }

        private void ApplyStatusFilter()
        {
            IEnumerable<CrashedSessionViewModel> filtered;

            if (_currentFilter == "Crashed")
            {
                // Crashed: show last 10 crashed sessions from all data
                filtered = _allSessions.Where(s => s.Status.Equals("Crashed", StringComparison.OrdinalIgnoreCase)).Take(10);
            }
            else
            {
                // Active, Closed, All: take last 10 overall, then filter by status
                var latest10 = _allSessions.Take(10).ToList();

                if (_currentFilter == "Active")
                    filtered = latest10.Where(s => s.Status.Equals("Active", StringComparison.OrdinalIgnoreCase));
                else if (_currentFilter == "Closed")
                    filtered = latest10.Where(s => s.Status.Equals("Closed", StringComparison.OrdinalIgnoreCase));
                else
                    filtered = latest10; // "All" filter
            }

            var filteredList = filtered.ToList();

            // Renumber rows
            for (int i = 0; i < filteredList.Count; i++)
            {
                filteredList[i].RowNumber = i + 1;
            }

            GridCrashedSessions.ItemsSource = filteredList;

            if (filteredList.Any())
            {
                GridCrashedSessions.Visibility = System.Windows.Visibility.Visible;
                EmptyCrashed.Visibility = System.Windows.Visibility.Collapsed;
            }
            else
            {
                GridCrashedSessions.Visibility = System.Windows.Visibility.Collapsed;
                EmptyCrashed.Visibility = System.Windows.Visibility.Visible;
            }
        }

        private void FilterTab_Click(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Border clickedTab))
                return;

            var filterValue = clickedTab.Tag?.ToString() ?? "All";
            _currentFilter = filterValue;

            // Update tab visual states
            ResetTabStyles();
            SetActiveTab(clickedTab);

            // Apply filter
            ApplyStatusFilter();
        }

        private void ResetTabStyles()
        {
            // Reset all tabs to inactive state
            SetTabStyle(TabCrashed, false);
            SetTabStyle(TabActive, false);
            SetTabStyle(TabClosed, false);
            SetTabStyle(TabAll, false);
        }

        private void SetActiveTab(Border tab)
        {
            SetTabStyle(tab, true);
        }

        private void SetTabStyle(Border tab, bool isActive)
        {
            if (tab == null) return;

            var textBlock = tab.Child as TextBlock;
            if (textBlock == null) return;

            if (isActive)
            {
                tab.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 234, 240)); // #E6EAF0 background
                textBlock.Foreground = Brushes.Black;
                textBlock.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                tab.Background = Brushes.Transparent;
                textBlock.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)); // #64748B
                textBlock.FontWeight = FontWeights.Medium;
            }
        }

        private async void Analysis_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !(btn.Tag is CrashedSessionViewModel session))
                return;

            _logger?.LogInfo($"Analysis requested for crashed session: {session.SessionId}");
            _logger?.LogInfo($"Journal file: {session.JournalFileName}, Short: {session.JournalFileNameShort}, Revit: {session.RevitVersion}");

            // Step 1: Find the journal file
            var journalPath = FindJournalFile(session.JournalFileName, session.JournalFileNameShort, session.RevitVersion);

            if (string.IsNullOrEmpty(journalPath))
            {
                _logger?.LogWarning($"Journal file not found: {session.JournalFileNameShort}");
                ZeMessageBox.Show(
                    "Journal File Not Found",
                    $"Journal file not found: {session.JournalFileNameShort}\n\n" +
                    "Searched in:\n" +
                    GetSearchedPaths(session.RevitVersion),
                    ZeMessageType.Warning);
                return;
            }

            _logger?.LogInfo($"Found journal file: {journalPath} ({new FileInfo(journalPath).Length / 1024} KB)");

            // Auto-upload → website's React UI renders the analysis → user downloads from the
            // website's own Download button. Per user spec: "when I click Analyse, automatically
            // upload the file [to the] website. Only after [that], the website's React-based
            // working [kicks in]."
            //
            // UploadAndAnalyzeAsync (in this file) implements that end-to-end:
            //   1. SAS-upload the journal → poll until analysis JSON is ready.
            //   2. Plugin-side crash status override patched into the JSON.
            //   3. Fetch the live analyzer page HTML, rewrite asset URLs to absolute,
            //      inject the analysis JSON + an auto-render IIFE that hands it to the
            //      website's own renderSummaryCards / renderSessionInfo / renderCrashReport
            //      / etc. — the SAME functions that run when a user drag-drops manually.
            //   4. Pre-fetch the website's official PDF (/generate-pdf) so the page's
            //      Download PDF Report button serves the real server PDF directly. If
            //      /generate-pdf is empty/down, fall back to Edge headless rendering the
            //      populated dashboard HTML → still a real PDF, just generated locally.
            //   5. Open the populated dashboard in the user's default browser.
            //
            // Net result: same React UI + same Download flow the website provides to
            // manual drag-drop uploaders, just without the drag.
            btn.IsEnabled = false;
            var originalContent = btn.Content;
            btn.Content = "Uploading...";

            _activeCts = new CancellationTokenSource();
            try
            {
                // pluginDetectedCrash: true → the dashboard banner agrees with the Crash
                // Register row's Status (plugin's process-death detection beats the
                // analyzer's journal-content inference for definitively-dead sessions).
                await UploadAndAnalyzeAsync(journalPath, btn, pluginDetectedCrash: true, _activeCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogInfo("Journal upload cancelled by user.");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Auto-upload/report failed ({ex.Message}); falling back to website + drag-drop staging.");
                try
                {
                    Process.Start(new ProcessStartInfo(AnalyzerBaseUrl)
                    {
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Normal
                    });
                }
                catch { }
                try { System.Windows.Clipboard.SetText(journalPath); } catch { }
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{journalPath}\"")
                    {
                        UseShellExecute = true
                    });
                }
                catch { }

                ZeMessageBox.Show(
                    "Auto-Upload Unavailable",
                    "The journal could not be uploaded automatically.\n\n" +
                    "The website has been opened in your browser and the journal file is selected " +
                    "in File Explorer — drag it onto the \"Drop your Journal\" zone (path also " +
                    "copied to clipboard).\n\n" +
                    $"Details: {ex.Message}",
                    ZeMessageType.Warning);
            }
            finally
            {
                _activeCts?.Dispose();
                _activeCts = null;
                btn.Content = originalContent;
                btn.IsEnabled = true;
            }
        }

        /// <summary>
        /// Finds the journal file by checking the full path first, then searching Revit Journals folders.
        /// </summary>
        private string? FindJournalFile(string journalFileName, string journalFileNameShort, string? revitVersion)
        {
            // 1. Check if the stored path is already a full path that exists
            if (!string.IsNullOrEmpty(journalFileName) && journalFileName != "-" && File.Exists(journalFileName))
                return journalFileName;

            var fileName = journalFileNameShort;
            if (string.IsNullOrEmpty(fileName) || fileName == "-")
                return null;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // 2. Try specific Revit version folder first
            if (!string.IsNullOrEmpty(revitVersion))
            {
                var versionFolder = Path.Combine(localAppData, "Autodesk", "Revit", $"Autodesk Revit {revitVersion}", "Journals");
                var versionPath = Path.Combine(versionFolder, fileName);
                if (File.Exists(versionPath))
                    return versionPath;
            }

            // 3. Search all Revit version Journals folders
            var revitBase = Path.Combine(localAppData, "Autodesk", "Revit");
            if (Directory.Exists(revitBase))
            {
                foreach (var dir in Directory.GetDirectories(revitBase, "Autodesk Revit *"))
                {
                    var journalsDir = Path.Combine(dir, "Journals");
                    if (Directory.Exists(journalsDir))
                    {
                        var fullPath = Path.Combine(journalsDir, fileName);
                        if (File.Exists(fullPath))
                            return fullPath;
                    }
                }
            }

            return null;
        }

        private string GetSearchedPaths(string? revitVersion)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var paths = new List<string>();

            if (!string.IsNullOrEmpty(revitVersion))
                paths.Add(Path.Combine(localAppData, "Autodesk", "Revit", $"Autodesk Revit {revitVersion}", "Journals"));

            var revitBase = Path.Combine(localAppData, "Autodesk", "Revit");
            if (Directory.Exists(revitBase))
            {
                foreach (var dir in Directory.GetDirectories(revitBase, "Autodesk Revit *"))
                {
                    var journalsDir = Path.Combine(dir, "Journals");
                    if (!paths.Contains(journalsDir))
                        paths.Add(journalsDir);
                }
            }

            return paths.Count > 0 ? string.Join("\n", paths.Select(p => $"  - {p}")) : "  (no Revit folders found)";
        }

        /// <summary>
        /// Uploads the journal file to the analyzer via the SAS-based chunked flow
        /// (avoids the 230s gateway timeout that the legacy /upload endpoint hit on large
        /// journals), pre-generates the PDF from C# (avoids browser CORS), builds an HTML
        /// dashboard page with the analysis data, and opens it in the browser.
        /// </summary>
        private async Task UploadAndAnalyzeAsync(
            string journalFilePath, Button btn, bool pluginDetectedCrash, CancellationToken ct)
        {
            var totalBytes = new FileInfo(journalFilePath).Length;
            // Progress<long> captures the current SynchronizationContext (UI thread here),
            // so the lambda runs on the dispatcher — safe to touch btn directly.
            var progress = new Progress<long>(bytes =>
            {
                if (totalBytes <= 0) return;
                var pct = (int)(bytes * 100 / totalBytes);
                if (pct < 0) pct = 0;
                if (pct > 100) pct = 100;
                btn.Content = $"Uploading {pct}%...";
            });

            var analyzer = new JournalAnalyzerClient(AnalyzerBaseUrl, _logger);
            var (analysisJson, jobId) = await analyzer.UploadAndAnalyzeWithJobIdAsync(journalFilePath, progress, ct);

            // Stage tracker: every step from here on logs entry + exit so a hang or silent
            // exception is pinpointable in the log. Last "Building report >>" line printed
            // tells us exactly which step is wedged.
            string stage = "received-json";
            try
            {
                var preview = analysisJson.Length > 200
                    ? analysisJson.Substring(0, 200) + "..."
                    : analysisJson;
                _logger?.LogInfo($"Building report >> analysis received ({analysisJson.Length} chars). Preview: {preview}");

                btn.Content = "Building report...";

                // Override the analyzer's session status when the plugin detected a crash but the
                // journal content didn't trigger one server-side. Keeps the UI consistent with the
                // Crash Register list. We patch the JSON before it's used to build the PDF + dashboard.
                if (pluginDetectedCrash)
                {
                    stage = "override-status";
                    _logger?.LogInfo("Building report >> override-status start");
                    analysisJson = OverrideStatusToCrashed(analysisJson);
                    _logger?.LogInfo($"Building report >> override-status complete ({analysisJson.Length} chars)");
                }

                // Pre-compute the PDF path. We DON'T call the analyzer's /generate-pdf
                // endpoint here anymore — it accepts our POSTed analysis JSON but produces
                // a default empty PDF (Status: Active, Revit Version: Unknown, Total
                // Errors: 0, Session Duration: N/A) regardless of the data we send. The
                // crash override we apply client-side never survives that round-trip
                // either, so the PDF contradicts the dashboard the user sees. Instead
                // we render the SAME populated HTML to PDF locally via Edge headless
                // (same pattern DetailedReportGenerator uses for Model Health reports)
                // — that guarantees PDF == HTML, including our crash override.
                var pdfFileName = $"{Path.GetFileNameWithoutExtension(journalFilePath)}_report.pdf";
                var pdfTempPath = Path.Combine(Path.GetTempPath(), pdfFileName);

                // Fetch the website page HTML and build a local dashboard with pre-loaded data
                stage = "dashboard-fetch";
                _logger?.LogInfo($"Building report >> dashboard-fetch start ({AnalyzerBaseUrl})");
                string pageHtml;
                try
                {
                    pageHtml = await ReportHttpClient.GetStringAsync(AnalyzerBaseUrl);
                }
                catch (HttpRequestException ex)
                {
                    _logger?.LogWarning($"Failed to fetch dashboard page: {ex.Message}");
                    throw new HttpRequestException($"Analysis completed but failed to fetch dashboard page: {ex.Message}");
                }
                _logger?.LogInfo($"Building report >> dashboard-fetch complete ({pageHtml.Length} chars)");

                // Replace relative URLs with absolute URLs so CSS/JS load from the server
                stage = "html-build";
                _logger?.LogInfo("Building report >> html-build start");
                pageHtml = pageHtml.Replace("href=\"/static/", $"href=\"{AnalyzerBaseUrl}/static/");
                pageHtml = pageHtml.Replace("src=\"/static/", $"src=\"{AnalyzerBaseUrl}/static/");

                // Inject ZeManage favicon and update title + forcible upload/results visibility CSS.
                //
                // The CSS rule below is the LOAD-BEARING fix for the "PDF shows landing-page
                // feature cards" bug. Previously the upload section was hidden only by a JS
                // call (uploadSection.classList.add('hidden')) inside our injected auto-render
                // script. Two failure modes made that unreliable:
                //   1. Edge headless can capture before the auto-render IIFE's setInterval
                //      poll resolves — so .classList.add('hidden') hasn't run yet at print time.
                //   2. The server's PDF template uses its own .hidden rule that isn't always
                //      `display:none !important` — sibling utility classes can override it.
                // Inline CSS with !important applied in <head> takes effect on the FIRST paint,
                // before any JS runs, so the landing page can never appear in the PDF regardless
                // of when Edge captures. Identical applied to results-section so the populated
                // content is forced visible even if the auto-render script's class toggle never
                // fires.
                // Inject favicon + a hard CSS override that controls which page sections
                // appear in the rendered PDF. The selectors below match the EXACT structure
                // of the staging analyzer page (verified 2026-05-28 by curling the live
                // markup):
                //
                //   <body>
                //     <div class="app-container">
                //       <header class="header">
                //         <div class="header-content">
                //           <img src="logo">
                //           <p class="tagline">> Attach the log for instant review</p>   ← HIDE
                //         </div>
                //       </header>
                //       <main class="main-content">
                //         <section id="upload-section">...drag-drop area...</section>     ← HIDE
                //         <section id="results-section" class="hidden">
                //           <div class="actions-bar">
                //             <button>New Analysis</button>
                //             <button>Download PDF Report</button>                        ← HIDE (meta button)
                //           </div>
                //           <div id="crash-banner">CRASH DETECTED ...</div>               ← KEEP
                //           <div id="summary-grid">Status / Revit Version cards</div>     ← KEEP
                //           ...tabs with the populated analysis content...                ← KEEP
                //         </section>
                //       </main>
                //       <section id="infoSection" class="info-section">                   ← HIDE
                //         <!-- Journal File / Crash Analysis / System Overview /
                //              Error & Warning Detection / Smart Solutions cards -->
                //       </section>
                //     </div>
                //   </body>
                //
                // The previous fix only hid #upload-section, which is why the feature
                // cards still appeared in the PDF — they live in #infoSection (a sibling
                // of <main>, not inside upload-section). The "Attach the log" tagline
                // sits inside the header and was also slipping through. Hiding all three
                // via inline CSS in <head> takes effect on first paint, before Edge
                // headless captures, so it's order-of-execution independent.
                // CSS visibility override — scoped to @media print so it only affects the
                // Edge-headless PDF render and leaves the LIVE HTML dashboard untouched.
                //
                // Earlier revision applied the hides globally (no @media wrapper) and
                // broke the live dashboard: the Download PDF button is inside .actions-bar,
                // so the user could no longer click it from the embedded browser. The
                // feature-cards / tagline / upload-section hides are correct — but they
                // must ONLY apply during print, not during interactive viewing.
                //
                // Edge headless --print-to-pdf triggers @media print, so this block
                // executes for the PDF render and is skipped for screen viewing.
                //
                // The screen-side hides (upload-section, infoSection during normal viewing)
                // are still handled by the website's own JS — uploadSection.classList.add('hidden')
                // runs from the injected auto-render script the moment data is loaded. We
                // don't duplicate them here for screen.
                // CSS visibility override — scoped to @media print so it only affects the
                // Edge-headless PDF render and leaves the LIVE HTML dashboard untouched.
                //
                // Key additions on 28 May 2026: ALL tab panels (Session Info, Revit Crash
                // Report, Issues & Errors, Timeline, Add-ins, Workflow, KB Articles) are
                // expanded in the PDF. Previously the inactive ones were left hidden by
                // their default display:none, so the PDF only contained Session Info —
                // useless for a comprehensive crash report. Tab navigation chrome is also
                // hidden because you can't click in a PDF.
                var logoDataUri = "data:image/png;base64," + BIManageRevit.BIManage.Views.Metrics.ModelHealthDashboard.LOGO_BASE64;
                var headInjection = $"<head>\n<link rel=\"icon\" type=\"image/png\" href=\"{logoDataUri}\">\n"
                    + "<style id=\"bimanage-pdf-visibility-override\">\n"
                    + "  @media print {\n"
                    + "    /* Upload + landing intro */\n"
                    + "    #upload-section, .upload-section, [data-section=\"upload\"] { display: none !important; }\n"
                    + "    /* Feature explainer cards (Journal File / Crash Analysis / System Overview / Error & Warning Detection / Smart Solutions) */\n"
                    + "    #infoSection, .info-section { display: none !important; }\n"
                    + "    /* 'Attach the log for instant review' tagline in the page header */\n"
                    + "    .tagline { display: none !important; }\n"
                    + "    /* Meta buttons (New Analysis / Download PDF Report) — irrelevant in a static PDF.\n"
                    + "       INTERACTIVE Download button is intentionally preserved on-screen. */\n"
                    + "    .actions-bar { display: none !important; }\n"
                    + "    /* Force the populated results section visible regardless of the .hidden class */\n"
                    + "    #results-section, .results-section, [data-section=\"results\"] { display: block !important; visibility: visible !important; opacity: 1 !important; }\n"
                    + "    #results-section.hidden { display: block !important; }\n"
                    + "    /* TAB EXPANSION: hide the clickable tab nav bar (no clicks in a PDF)\n"
                    + "       and force every tab panel visible so the PDF contains the FULL report\n"
                    + "       (Session Info + Revit Crash Report + Issues & Errors + Timeline +\n"
                    + "        Add-ins + Workflow + KB Articles), not just whichever tab happened\n"
                    + "       to be active when Edge rendered. */\n"
                    + "    .tabs { display: none !important; }\n"
                    + "    .tab-panel, .tab-panel.hidden,\n"
                    + "    #panel-session, #panel-crash, #panel-issues, #panel-timeline,\n"
                    + "    #panel-addins, #panel-workflow, #panel-kb {\n"
                    + "        display: block !important;\n"
                    + "        visibility: visible !important;\n"
                    + "        opacity: 1 !important;\n"
                    + "    }\n"
                    + "    /* Each tab panel gets a printed-section heading + a separator above so the\n"
                    + "       reader can tell sections apart now that they all flow vertically. */\n"
                    + "    .tab-panel { page-break-inside: avoid; margin-top: 18px; padding-top: 12px;\n"
                    + "                 border-top: 1px solid #E2E8F0; }\n"
                    + "    .tab-panel:first-of-type { border-top: none; margin-top: 0; padding-top: 0; }\n"
                    + "  }\n"
                    + "</style>";
                pageHtml = pageHtml.Replace("<head>", headInjection);
                pageHtml = pageHtml.Replace("<title>Revit Journal Analyzer</title>", "<title>ZeManage - Journal Analyzer</title>");

                // Build the PDF download override script
                var pdfPathJs = pdfTempPath != null
                    ? $"'{pdfTempPath.Replace("\\", "\\\\")}'"
                    : "null";

                // Inject script that auto-renders dashboard and overrides PDF download
                var autoRenderScript = $@"
<script>
// Make analysis data available at top-level scope so the IIFE poll-renderer
// AND the window.load handler below both see the same object.
var _raw = {analysisJson};
var data = (_raw && _raw.result) ? _raw.result : _raw;

// Poll-and-run render. Earlier versions used a single window.load handler that
// fired the render functions exactly once — if /static/script.js hadn't finished
// fetching/parsing by that moment (slow network, headless Edge cold-start), the
// `typeof === 'function'` guards silently skipped every call and the PDF captured
// the landing-page feature cards with their placeholder text instead of the
// populated analysis (the symptom reported 2026-05-27: ""only header + status
// page in the downloaded PDF""). The poll retries up to 100 × 100 ms = 10 s,
// then runs whatever subset of renders has loaded by then.
(function() {{
    var attempts = 0;
    var maxAttempts = 100;
    var poll = setInterval(function() {{
        attempts++;
        // Wait until script.js has defined the core renderer OR we've timed out.
        var ready = (typeof renderSummaryCards === 'function')
                 || (typeof renderSessionInfo === 'function');
        if (!ready && attempts < maxAttempts) return;
        clearInterval(poll);

        // Hide upload section, show results
        var uploadSection = document.getElementById('upload-section');
        var resultsSection = document.getElementById('results-section');
        if (uploadSection) uploadSection.classList.add('hidden');
        if (resultsSection) resultsSection.classList.remove('hidden');

        // Make analysis data globally available for the website's own handlers.
        try {{ analysisData = data; }} catch (e) {{ window.analysisData = data; }}

        // Call every render function — guards handle the rare case where one
        // is missing (older deploys / partial script.js load).
        try {{ if (typeof renderSummaryCards === 'function') renderSummaryCards(data); }} catch (e) {{ console.error('renderSummaryCards', e); }}
        try {{ if (typeof renderSessionInfo === 'function') renderSessionInfo(data.session_info, data); }} catch (e) {{ console.error('renderSessionInfo', e); }}
        try {{ if (typeof renderIssues === 'function') renderIssues(data.errors); }} catch (e) {{ console.error('renderIssues', e); }}
        try {{ if (typeof renderTimeline === 'function') renderTimeline(data.timeline); }} catch (e) {{ console.error('renderTimeline', e); }}
        try {{ if (typeof renderAddins === 'function') renderAddins(data.addins); }} catch (e) {{ console.error('renderAddins', e); }}
        try {{ if (typeof renderWorkflow === 'function') renderWorkflow(data.workflow); }} catch (e) {{ console.error('renderWorkflow', e); }}
        try {{ if (typeof renderKbArticles === 'function') renderKbArticles(data.kb_articles); }} catch (e) {{ console.error('renderKbArticles', e); }}
        // Revit Crash Report tab — verified against /static/script.js renderResults():
        // the website's natural upload flow calls renderCrashReport(data.crash_report) +
        // initCrNav() after the analysis JSON arrives. Skipping these is why the
        // Crash Overview / Root Cause / Fatal Errors sections rendered blank when the
        // plugin auto-attached the log (user-reported 2026-05-25).
        try {{ if (typeof renderCrashReport === 'function') renderCrashReport(data.crash_report); }} catch (e) {{ console.error('renderCrashReport', e); }}
        try {{ if (typeof initCrNav === 'function') initCrNav(); }} catch (e) {{ console.error('initCrNav', e); }}

        // Sentinel: mark body once renders have run so Edge headless captures a
        // populated DOM. Title change is the secondary signal (visible in headless
        // logs) — Chromium's virtual-time-budget will keep advancing time until the
        // event loop is idle, and by that point this attribute is set.
        document.body.setAttribute('data-render-complete', '1');
        document.title = 'ZeManage - Journal Analyzer (Ready)';
    }}, 100);
}})();

window.addEventListener('load', function() {{

    // Hide New Analysis button
    var newAnalysisBtn = document.getElementById('newAnalysisBtn');
    if (newAnalysisBtn) newAnalysisBtn.style.display = 'none';

    // Show crash banner if crashed
    if (data.summary && data.summary.session_status === 'Crashed') {{
        var banner = document.getElementById('crash-banner');
        if (banner) banner.classList.remove('hidden');
    }}

    // Download PDF button — two-mode behaviour wired up via pdfPath:
    //   pdfPath set (server /generate-pdf succeeded in C#, we have a local PDF
    //     that came straight from the server with no plugin-side rendering) ->
    //     click opens the local file:// PDF in a new tab. One-click download,
    //     exact server-rendered Comprehensive Analysis Report PDF.
    //   pdfPath null (server returned 5xx / empty default / network failed) ->
    //     click opens the analyzer website in a new tab so the user can upload
    //     the journal there and trigger the website own Export Analysis Report
    //     button (only reliable path left when the server is intermittent).
    // Tooltip distinguishes the two modes so the user knows what to expect
    // before clicking.
    var pdfPath = {pdfPathJs};
    var pdfBtn = document.getElementById('downloadPdfBtn');
    if (pdfBtn) {{
        var newBtn = pdfBtn.cloneNode(true);
        pdfBtn.parentNode.replaceChild(newBtn, pdfBtn);
        if (pdfPath) {{
            newBtn.title = 'Download the official analyzer PDF (pre-fetched from the server).';
            newBtn.addEventListener('click', function(e) {{
                e.preventDefault();
                window.open('file:///' + pdfPath.replace(/\\\\/g, '/'), '_blank');
            }});
        }} else {{
            newBtn.title = 'Server PDF was unavailable. Click to open the analyzer website, upload this journal there, and click Export Analysis Report to download the official PDF.';
            newBtn.addEventListener('click', function(e) {{
                e.preventDefault();
                window.open('{AnalyzerBaseUrl}/', '_blank');
            }});
        }}
    }}
}});
</script>";

                // Insert auto-render script before </body>
                pageHtml = pageHtml.Replace("</body>", autoRenderScript + "\n</body>");
                _logger?.LogInfo($"Building report >> html-build complete ({pageHtml.Length} chars)");

                // Save and open in browser
                stage = "html-write";
                var tempHtml = Path.Combine(Path.GetTempPath(), $"crash_analysis_{Path.GetFileNameWithoutExtension(journalFilePath)}.html");
                _logger?.LogInfo($"Building report >> html-write start ({tempHtml})");
                File.WriteAllText(tempHtml, pageHtml, System.Text.Encoding.UTF8);
                _logger?.LogInfo("Building report >> html-write complete");

                // Try the server /generate-pdf endpoint ONCE so the Download PDF button
                // on the local dashboard can open the resulting file directly — a true
                // one-click download for the exact server-rendered Comprehensive Analysis
                // Report PDF. NO client-side approximation fallbacks (Edge on our own
                // HTML, etc.) — those produced layouts the user kept rejecting. If the
                // server returns 5xx / empty default / network failure, pdfTempPath stays
                // null and the Download PDF button instead opens the analyzer website in
                // a new tab (handled in the inject-script branch above), so the user
                // always has a path to the official PDF even during server outages.
                stage = "pdf-generate";
                _logger?.LogInfo($"Building report >> pdf-generate start (server /generate-pdf, jobId={jobId})");
                bool serverPdfOk;
                try
                {
                    serverPdfOk = await analyzer.DownloadPdfForAnalysisAsync(analysisJson, pdfTempPath, ct);
                }
                catch (Exception pex)
                {
                    _logger?.LogWarning($"Building report >> server /generate-pdf threw: {pex.GetType().Name}: {pex.Message}");
                    serverPdfOk = false;
                }
                if (serverPdfOk)
                {
                    _logger?.LogInfo($"Building report >> server PDF saved ({new FileInfo(pdfTempPath).Length} bytes) — Download PDF button will open it directly.");
                }
                else
                {
                    _logger?.LogInfo("Building report >> server PDF unavailable — Download PDF button will open the analyzer website for manual export.");
                    // Force the inject script's pdfPath placeholder to null so the
                    // button takes the website-fallback branch (see the inject script
                    // above for the two modes).
                    pageHtml = pageHtml.Replace($"var pdfPath = {pdfPathJs};", "var pdfPath = null;");
                    File.WriteAllText(tempHtml, pageHtml, System.Text.Encoding.UTF8);
                }

                // Open browser in a separate process so it doesn't affect this window
                stage = "browser-launch";
                _logger?.LogInfo("Building report >> browser-launch start");
                var psi = new ProcessStartInfo(tempHtml)
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };
                Process.Start(psi);
                _logger?.LogInfo($"Building report >> browser-launch complete (Dashboard opened: {tempHtml})");
            }
            catch (Exception ex)
            {
                // Surface anything thrown during the post-analysis report build instead of
                // letting it bubble silently to the outer Analysis_Click catch — the stage
                // name tells us exactly which step failed.
                _logger?.LogError($"Building report FAILED at stage '{stage}': {ex.GetType().Name}: {ex.Message}", ex);
                throw;
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();

        /// <summary>
        /// Renders the populated dashboard HTML to PDF by shelling out to Microsoft Edge
        /// in headless mode (same engine that paints the HTML on screen). The PDF therefore
        /// matches the dashboard exactly — including our client-side crash override and any
        /// JS-rendered cards. Replaces the previous approach of POSTing the analysis JSON to
        /// the analyzer's /generate-pdf endpoint, which was returning an empty default PDF
        /// regardless of the input (Status: Active, Revit Version: Unknown, Total Errors: 0).
        /// Returns true if the PDF was written successfully, false otherwise.
        /// </summary>
        private bool TryRenderHtmlToPdfViaEdge(string htmlFilePath, string pdfOutputPath)
        {
            try
            {
                var edgePath = LocateMsEdge();
                if (edgePath == null)
                {
                    _logger?.LogWarning("Edge headless PDF: msedge.exe not found in standard locations — download button will be disabled.");
                    return false;
                }

                // --headless                              run without a window
                // --disable-gpu                           skip GPU init in headless mode (Chromium docs recommend)
                // --no-pdf-header-footer                  drop the auto-injected URL/date header & page-number footer
                // --virtual-time-budget=30000             advance virtual time by 30s before printing.
                //                                         Bumped from 10s on 2026-05-27 after the user reported
                //                                         the downloaded PDF only contained the landing-page
                //                                         feature cards (placeholder text) instead of the
                //                                         populated analysis. Root cause: the injected
                //                                         auto-render script polls every 100 ms for
                //                                         renderSummaryCards / renderSessionInfo to be defined,
                //                                         and on slow/cold-start headless Edge the poll could
                //                                         take more than 10 s to settle. 30 s gives ample
                //                                         margin while keeping the worst-case print time bounded
                //                                         (the 90 s WaitForExit budget below still applies).
                // --run-all-compositor-stages-before-draw force a full compositor cycle so the rendered DOM
                //                                         is fully painted into the PDF, not a partial frame.
                // --print-to-pdf=<out>                    write vector PDF directly to disk, no print dialog
                // <input>                                 absolute path to the HTML we just wrote
                var psi = new ProcessStartInfo
                {
                    FileName               = edgePath,
                    Arguments              = $"--headless --disable-gpu --no-pdf-header-footer "
                                           + $"--virtual-time-budget=30000 "
                                           + $"--run-all-compositor-stages-before-draw "
                                           + $"--print-to-pdf=\"{pdfOutputPath}\" \"{htmlFilePath}\"",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardError  = true,
                    RedirectStandardOutput = true,
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    _logger?.LogWarning("Edge headless PDF: Process.Start returned null.");
                    return false;
                }

                // 90s budget covers cold-start + render time for a journal report that
                // calls back to the analyzer server for CSS/JS assets. If Edge hangs
                // past that, kill it and report failure rather than blocking the UI.
                if (!proc.WaitForExit(90_000))
                {
                    try { proc.Kill(); } catch { }
                    _logger?.LogWarning("Edge headless PDF: timed out after 90 seconds.");
                    return false;
                }

                if (proc.ExitCode != 0)
                {
                    var stderr = proc.StandardError.ReadToEnd();
                    _logger?.LogWarning($"Edge headless PDF: exited with code {proc.ExitCode}. stderr: {stderr}");
                    return false;
                }

                if (!File.Exists(pdfOutputPath) || new FileInfo(pdfOutputPath).Length == 0)
                {
                    _logger?.LogWarning($"Edge headless PDF: output file missing or empty at {pdfOutputPath}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Edge headless PDF threw: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Locate msedge.exe on the standard Windows install paths. Edge ships preinstalled
        /// on every Windows 10/11 box, so this almost always finds it. Returns null if Edge
        /// isn't present (caller falls back to disabling the PDF button).
        /// </summary>
        private static string? LocateMsEdge()
        {
            string[] candidates =
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            };
            foreach (var p in candidates)
            {
                try { if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }

        /// <summary>
        /// When the plugin already classified the session as crashed (e.g. force-kill, dead PID),
        /// override the analyzer's status so the dashboard/PDF agree with the Crash Register list.
        /// Patches common status fields if present; otherwise injects pluginDetectedCrash.
        /// </summary>
        private string OverrideStatusToCrashed(string analysisJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(analysisJson)) return analysisJson;

                using var doc = System.Text.Json.JsonDocument.Parse(analysisJson);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return analysisJson;

                // The analyzer wraps the payload as { "result": { ... } }. The dashboard's
                // unwrap step strips that wrapper, so any override we add at the top level
                // would be lost. Detect the wrapper and write the override inside result
                // (and inside result.summary.session_status if that path exists) so the
                // crash banner & status card see "Crashed" after unwrap.
                var rewritten = RewriteWithCrashOverride(doc.RootElement);
                _logger?.LogDebug("Analysis JSON patched: status overridden to Crashed");
                return rewritten;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to patch analysis JSON: {ex.Message}");
                return analysisJson;
            }
        }

        private static string RewriteWithCrashOverride(System.Text.Json.JsonElement root)
        {
            using var ms = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = false }))
            {
                WriteObjectWithCrashOverride(writer, root, isResultPayload: !root.TryGetProperty("result", out _));
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        // Writes an object element while overriding any status-shaped fields to "Crashed".
        // When isResultPayload is true, this object IS the analyzer's result payload (either
        // because there's no wrapper, or we just dove into result), so we also ensure
        // summary.session_status = "Crashed".
        private static void WriteObjectWithCrashOverride(System.Text.Json.Utf8JsonWriter writer, System.Text.Json.JsonElement obj, bool isResultPayload)
        {
            writer.WriteStartObject();
            bool wroteCrashedFlag = false;
            bool wroteStatus = false;
            bool wroteSummary = false;

            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, "status", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prop.Name, "sessionStatus", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prop.Name, "session_status", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteString(prop.Name, "Crashed");
                    wroteStatus = true;
                    continue;
                }
                if (string.Equals(prop.Name, "isCrashed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prop.Name, "is_crashed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(prop.Name, "crashed", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteBoolean(prop.Name, true);
                    wroteCrashedFlag = true;
                    continue;
                }
                // Recurse into the result wrapper so the override lands where the dashboard reads it.
                if (string.Equals(prop.Name, "result", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    writer.WritePropertyName(prop.Name);
                    WriteObjectWithCrashOverride(writer, prop.Value, isResultPayload: true);
                    continue;
                }
                // Recurse into summary (only when we're inside the result payload) to override session_status.
                if (isResultPayload
                    && string.Equals(prop.Name, "summary", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    writer.WritePropertyName(prop.Name);
                    WriteObjectWithCrashOverride(writer, prop.Value, isResultPayload: false);
                    wroteSummary = true;
                    continue;
                }
                prop.WriteTo(writer);
            }

            if (isResultPayload && !wroteSummary)
            {
                writer.WritePropertyName("summary");
                writer.WriteStartObject();
                writer.WriteString("session_status", "Crashed");
                writer.WriteEndObject();
            }
            if (!wroteStatus) writer.WriteString("status", "Crashed");
            if (!wroteCrashedFlag) writer.WriteBoolean("pluginDetectedCrash", true);

            writer.WriteEndObject();
        }
    }

    public class CrashedSessionViewModel
    {
        public int RowNumber { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string StartedAtLocal { get; set; } = string.Empty;
        public string EndedAtLocal { get; set; } = string.Empty;
        public string Duration { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string JournalFileName { get; set; } = string.Empty;
        public string JournalFileNameShort { get; set; } = string.Empty;
        public string? RevitVersion { get; set; }
    }
}
