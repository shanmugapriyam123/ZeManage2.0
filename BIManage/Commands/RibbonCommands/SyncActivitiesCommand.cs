using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Core;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Revit.Helpers;
using BIManageRevit.BIManage.Views.ModelActivities;

namespace BIManageRevit.Commands.RibbonCommands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class SyncActivitiesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            if (LicenseGuard.IsBlockedByLicense(commandData)) return Result.Succeeded;

            ILogger? logger = null;
            try
            {
                var uiDoc = commandData.Application.ActiveUIDocument;
                var doc = uiDoc?.Document;

                if (doc == null)
                {
                    TaskDialog.Show("Model Activities", "No active document found.\n\nPlease open a model first.");
                    return Result.Cancelled;
                }

                // Get services from Application instance
                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;

                SessionRepository? sessionRepo = null;
                RegisteredModelsRepository? modelsRepo = null;
                ISignalREventBus? eventBus = null;
                ISignalRConnectionManager? connectionManager = null;

                if (services != null)
                {
                    logger = services.GetService<ILogger>();
                    sessionRepo = services.GetService<SessionRepository>();
                    modelsRepo = services.GetService<RegisteredModelsRepository>();
                    eventBus = services.GetService<ISignalREventBus>();
                    connectionManager = services.GetService<ISignalRConnectionManager>();
                }

                // Get current model info
                var modelGuid = ModelGuidHelper.GetModelGuid(doc, logger);
                if (string.IsNullOrEmpty(modelGuid))
                {
                    TaskDialog.Show("Model Activities", "Unable to determine model identifier.\n\nThis model may not have a unique identifier.");
                    return Result.Cancelled;
                }

                var centralModelPath = GetCentralModelPath(doc);
                var modelName = GetCentralModelName(centralModelPath) ?? doc.Title;
                var modelType = GetModelType(doc);
                var currentUsername = commandData.Application.Application.Username;

                // Project-name resolution — strict web-only policy.
                //
                // ORDER CHANGED 28 May 2026: API is now the PRIMARY source, cache is only
                // consulted when the API call fails (offline, server error). Previously the
                // cache was tried first and the API was a fallback — that meant a project
                // renamed on the web stayed showing its OLD name in Revit until the cache
                // was somehow refreshed (which only happened on re-registration). The user
                // saw "NEOM COMMUNITY 1 HIGH DENSITY EXPANSION -PACKAGE -2- OFFICE" in the
                // dialog while the web had been updated to "Default Project".
                //
                // The synchronous Task.Run().GetAwaiter().GetResult() blocks the click for
                // one HTTP round-trip on dialog open. That's acceptable because the user
                // just clicked the ribbon button and a small delay (typically < 300 ms on
                // the LAN, < 1 s on WAN) is invisible. Failures fall back to cache so the
                // dialog still opens offline.
                // [PNRv4] Strict project-entity-only resolution. The ONLY value allowed
                // here is the current name from the project entity (referenced by the
                // model's projectId). Every other source — registered_models cache,
                // model.localProjectName field — has been removed because they all carry
                // a registration-time snapshot of Revit's ProjectInformation.Name and
                // produce the stale "NEOM COMMUNITY 1 HIGH DENSITY EXPANSION ..." display
                // even after the project is renamed on the web.
                //
                // Result: when the project-entity lookup succeeds → the dialog shows the
                // current web name (e.g. "Default Project"). When it fails for ANY reason
                // (project endpoint URL not found, network down, projectId missing) →
                // projectName stays null and ModelActivitiesDialog renders the "—"
                // placeholder. The stale NEOM name CAN NOT appear under any circumstance.
                string? projectName = null;
                string? projectId = null;
                bool apiCallSucceeded = false;

                try
                {
                    var modelSync = services?.GetService<global::BIManage.Infrastructure.Api.ModelSyncService>();
                    if (modelSync == null)
                    {
                        logger?.LogInfo("[PNRv4] ModelSyncService is null — projectName stays null → badge shows '—'");
                    }
                    else
                    {
                        // PRIMARY PATH: GET /api/v1/Revit/projects/by-model/{modelGuid}
                        // (verified against Swagger 29 May 2026). Returns the current
                        // web-dashboard project name directly — no need to first fetch
                        // the model record and then resolve its projectId.
                        logger?.LogInfo($"[PNRv4] Calling /projects/by-model for {modelGuid}");
                        var primaryName = Task.Run(() => modelSync.FetchProjectNameByModelGuidAsync(modelGuid)).GetAwaiter().GetResult();
                        if (!string.IsNullOrWhiteSpace(primaryName))
                        {
                            projectName = primaryName;
                            apiCallSucceeded = true;
                            logger?.LogInfo($"[PNRv4] Resolved project name from /projects/by-model: '{projectName}'");
                        }
                        else
                        {
                            // FALLBACK PATH: fetch the model record, read projectId, then
                            // resolve via the project-entity endpoints. Kept for backends
                            // that don't expose /projects/by-model.
                            logger?.LogInfo($"[PNRv4] /projects/by-model returned nothing — falling back to model→projectId→project-entity chain");
                            var apiModel = Task.Run(() => modelSync.FetchModelByGuidAsync(modelGuid)).GetAwaiter().GetResult();
                            apiCallSucceeded = apiModel != null;
                            if (apiModel == null)
                            {
                                logger?.LogInfo($"[PNRv4] Live API returned NULL for model {modelGuid} — projectName stays null → badge shows '—'");
                            }
                            else
                            {
                                projectId = apiModel.ZemanageProjectId ?? apiModel.ProjectIdAlternate;
                                logger?.LogInfo($"[PNRv4] Live API returned: projectId='{projectId ?? "(null)"}', model.localProjectName='{apiModel.LocalProjectName ?? "(null)"}' (snapshot — NEVER used)");

                                if (!string.IsNullOrWhiteSpace(projectId))
                                {
                                    var resolvedName = Task.Run(() => modelSync.FetchProjectNameByIdAsync(projectId!)).GetAwaiter().GetResult();
                                    if (!string.IsNullOrWhiteSpace(resolvedName))
                                    {
                                        projectName = resolvedName;
                                        logger?.LogInfo($"[PNRv4] Resolved project name from project-entity: '{projectName}'");
                                    }
                                    else
                                    {
                                        logger?.LogInfo("[PNRv4] Project-entity lookup returned no name — projectName stays null → badge shows '—'");
                                    }
                                }
                                else
                                {
                                    logger?.LogInfo("[PNRv4] No projectId on model — projectName stays null → badge shows '—'");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning($"[PNRv4] Project resolution THREW: {ex.GetType().Name}: {ex.Message} — projectName stays null → badge shows '—'");
                }

                // Pull projectId from the cache ONLY for downstream consumers (ChatScope,
                // ProjectAdmins lookup, etc.) — never let it set projectName. The cache's
                // ProjectName field holds the same stale registration snapshot.
                if (string.IsNullOrWhiteSpace(projectId) && modelsRepo != null)
                {
                    try
                    {
                        var registeredModel = Task.Run(() => modelsRepo.GetModelAsync(modelGuid)).GetAwaiter().GetResult();
                        projectId = registeredModel?.CloudProjectId ?? registeredModel?.ZemanageProjectId;
                        logger?.LogInfo($"[PNRv4] Pulled projectId='{projectId ?? "(null)"}' from cache (name field deliberately ignored)");
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning($"[PNRv4] Cache projectId lookup failed: {ex.Message}");
                    }
                }

                logger?.LogInfo($"[PNRv4] FINAL ProjectName for dialog header = '{projectName ?? "(null → '—')"}' (apiSucceeded={apiCallSucceeded})");

                // No Revit-side fallback — strictly web/API. If both API and cache returned
                // nothing (e.g. project genuinely not registered), the badge shows "—".

                logger?.LogInfo($"SyncActivitiesCommand: Opening Model Activities for {modelName} ({modelGuid}), Project: {projectName ?? "(none)"}");

                var unmonitoredService = services.GetService<global::BIManage.Core.Detection.UnmonitoredUserDetectionService>();
                var presenceCache = services.GetService<global::BIManage.Infrastructure.SignalR.PresenceCache>();
                var modelAdminsService = services.GetService<global::BIManage.Infrastructure.Api.ModelAdminsSyncService>();

                var dialog = new ModelActivitiesDialog(
                    modelGuid: modelGuid,
                    modelName: modelName,
                    centralModelPath: centralModelPath,
                    modelType: modelType,
                    currentUsername: currentUsername,
                    projectName: projectName,
                    projectId: projectId,
                    sessionRepository: sessionRepo,
                    logger: logger,
                    eventBus: eventBus,
                    connectionManager: connectionManager,
                    unmonitoredDetectionService: unmonitoredService,
                    presenceCache: presenceCache,
                    modelAdminsService: modelAdminsService);

                new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = commandData.Application.MainWindowHandle };
                dialog.ShowDialog();

                logger?.LogInfo("Model Activities dialog closed");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                logger?.LogError($"SyncActivitiesCommand failed: {ex.Message}", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static string? GetCentralModelPath(Document doc)
        {
            try
            {
                if (doc.IsWorkshared)
                {
                    var centralPath = doc.GetWorksharingCentralModelPath();
                    if (centralPath != null && !centralPath.Empty)
                    {
                        return ModelPathUtils.ConvertModelPathToUserVisiblePath(centralPath);
                    }
                }
                return doc.PathName;
            }
            catch
            {
                return doc.PathName;
            }
        }

        private static string? GetCentralModelName(string? centralModelPath)
        {
            if (string.IsNullOrEmpty(centralModelPath))
                return null;

            try
            {
                return System.IO.Path.GetFileNameWithoutExtension(centralModelPath);
            }
            catch
            {
                return null;
            }
        }

        private static string GetModelType(Document doc)
        {
            if (doc.IsFamilyDocument) return "Family";
            if (doc.IsModelInCloud) return "Cloud";
            if (doc.IsWorkshared) return "Workshared";
            return "Local";
        }

        /// <summary>
        /// DEPRECATED 28 May 2026 — no longer wired. User requested that the Project
        /// badge show ONLY the web/API project name (registered_models / live API).
        /// When the backend returns nothing, the badge shows "No Project" rather than
        /// a Revit-derived placeholder. Kept here as dead code in case the policy
        /// ever flips back; remove cleanly if not needed beyond a release cycle.
        /// </summary>
        private static string? ResolveRevitProjectName(Document doc, string? modelName)
        {
            if (doc == null || doc.IsFamilyDocument) return null;

            // Accept rejects values that are clearly NOT real project names: Revit
            // placeholder strings, ISO dates, pure numeric codes. Does NOT reject the
            // model name itself any more — when the user's project literally is named
            // after the model file (common when they haven't filled in ProjectInformation),
            // showing the model name is more useful than "No Project".
            bool Accept(string? candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate)) return false;
                var t = candidate!.Trim();
                if (string.Equals(t, "Project Name", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(t, "Project Number", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(t, "Owner", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(t, "Enter address here", StringComparison.OrdinalIgnoreCase)) return false;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{4}[-/]\d{1,2}[-/]\d{1,2}$")) return false;
                bool hasLetter = false;
                foreach (var ch in t) { if (char.IsLetter(ch)) { hasLetter = true; break; } }
                if (!hasLetter) return false;
                return true;
            }

            // 1. ProjectInformation chain
            try
            {
                var info = doc.ProjectInformation;
                if (info != null)
                {
                    string? candidate;
                    try { candidate = info.Name; if (Accept(candidate)) return candidate; } catch { }
                    try { candidate = info.Number; if (Accept(candidate)) return candidate; } catch { }
                    try { candidate = info.BuildingName; if (Accept(candidate)) return candidate; } catch { }
                    try { candidate = info.ClientName; if (Accept(candidate)) return candidate; } catch { }
                }
            }
            catch { }

            // 2. Parent directory leaf
            try
            {
                var docPath = doc.PathName;
                if (!string.IsNullOrWhiteSpace(docPath))
                {
                    var dir = System.IO.Path.GetDirectoryName(docPath);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        var leaf = System.IO.Path.GetFileName(dir);
                        if (Accept(leaf)
                            && !string.Equals(leaf, "Revit", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(leaf, "Working", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(leaf, "Models", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(leaf, "Projects", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(leaf, "Files", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(leaf, "Documents", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(leaf, "Local", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(leaf, "Central", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(leaf, "Temp", StringComparison.OrdinalIgnoreCase))
                            return leaf;
                    }
                }
            }
            catch { }

            // 3. Derive project code from model name. Most workshared models follow the
            //    pattern "<ProjectCode>_<Role>[_<User>]" (e.g. "MECH-MOD-2024_Central_priya",
            //    "ELE-MOD_detached_Testing", "MECH-MOD-2024_Testing_detached_Testing").
            //    Strip the well-known role/user suffixes from the end until what remains
            //    looks like a project identifier on its own.
            if (!string.IsNullOrWhiteSpace(modelName))
            {
                var code = StripWellKnownSuffixes(modelName!.Trim());
                if (!string.IsNullOrWhiteSpace(code) && Accept(code))
                    return code;
            }

            // 4. Final fallback — model name itself. Always populates the header.
            if (!string.IsNullOrWhiteSpace(modelName))
                return modelName!.Trim();

            return null;
        }

        /// <summary>
        /// Trims trailing role / user suffixes off a model name to produce the project
        /// identifier most teams put at the front. Examples:
        ///   "MECH-MOD-2024_Testing_detached_Testing" → "MECH-MOD-2024"
        ///   "ELE-MOD_central_Info 1"                → "ELE-MOD"
        ///   "ARCH-2024_Central_priya"               → "ARCH-2024"
        /// Stops as soon as no more known-suffix segment matches, so a project legitimately
        /// named "BUILDING_A" survives untouched (neither A nor BUILDING is a role/user marker).
        /// </summary>
        private static string StripWellKnownSuffixes(string modelName)
        {
            var rolePatterns = new[]
            {
                "_central", "_detached", "_local", "_testing", "_test", "_backup",
                "_temp", "_working", "_archive", "_draft", "_review", "_qa",
            };

            string current = modelName;
            if (current.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                current = current.Substring(0, current.Length - 4);

            bool trimmed = true;
            // Cap iterations defensively — a pathological name wouldn't dance forever, but
            // a guard removes any chance of an accidental infinite loop on weird input.
            int safety = 8;
            while (trimmed && safety-- > 0)
            {
                trimmed = false;
                foreach (var suffix in rolePatterns)
                {
                    int idx = current.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
                    if (idx > 0 && idx + suffix.Length <= current.Length)
                    {
                        // Either the suffix is at the end OR followed by trailing chars
                        // (the user-name segment after _Central_<user>). Strip everything
                        // from the suffix's _ onward.
                        current = current.Substring(0, idx);
                        trimmed = true;
                        break;
                    }
                }
                // Also strip a trailing "_<user>" segment that didn't match a role keyword
                // but follows the last role we already trimmed (e.g. "MECH-MOD_Central_priya"
                // — after stripping _Central, "_priya" is whatever the user's name is).
                if (!trimmed && current.Contains("_"))
                {
                    int lastUnderscore = current.LastIndexOf('_');
                    if (lastUnderscore > 0)
                    {
                        var tail = current.Substring(lastUnderscore + 1);
                        // Only strip if the tail looks like a free-text user-name segment
                        // (letters with no project-code structure — no digits, no dashes).
                        // Keeps "MECH-MOD-2024" intact (the "2024" tail is numeric → not stripped).
                        bool looksLikeUserName = tail.Length > 0
                            && tail.All(char.IsLetter)
                            && !rolePatterns.Any(s => string.Equals("_" + tail, s, StringComparison.OrdinalIgnoreCase));
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
    }
}
