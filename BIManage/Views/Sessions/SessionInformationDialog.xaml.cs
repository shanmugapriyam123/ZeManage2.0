using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Autodesk.Revit.UI;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Helpers;


namespace BIManageRevit.BIManage.Views.Sessions
{
    public partial class SessionInformationDialog : Window
    {
        private readonly SessionRepository _sessionRepository;
        private readonly RegisteredModelsRepository? _registeredModels;
        private readonly ModelSyncService? _modelSyncService;
        private readonly ILogger _logger;
        private readonly string _currentSessionId;
        private readonly UIApplication _uiApp;
        private List<OpenDocumentViewModel> _allDocuments = new List<OpenDocumentViewModel>();

        public SessionInformationDialog(
            SessionRepository sessionRepository,
            ILogger logger,
            string currentSessionId,
            UIApplication uiApp,
            RegisteredModelsRepository? registeredModels = null,
            ModelSyncService? modelSyncService = null)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
            _registeredModels = registeredModels;
            _modelSyncService = modelSyncService;
            _logger = logger;
            _currentSessionId = currentSessionId;
            _uiApp = uiApp;

            Loaded += SessionInformationDialog_Loaded;
        }

        private async void SessionInformationDialog_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadDataAsync();
        }

        private async Task LoadDataAsync()
        {
            try
            {
                // Load current session info
                await LoadSessionInfoAsync();

                // Load open documents
                await LoadOpenDocumentsAsync();

            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load session data: {ex.Message}", ex);
                MessageBox.Show($"Failed to load session data: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Safely converts a DateTime to local time, handling cases where Kind is not set
        /// </summary>
        private DateTime ToLocalTime(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Utc)
            {
                return dateTime.ToLocalTime();
            }
            else if (dateTime.Kind == DateTimeKind.Local)
            {
                return dateTime;
            }
            else
            {
                // Kind is Unspecified - assume it's UTC and convert
                return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc).ToLocalTime();
            }
        }

        /// <summary>
        /// Formats a DateTime to local time string
        /// </summary>
        private string FormatLocalDateTime(DateTime dateTime)
        {
            return ToLocalTime(dateTime).ToString("yyyy-MM-dd HH:mm:ss");
        }

        /// <summary>
        /// Calculates duration from a DateTime (assumed UTC) to now
        /// </summary>
        private TimeSpan CalculateDuration(DateTime startTime)
        {
            DateTime utcStart;
            if (startTime.Kind == DateTimeKind.Utc)
            {
                utcStart = startTime;
            }
            else if (startTime.Kind == DateTimeKind.Local)
            {
                utcStart = startTime.ToUniversalTime();
            }
            else
            {
                // Assume UTC if unspecified
                utcStart = DateTime.SpecifyKind(startTime, DateTimeKind.Utc);
            }
            return DateTime.UtcNow - utcStart;
        }

        private async Task LoadSessionInfoAsync()
        {
            var session = await _sessionRepository.GetSessionFullAsync(_currentSessionId);
            if (session == null)
            {
                return;
            }

            // Started At - convert to local time
            TxtStartedAt.Text = FormatLocalDateTime(session.StartedAt);

            // Session Duration - calculate from started_at to now
            var duration = CalculateDuration(session.StartedAt);
            TxtSessionDuration.Text = FormatDuration(duration);

            // Computer Name
            TxtComputerName.Text = session.ComputerName ?? "-";

            // Revit Build
            TxtRevitBuild.Text = session.RevitBuild ?? "-";

            // Revit Username
            TxtRevitUsername.Text = session.RevitUsername ?? "-";

            // Loaded Plugin Count
            TxtPluginCount.Text = session.LoadedPluginCount?.ToString() ?? "-";

            // Opening Duration
            if (session.OpeningDurationSeconds.HasValue && session.OpeningDurationSeconds.Value > 0)
                TxtOpeningDuration.Text = $"{session.OpeningDurationSeconds.Value:F1}s";
            else
                TxtOpeningDuration.Text = "-";

            // Journal File Name - get from live Revit Application
            try
            {
                var journalPath = _uiApp?.Application?.RecordingJournalFilename;
                if (!string.IsNullOrEmpty(journalPath))
                {
                    TxtJournalFileName.Text = System.IO.Path.GetFileName(journalPath);
                    TxtJournalFileName.ToolTip = journalPath; // Full path in tooltip
                }
                else if (!string.IsNullOrEmpty(session.JournalFileName))
                {
                    // Fallback to database value
                    TxtJournalFileName.Text = System.IO.Path.GetFileName(session.JournalFileName);
                    TxtJournalFileName.ToolTip = session.JournalFileName;
                }
                else
                {
                    TxtJournalFileName.Text = "-";
                }
            }
            catch
            {
                TxtJournalFileName.Text = "-";
            }
        }

        private async Task LoadOpenDocumentsAsync()
        {
            try
            {
                _allDocuments = new List<OpenDocumentViewModel>();

                // Get open documents directly from Revit Application
                if (_uiApp?.Application?.Documents != null)
                {
                    int rowNum = 1;
                    foreach (Autodesk.Revit.DB.Document doc in _uiApp.Application.Documents)
                    {
                        if (doc == null || doc.IsLinked) continue;

                        var docPath = doc.PathName ?? "";
                        var docTitle = doc.Title ?? System.IO.Path.GetFileNameWithoutExtension(docPath) ?? "Untitled";
                        var isWorkshared = doc.IsWorkshared;
                        var isFamily = doc.IsFamilyDocument;
                        // Cloud detection: prefer the Revit API (ModelPath.CloudPath) because
                        // cloud-only models in Revit 2022+ often have an empty PathName, so the
                        // legacy string-prefix checks ("BIM 360://", "ACC://", contains "cloud")
                        // miss them. Fall back to the path-string scan for older Revit versions
                        // and for the DB-fallback path below where the live ModelPath is not
                        // available.
                        bool isCloud = false;
                        try
                        {
                            if (isWorkshared)
                            {
                                var centralPath = doc.GetWorksharingCentralModelPath();
                                if (centralPath != null && centralPath.CloudPath)
                                    isCloud = true;
                            }
                        }
                        catch { /* non-workshared or sandboxed — fall through to string check */ }
                        if (!isCloud)
                        {
                            isCloud = docPath.StartsWith("BIM 360://") ||
                                      docPath.StartsWith("ACC://") ||
                                      docPath.IndexOf("cloud", StringComparison.OrdinalIgnoreCase) >= 0;
                        }

                        // Prefer the API-synced project name from registered_models — that's
                        // what the ZeManage dashboard shows (e.g. "Default Project"). The Revit
                        // ProjectInformation.Name/Number fields are local user-editable text that
                        // often holds dates, project codes (e.g. "4800000856"), or is just blank.
                        // Only fall through to the Revit chain when there's no registered_models
                        // row yet (model not yet synced with the backend).
                        var projectName = await ResolveProjectNameFromBackendAsync(doc, docTitle);
                        if (string.IsNullOrWhiteSpace(projectName))
                            projectName = ResolveProjectName(doc, isFamily, docPath, docTitle);

                        // Final guard: if every source returned the document name (which happens
                        // when the registration payload mistakenly copied modelName into projectName
                        // upstream), clear the field so the Project column doesn't just echo the
                        // Document Name cell. Empty is more honest than duplicated.
                        if (!string.IsNullOrWhiteSpace(projectName)
                            && !string.IsNullOrWhiteSpace(docTitle)
                            && IsSameAsDocumentName(projectName, docTitle))
                        {
                            projectName = string.Empty;
                        }

                        // Resolve model GUID up-front so the background patcher can hit the
                        // API by GUID if the project name is missing or looks wrong.
                        string? modelGuidForRow = null;
                        try { modelGuidForRow = ModelGuidHelper.GetModelGuid(doc, _logger); }
                        catch { }

                        // No Revit-side / strip-from-model fallback — user policy (28 May 2026):
                        // the Project cell shows ONLY the web/API project name. If the backend
                        // has no value, show an em dash placeholder ("—") so the cell never
                        // looks broken / blank. The async patcher (FetchProjectNameFromApiAndPatch
                        // below) replaces "—" with the real name in-place when the GET
                        // /api/v1/Revit/models/{guid} call returns one.
                        if (string.IsNullOrWhiteSpace(projectName))
                            projectName = "—";

                        var row = new OpenDocumentViewModel
                        {
                            RowNumber = rowNum++,
                            DocumentTitle = docTitle,
                            DocumentPath = string.IsNullOrEmpty(docPath) ? "Not saved" : docPath,
                            ProjectName = projectName,
                            ModelGuid = modelGuidForRow,
                            OpenedAt = DateTime.Now, // Revit doesn't provide opened time
                            OpenedAtLocal = "-",
                            OpenDurationDisplay = "-",
                            IsWorkshared = isWorkshared,
                            IsFamily = isFamily,
                            IsCloudModel = isCloud,
                            IsWorksharedDisplay = isWorkshared ? "Yes" : "No",
                            IsFamilyDisplay = isFamily ? "Yes" : "No",
                            IsCloudModelDisplay = isCloud ? "Yes" : "No"
                        };
                        _allDocuments.Add(row);

                        // Fire-and-forget: ALWAYS hit the backend API, even when the cache
                        // already produced a value. Without this, a stale registered_models
                        // row (project renamed on the web after the cache was populated)
                        // keeps showing the OLD name forever — the visible symptom that
                        // triggered this change: the Revit dialog showed "NEOM COMMUNITY 1
                        // HIGH DENSITY EXPANSION -PACKAGE -2- OFFICE" while the web had been
                        // updated to "Default Project". The patcher's setter is a no-op when
                        // the value is unchanged, so the extra call is free when cache is
                        // already fresh; the only cost is one HTTP round-trip per dialog open.
                        FetchProjectNameFromApiAndPatch(row);
                    }
                }

                // If no documents from Revit, try database fallback
                if (!_allDocuments.Any())
                {
                    var documents = await _sessionRepository.GetActiveDocumentsAsync(_currentSessionId);
                    if (documents != null && documents.Any())
                    {
                        int rowNum = 1;
                        _allDocuments = documents.Select(d => new OpenDocumentViewModel
                        {
                            RowNumber = rowNum++,
                            DocumentTitle = d.DocumentTitle ?? System.IO.Path.GetFileNameWithoutExtension(d.DocumentPath) ?? "-",
                            DocumentPath = d.DocumentPath ?? "-",
                            // Prefer the cloud project name (the ZeManage backend value, what the
                            // web dashboard shows) over the local Revit project_name field which
                            // commonly holds dates or numeric project codes. Also reject the doc
                            // name itself — upstream sometimes copies modelName into project_name,
                            // and the Project column shouldn't just echo the Document Name cell.
                            // Same em-dash fallback as the live Revit row builder above —
                            // user policy: show only the web/API project name; "—" when none.
                            ProjectName = string.IsNullOrWhiteSpace(PickProjectNameForRow(d.CloudProjectName, d.ProjectName, d.DocumentTitle))
                                            ? "—"
                                            : PickProjectNameForRow(d.CloudProjectName, d.ProjectName, d.DocumentTitle),
                            OpenedAt = d.OpenedAt,
                            OpenedAtLocal = FormatLocalDateTime(d.OpenedAt),
                            OpenDurationDisplay = FormatDuration(CalculateDuration(d.OpenedAt)),
                            IsWorkshared = d.IsWorkshared,
                            IsFamily = d.IsFamily,
                            IsCloudModel = d.DocumentPath?.StartsWith("BIM 360://") == true ||
                                           d.DocumentPath?.StartsWith("ACC://") == true ||
                                           d.DocumentPath?.Contains("cloud") == true,
                            IsWorksharedDisplay = d.IsWorkshared ? "Yes" : "No",
                            IsFamilyDisplay = d.IsFamily ? "Yes" : "No",
                            IsCloudModelDisplay = (d.DocumentPath?.StartsWith("BIM 360://") == true ||
                                                  d.DocumentPath?.StartsWith("ACC://") == true ||
                                                  d.DocumentPath?.Contains("cloud") == true) ? "Yes" : "No"
                        }).ToList();
                    }
                }

                if (!_allDocuments.Any())
                {
                    GridOpenDocuments.Visibility = System.Windows.Visibility.Collapsed;
                    EmptyDocuments.Visibility = System.Windows.Visibility.Visible;
                    return;
                }

                ApplyDocumentFilters();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load open documents: {ex.Message}", ex);
                GridOpenDocuments.Visibility = System.Windows.Visibility.Collapsed;
                EmptyDocuments.Visibility = System.Windows.Visibility.Visible;
            }
        }

        /// <summary>
        /// Looks up the project name from the local <c>registered_models</c> table —
        /// this is the value synced from the ZeManage backend (e.g. "Default Project")
        /// and matches what the web dashboard shows. Falls back to <c>document_sessions.cloud_project_name</c>
        /// for the current session if the model isn't registered. Returns null when no
        /// backend-sourced name is available, so the caller can fall through to the
        /// local Revit ProjectInformation chain.
        /// </summary>
        /// <summary>
        /// Returns true when <paramref name="candidate"/> is effectively the same as the
        /// document name — same text after trimming and lowercasing, OR the candidate is
        /// the document name plus a common Revit suffix (e.g. "_central", "_central_user",
        /// "_detached"). Used to reject project-name candidates that are really just the
        /// model file name leaking through.
        /// </summary>
        private static bool IsSameAsDocumentName(string? candidate, string? docTitle)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(docTitle)) return false;
            var c = candidate!.Trim();
            var d = docTitle!.Trim();
            if (string.Equals(c, d, StringComparison.OrdinalIgnoreCase)) return true;
            // Strip ".rvt"/".rfa" extension from candidate if present and compare again.
            string strip(string s)
            {
                if (s.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)
                    || s.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                    return s.Substring(0, s.Length - 4);
                return s;
            }
            return string.Equals(strip(c), strip(d), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Chooses the best project-name value from the document_sessions row, preferring
        /// CloudProjectName, then ProjectName, then empty. Skips any candidate that is
        /// effectively the document title (the row's modelName leaking into a project field).
        /// </summary>
        private static string PickProjectNameForRow(string? cloudProjectName, string? projectName, string? docTitle)
        {
            if (!string.IsNullOrWhiteSpace(cloudProjectName) && !IsSameAsDocumentName(cloudProjectName, docTitle))
                return cloudProjectName!;
            if (!string.IsNullOrWhiteSpace(projectName) && !IsSameAsDocumentName(projectName, docTitle))
                return projectName!;
            return string.Empty;
        }

        private async Task<string?> ResolveProjectNameFromBackendAsync(Autodesk.Revit.DB.Document doc, string? docTitle = null)
        {
            try
            {
                if (doc == null || doc.IsFamilyDocument) return null;

                // Resolve the model GUID — this is what registered_models is keyed by.
                var modelGuid = ModelGuidHelper.GetModelGuid(doc, _logger);
                if (string.IsNullOrWhiteSpace(modelGuid)) return null;

                // 1. registered_models — the authoritative API-synced project name.
                if (_registeredModels != null)
                {
                    try
                    {
                        var reg = await _registeredModels.GetModelAsync(modelGuid);
                        if (LooksLikeRealProjectName(reg?.ProjectName)
                            && !IsSameAsDocumentName(reg?.ProjectName, docTitle))
                            return reg!.ProjectName;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug($"ResolveProjectNameFromBackendAsync: registered_models lookup failed for {modelGuid}: {ex.Message}");
                    }
                }

                // 2. document_sessions.cloud_project_name — populated when the API replays a
                //    document-open event back; reliable for sessions the backend has seen.
                //
                // CRITICAL: the candidate row MUST belong to the document we're resolving for.
                // Previously this used a plain FirstOrDefault that returned ANY row in the
                // session that happened to have a cloud_project_name — so opening a brand-new
                // unsaved "Project1" alongside a real model would copy the real model's
                // project name into Project1's row (the visible bug). Filter strictly by
                // modelGuid first; only fall back to docTitle when modelGuid isn't available
                // (unsaved / non-workshared docs that don't carry a model GUID at all). The
                // docTitle compare is case-insensitive ordinal — see SameDoc helper.
                if (!string.IsNullOrWhiteSpace(_currentSessionId))
                {
                    try
                    {
                        var docs = await _sessionRepository.GetActiveDocumentsAsync(_currentSessionId);
                        if (docs != null && docs.Count > 0)
                        {
                            // True when this DB row describes the same document we're resolving
                            // for. DocumentPath is the strong identity (full central / local
                            // path uniquely identifies a saved model). DocumentTitle is the
                            // fallback for unsaved / new projects that have no path yet —
                            // titles can collide in pathological cases but that's strictly
                            // better than the previous behaviour of accepting ANY row in the
                            // session and copying its project name across to unrelated docs.
                            string livePath = doc.PathName ?? "";
                            bool SameDoc(DocumentSession d)
                            {
                                if (!string.IsNullOrWhiteSpace(livePath)
                                    && !string.IsNullOrWhiteSpace(d.DocumentPath)
                                    && string.Equals(d.DocumentPath, livePath, StringComparison.OrdinalIgnoreCase))
                                    return true;
                                if (!string.IsNullOrWhiteSpace(docTitle)
                                    && !string.IsNullOrWhiteSpace(d.DocumentTitle)
                                    && string.Equals(d.DocumentTitle, docTitle, StringComparison.OrdinalIgnoreCase))
                                    return true;
                                return false;
                            }

                            var cloudMatch = docs.FirstOrDefault(d =>
                                SameDoc(d)
                                && LooksLikeRealProjectName(d.CloudProjectName)
                                && !IsSameAsDocumentName(d.CloudProjectName, docTitle));
                            if (cloudMatch != null) return cloudMatch.CloudProjectName;
                            // 2b. Sometimes only the (non-cloud) project_name column is filled.
                            var projMatch = docs.FirstOrDefault(d =>
                                SameDoc(d)
                                && LooksLikeRealProjectName(d.ProjectName)
                                && !IsSameAsDocumentName(d.ProjectName, docTitle));
                            if (projMatch != null) return projMatch.ProjectName;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug($"ResolveProjectNameFromBackendAsync: document_sessions lookup failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"ResolveProjectNameFromBackendAsync failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Background fetch: hits GET /api/v1/Revit/models/{modelGuid} to get the EXACT
        /// project name shown on the ZeManage dashboard, then patches the row's
        /// <see cref="OpenDocumentViewModel.ProjectName"/> in place when the response
        /// arrives. The row is observable so the DataGrid re-renders the Project cell
        /// automatically — no need to rebuild ItemsSource.
        ///
        /// Also persists the resolved name into the local <c>registered_models</c> table
        /// so subsequent dialog opens (and other features that read that table) get the
        /// name instantly without another network round-trip.
        ///
        /// Fire-and-forget on purpose: the dialog must not block on network. If the API
        /// is unreachable or returns nothing, the cell stays at whatever the local
        /// resolver produced (which after the LooksLikeRealProjectName filter is either
        /// a real name or empty — never "4800000856").
        /// </summary>
        private void FetchProjectNameFromApiAndPatch(OpenDocumentViewModel row)
        {
            if (row == null) return;
            if (_modelSyncService == null) return;
            if (string.IsNullOrWhiteSpace(row.ModelGuid)) return;
            var modelGuid = row.ModelGuid!;
            var docTitle = row.DocumentTitle;

            _ = Task.Run(async () =>
            {
                try
                {
                    // PRIMARY PATH (verified against Swagger 29 May 2026):
                    // GET /api/v1/Revit/projects/by-model/{modelGuid} returns
                    // { data: { projectId, projectName, projectNumber } } — this is the
                    // exact endpoint the web dashboard uses to resolve a model's project
                    // folder. Try it first because it's keyed by modelGuid (no model
                    // record fetch needed) and returns the current name directly.
                    string? fetchedPrimary = await _modelSyncService.FetchProjectNameByModelGuidAsync(modelGuid).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(fetchedPrimary))
                    {
                        var doctitleLocal = docTitle;
                        bool primaryUsable = LooksLikeRealProjectName(fetchedPrimary)
                                             && !IsSameAsDocumentName(fetchedPrimary, doctitleLocal);
                        if (primaryUsable)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (!string.Equals(row.ProjectName, fetchedPrimary, StringComparison.Ordinal))
                                {
                                    row.ProjectName = fetchedPrimary!;
                                    _logger?.LogInfo($"Patched project name for {modelGuid} from /projects/by-model → '{fetchedPrimary}'");
                                }
                            });

                            // Persist to local registered_models cache so other consumers
                            // (Model Activities, etc.) get the name without a round-trip.
                            if (_registeredModels != null)
                            {
                                try
                                {
                                    var existing = await _registeredModels.GetModelAsync(modelGuid).ConfigureAwait(false);
                                    if (existing != null && !string.Equals(existing.ProjectName, fetchedPrimary, StringComparison.Ordinal))
                                    {
                                        existing.ProjectName = fetchedPrimary;
                                        await _registeredModels.RegisterModelAsync(existing).ConfigureAwait(false);
                                    }
                                }
                                catch (Exception persistEx)
                                {
                                    _logger?.LogDebug($"FetchProjectNameFromApiAndPatch: persist (primary) failed for {modelGuid}: {persistEx.Message}");
                                }
                            }
                            return;
                        }
                        _logger?.LogInfo($"[PNRv3] /projects/by-model returned '{fetchedPrimary}' but rejected (matches doc name / not real) — falling back");
                    }

                    // FALLBACK PATH (older builds / different deployment):
                    var apiModel = await _modelSyncService.FetchModelByGuidAsync(modelGuid).ConfigureAwait(false);

                    // PROJECT-ENTITY LOOKUP — STRICT, no fallback.
                    //
                    // The model record's localProjectName field is a frozen snapshot of
                    // Revit ProjectInformation.Name from registration time. That field
                    // produced the "NEOM COMMUNITY..." stale display the user kept seeing
                    // even after the actual project was renamed to "Default Project" on
                    // the web. The project entity (referenced by model.projectId) has the
                    // CURRENT name. We use ONLY that; if the project-entity lookup fails
                    // or the project endpoint isn't reachable, we leave fetched=null so
                    // the cell shows "—" instead of the stale wrong name.
                    string? fetched = null;
                    var projectIdForLookup = apiModel?.ZemanageProjectId ?? apiModel?.ProjectIdAlternate;
                    if (!string.IsNullOrWhiteSpace(projectIdForLookup))
                    {
                        _logger?.LogInfo($"[PNRv3] Resolving project name via project-entity for projectId={projectIdForLookup}");
                        fetched = await _modelSyncService.FetchProjectNameByIdAsync(projectIdForLookup!).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(fetched))
                            _logger?.LogInfo($"[PNRv3] Project-entity returned '{fetched}'");
                        else
                            _logger?.LogInfo($"[PNRv3] Project-entity returned no name for {projectIdForLookup}");
                    }
                    else
                    {
                        _logger?.LogInfo("[PNRv3] No projectId on model");
                    }

                    // Fallback: if the project-entity lookup didn't yield a name (either
                    // because the model has no projectId, or because none of the candidate
                    // /projects/{id} endpoints worked), try apiModel.ProjectName. This is
                    // the field the backend updates when a model is assigned to a project
                    // folder on the web — distinct from LocalProjectName which is the
                    // Revit ProjectInformation snapshot captured at registration time.
                    //
                    // Guard against the prior NEOM regression: only accept ProjectName
                    // when it differs from LocalProjectName. If they're equal, the value
                    // is the stale Revit snapshot copied into projectName upstream — not
                    // a real web-assigned folder name.
                    if (string.IsNullOrWhiteSpace(fetched) && apiModel != null)
                    {
                        var localSnapshot = apiModel.LocalProjectName;
                        // Try the web-side fields in priority order: a folder-name field
                        // (most specific) → projectName (commonly populated by the web
                        // when a model is assigned to a project folder) → folderName.
                        var candidates = new[]
                        {
                            ("projectFolderName", apiModel.ProjectFolderName),
                            ("folderName", apiModel.FolderName),
                            ("projectName", apiModel.ProjectName),
                        };
                        foreach (var (sourceField, candidate) in candidates)
                        {
                            if (string.IsNullOrWhiteSpace(candidate)) continue;
                            if (string.Equals(candidate?.Trim(), localSnapshot?.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                _logger?.LogInfo($"[PNRv3] model.{sourceField}='{candidate}' equals localProjectName — treated as Revit snapshot, NOT used");
                                continue;
                            }
                            fetched = candidate;
                            _logger?.LogInfo($"[PNRv3] Using model.{sourceField} fallback → '{fetched}' (localProjectName='{localSnapshot}' — differs, so this is a web-assigned folder name)");
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(fetched))
                        _logger?.LogInfo("[PNRv3] No usable project name from any API path — cell will show '—'");

                    // Decide the cell's final value:
                    //   - usable name → use it verbatim (overwrites stale cache)
                    //   - nothing usable → show "—" so the stale cache value never leaks
                    bool fetchedUsable = !string.IsNullOrWhiteSpace(fetched)
                                         && LooksLikeRealProjectName(fetched)
                                         && !IsSameAsDocumentName(fetched, docTitle);
                    string finalValue = fetchedUsable ? fetched! : "—";

                    // Patch on the UI thread so the binding fires
                    Dispatcher.Invoke(() =>
                    {
                        if (string.Equals(row.ProjectName, finalValue, StringComparison.Ordinal)) return;
                        row.ProjectName = finalValue;
                        _logger?.LogInfo($"Patched project name for {modelGuid} from API → '{finalValue}'"
                            + (fetchedUsable ? "" : " (API returned no usable name — showing em-dash)"));
                    });

                    // Skip the registered_models persist step when API returned nothing —
                    // we don't want to write "—" into the cache and we don't want to
                    // preserve a stale value either. Leave the cache untouched; next
                    // dialog open will retry the API.
                    if (!fetchedUsable) return;

                    // Persist to registered_models so the next dialog open / other
                    // consumers get this name immediately from the local cache.
                    if (_registeredModels != null)
                    {
                        try
                        {
                            var existing = await _registeredModels.GetModelAsync(modelGuid).ConfigureAwait(false);
                            if (existing != null && !string.Equals(existing.ProjectName, fetched, StringComparison.Ordinal))
                            {
                                existing.ProjectName = fetched;
                                await _registeredModels.RegisterModelAsync(existing).ConfigureAwait(false);
                            }
                        }
                        catch (Exception persistEx)
                        {
                            _logger?.LogDebug($"FetchProjectNameFromApiAndPatch: persist to registered_models failed for {modelGuid}: {persistEx.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"FetchProjectNameFromApiAndPatch failed for {modelGuid}: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Folder names that almost certainly aren't real project names — they're generic
        /// holders for Revit files (the user's "D:\Revit\" working folder, etc.). When the
        /// directory-name fallback in <see cref="ResolveProjectName"/> hits one of these,
        /// we skip it and let the Project column fall through to empty rather than show
        /// a confusing "Revit" / "Models" / "Projects" placeholder. Comparison is case-
        /// insensitive and ignores surrounding whitespace.
        /// </summary>
        private static readonly HashSet<string> GenericFolderBlocklist =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Revit", "Models", "Model", "Projects", "Project", "Files", "Documents",
                "Drawings", "Drawing", "BIM", "RVT", "Local", "Central", "Backup", "Backups",
                "Temp", "Working", "Work", "Active", "Archive", "Archived", "New folder",
                "Downloads", "Desktop", "Shared", "Public", "Users",
            };

        /// <summary>
        /// Filters out values that look like SAP / PO project codes ("4800000856"), dates
        /// ("2026-05-12"), or other obviously-non-name placeholders. A real project name
        /// has at least one letter and isn't a pure ISO date — anything else is almost
        /// always a numeric code that doesn't belong in the Project column.
        /// Also rejects generic folder names like "Revit" / "Models" / "Projects" that
        /// commonly appear as a parent directory and aren't real project names.
        /// </summary>
        /// <summary>
        /// DEPRECATED 28 May 2026 — no longer wired. User policy: the Project cell
        /// shows only the web/API project name; do NOT show a stripped-model-name
        /// fallback. Kept here as dead code in case the policy flips back.
        /// </summary>
        private static string StripWellKnownModelSuffixes(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return modelName ?? string.Empty;

            string current = modelName.Trim();
            if (current.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                current = current.Substring(0, current.Length - 4);

            var rolePatterns = new[]
            {
                "_central", "_detached", "_local", "_testing", "_test", "_backup",
                "_temp", "_working", "_archive", "_draft", "_review", "_qa",
            };

            bool trimmed = true;
            int safety = 8;
            while (trimmed && safety-- > 0)
            {
                trimmed = false;
                foreach (var suffix in rolePatterns)
                {
                    int idx = current.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
                    if (idx > 0 && idx + suffix.Length <= current.Length)
                    {
                        current = current.Substring(0, idx);
                        trimmed = true;
                        break;
                    }
                }
                // After stripping the role, the segment that follows is usually a
                // user name segment ("_priya", "_Info 1"). Drop it too — but only if
                // the tail is letters/space (no digits/dashes) so we don't accidentally
                // chop "MECH-MOD-2024" → "MECH-MOD" by treating "2024" as a username.
                if (!trimmed && current.Contains("_"))
                {
                    int lastUnderscore = current.LastIndexOf('_');
                    if (lastUnderscore > 0)
                    {
                        var tail = current.Substring(lastUnderscore + 1);
                        bool looksLikeUserName = tail.Length > 0
                            && tail.All(ch => char.IsLetter(ch) || ch == ' ');
                        if (looksLikeUserName)
                        {
                            current = current.Substring(0, lastUnderscore);
                            trimmed = true;
                        }
                    }
                }
            }

            return current;
        }

        private static bool LooksLikeRealProjectName(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            var trimmed = candidate!.Trim();
            // ISO date "YYYY-MM-DD" or "YYYY/MM/DD" — common when teams name folders by date
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d{4}[-/]\d{1,2}[-/]\d{1,2}$")) return false;
            // Pure numeric or near-pure numeric (SAP/PO codes like "4800000856", "PO-12345")
            int letterCount = 0;
            foreach (var ch in trimmed) if (char.IsLetter(ch)) letterCount++;
            if (letterCount == 0) return false;
            // Generic folder names — "Revit" was the trigger for this fix: a user
            // working out of D:\Revit\MECH-MOD-2024_Central_priya.rvt was seeing
            // "Revit" in the Project column because the directory-name fallback
            // (Path.GetFileName(directory)) returned the literal working folder.
            if (GenericFolderBlocklist.Contains(trimmed)) return false;
            return true;
        }

        /// <summary>
        /// Resolves the Project column value with a fallback chain so the cell is rarely
        /// empty. <see cref="Autodesk.Revit.DB.ProjectInfo.Name"/> is the Revit "Project
        /// Name" field on the Project Information dialog — blank by default because most
        /// users never fill it in. We therefore fall through to other ProjectInfo fields
        /// (Number, BuildingName, ClientName) and finally to the parent folder name of
        /// the document path, which is almost always populated for saved workshared
        /// models. Each step is in its own try/catch because individual ProjectInfo
        /// property getters can throw on documents in unusual states (templates, certain
        /// detached states, in-flight transactions).
        /// </summary>
        private static string ResolveProjectName(Autodesk.Revit.DB.Document doc, bool isFamily, string? docPath, string? docTitle = null)
        {
            if (isFamily) return string.Empty;

            // Local helper: a candidate is acceptable iff it looks like a real project name
            // AND it's not a clone of the document name (a common upstream data-quality issue
            // where the model file name gets typed into ProjectInformation.Name).
            bool Accept(string? c) => LooksLikeRealProjectName(c) && !IsSameAsDocumentName(c, docTitle);

            try
            {
                var info = doc.ProjectInformation;
                if (info != null)
                {
                    string? candidate = TryGetProjectInfoString(() => info.Name);
                    if (Accept(candidate)) return candidate!;

                    candidate = TryGetProjectInfoString(() => info.Number);
                    if (Accept(candidate)) return candidate!;

                    candidate = TryGetProjectInfoString(() => info.BuildingName);
                    if (Accept(candidate)) return candidate!;

                    candidate = TryGetProjectInfoString(() => info.ClientName);
                    if (Accept(candidate)) return candidate!;
                }
            }
            catch { /* ProjectInfo not accessible — fall through to path fallback */ }


            try
            {
                if (!string.IsNullOrWhiteSpace(docPath))
                {
                    var dir = System.IO.Path.GetDirectoryName(docPath);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        var leaf = System.IO.Path.GetFileName(dir);
                        if (Accept(leaf)) return leaf!;
                    }
                }
            }
            catch { }

            return string.Empty;
        }

        private static string? TryGetProjectInfoString(Func<string?> getter)
        {
            try { return getter(); }
            catch { return null; }
        }

        private void ApplyDocumentFilters()
        {
            var filtered = _allDocuments.AsEnumerable();

            // Toggle button combination filters
            bool filterFamily = FilterFamily?.IsChecked == true;
            bool filterWorkshared = FilterWorkshared?.IsChecked == true;
            bool filterCloud = FilterCloud?.IsChecked == true;

            // Apply toggle filters - when multiple are selected, document must match ALL selected filters
            if (filterFamily || filterWorkshared || filterCloud)
            {
                filtered = filtered.Where(d =>
                    (!filterFamily || d.IsFamily) &&
                    (!filterWorkshared || d.IsWorkshared) &&
                    (!filterCloud || d.IsCloudModel));
            }

            var result = filtered.ToList();

            // Update row numbers after filtering
            int rowNum = 1;
            foreach (var doc in result)
            {
                doc.RowNumber = rowNum++;
            }

            if (result.Any())
            {
                GridOpenDocuments.ItemsSource = result;
                GridOpenDocuments.Visibility = System.Windows.Visibility.Visible;
                EmptyDocuments.Visibility = System.Windows.Visibility.Collapsed;
            }
            else
            {
                GridOpenDocuments.ItemsSource = null;
                GridOpenDocuments.Visibility = System.Windows.Visibility.Collapsed;
                EmptyDocuments.Visibility = System.Windows.Visibility.Visible;
            }
        }

        private void FilterToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_allDocuments != null && _allDocuments.Any())
            {
                ApplyDocumentFilters();
            }
        }


        private string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalSeconds < 0)
                return "0s";
            if (duration.TotalDays >= 1)
                return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
            if (duration.TotalHours >= 1)
                return $"{(int)duration.TotalHours}h {duration.Minutes}m";
            if (duration.TotalMinutes >= 1)
                return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
            return $"{duration.Seconds}s";
        }

        #region Tab Navigation

        private void TabSessionInfo_Click(object sender, RoutedEventArgs e)
        {
            SetActiveTab(0);
        }

        private void TabOpenDocuments_Click(object sender, RoutedEventArgs e)
        {
            SetActiveTab(1);
        }

        private void SetActiveTab(int tabIndex)
        {
            // Reset all tab buttons
            TabSessionInfo.Style = (Style)FindResource("TabButton");
            TabOpenDocuments.Style = (Style)FindResource("TabButton");

            // Hide all panels
            PanelSessionInfo.Visibility = System.Windows.Visibility.Collapsed;
            PanelOpenDocuments.Visibility = System.Windows.Visibility.Collapsed;

            // Activate selected tab
            switch (tabIndex)
            {
                case 0:
                    TabSessionInfo.Style = (Style)FindResource("ActiveTabButton");
                    PanelSessionInfo.Visibility = System.Windows.Visibility.Visible;
                    break;
                case 1:
                    TabOpenDocuments.Style = (Style)FindResource("ActiveTabButton");
                    PanelOpenDocuments.Visibility = System.Windows.Visibility.Visible;
                    break;
            }
        }

        #endregion

        #region Button Handlers

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadDataAsync();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        }

        #endregion
    }

    #region View Models

    public class OpenDocumentViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public int RowNumber { get; set; }
        public string DocumentTitle { get; set; } = string.Empty;
        public string DocumentPath { get; set; } = string.Empty;

        // Observable so the row can be patched in-place after a background API fetch
        // resolves the real "web" project name (e.g. "Default Project"). The DataGrid
        // re-renders the Project cell as soon as the setter fires PropertyChanged —
        // no need to rebuild ItemsSource.
        private string _projectName = string.Empty;
        public string ProjectName
        {
            get => _projectName;
            set
            {
                if (_projectName == value) return;
                _projectName = value ?? string.Empty;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ProjectName)));
            }
        }

        // Carried alongside the row so the background patcher can hit the API by model_guid
        // and persist the resolved name back to registered_models without a second lookup.
        public string? ModelGuid { get; set; }

        public DateTime OpenedAt { get; set; }
        public string OpenedAtLocal { get; set; } = string.Empty;
        public string OpenDurationDisplay { get; set; } = string.Empty;
        public bool IsWorkshared { get; set; }
        public bool IsFamily { get; set; }
        public bool IsCloudModel { get; set; }
        public string IsWorksharedDisplay { get; set; } = string.Empty;
        public string IsFamilyDisplay { get; set; } = string.Empty;
        public string IsCloudModelDisplay { get; set; } = string.Empty;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }


    #endregion
}
