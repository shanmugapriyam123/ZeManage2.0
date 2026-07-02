using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Core.Protection.Models;
using BIManage.Core.Rules.Models;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Helpers;
using BIManage.Revit.Protection;

namespace BIManage.Revit.Commands.Bindings
{
    /// <summary>
    /// Dedicated binding for Mirror commands — handles MirrorPickAxis and MirrorDrawAxis variants.
    /// Uses BeforeExecuted only: equipment-category guard → CommandProtectionBinding check → rule evaluation.
    /// Mirror creates mirrored copies without moving originals, so no pin-blocking is applied.
    /// </summary>
    public class MirrorCommandBinding : CommandBindingBase
    {
        /// <summary>
        /// Equipment categories that may not be mirrored (Mirror Pick Axis + Mirror Draw Axis).
        /// Match is by BuiltInCategory where available, falling back to category display name.
        /// </summary>
        private static readonly HashSet<string> ProtectedEquipmentCategoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Electrical Equipment",
            "Mechanical Equipment",
            "Food Service Equipment",
            "Medical Equipment",
            "Plumbing Equipment",
            "Specialty Equipment",
            "Zone Equipment"
        };

        private static readonly HashSet<BuiltInCategory> ProtectedEquipmentBuiltInCategories = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_MechanicalEquipment,
            BuiltInCategory.OST_MedicalEquipment,
            BuiltInCategory.OST_PlumbingEquipment,
            BuiltInCategory.OST_SpecialityEquipment
            // OST_FoodServiceEquipment / OST_ZoneEquipment aren't in all Revit versions —
            // fall through to name-based match above.
        };
        private AddInCommandBinding _binding;
        private readonly IRuleCommandInterceptor _ruleInterceptor;
        private readonly PostableCommand _mirrorCommand;
        private readonly Func<CommandProtectionBinding?> _commandProtectionGetter;
        private readonly Func<IEventProtectionService?>? _eventProtectionGetter;
        private readonly Func<EventInterventionHandler?>? _interventionHandlerFactory;
        private readonly Func<BIManage.Data.SQLite.AuditRepository?>? _auditRepoGetter;
        private readonly Func<BIManage.Data.SQLite.RegisteredModelsRepository?>? _registeredModelsRepoGetter;

        public MirrorCommandBinding(
            UIApplication uiApp,
            ILogger logger,
            IRuleCommandInterceptor ruleInterceptor,
            PostableCommand mirrorCommand, // MirrorPickAxis or MirrorDrawAxis
            Func<CommandProtectionBinding?>? commandProtectionGetter = null,
            Func<IEventProtectionService?>? eventProtectionGetter = null,
            Func<EventInterventionHandler?>? interventionHandlerFactory = null,
            Func<BIManage.Data.SQLite.AuditRepository?>? auditRepoGetter = null,
            Func<BIManage.Data.SQLite.RegisteredModelsRepository?>? registeredModelsRepoGetter = null)
            : base(uiApp, logger)
        {
            _ruleInterceptor = ruleInterceptor;
            _mirrorCommand = mirrorCommand;
            _commandProtectionGetter = commandProtectionGetter ?? (() => null);
            _eventProtectionGetter = eventProtectionGetter;
            _interventionHandlerFactory = interventionHandlerFactory;
            _auditRepoGetter = auditRepoGetter;
            _registeredModelsRepoGetter = registeredModelsRepoGetter;
            CommandId = RevitCommandId.LookupPostableCommandId(mirrorCommand);
        }

        public override void RegisterWithBeforeExecute()
        {
            if (!CanRegister())
                return;

            try
            {
                _binding = UIApp.CreateAddInCommandBinding(CommandId);
                _binding.BeforeExecuted += OnBeforeExecuted;
                // NOTE: Do NOT attach Executed handler - it prevents native Mirror command from running
                // With only BeforeExecuted, Revit will proceed with native Mirror after our handler completes

                // Mark as registered in the shared registry to prevent duplicate bindings
                MarkAsRegistered(CommandId.Name);

                Logger?.LogInfo($"Mirror command binding registered: {CommandId.Name} (ID: {CommandId.Id}, PostableCommand: {_mirrorCommand})");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to register Mirror binding for {_mirrorCommand}: {ex.Message}", ex);
            }
        }

        private void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e)
        {
            try
            {
                // Log when Mirror command is activated (user requirement)
                Logger?.LogInfo($"Mirror command activated: {e.CommandId.Name} ({_mirrorCommand})");

                // PRIORITY -1: Equipment Mirror Protection — block mirror for protected equipment categories.
                // Applies to BOTH MirrorPickAxis and MirrorDrawAxis.
                // Only returns early when the action is actually blocked (e.Cancel = true).
                // All other paths (protection disabled, model not registered, action allowed) fall through
                // to CommandProtection and rule interceptor so rules always evaluate.
                if (TryGetProtectedEquipment(sender, out var protectedCategories, out var blockedCount))
                {
                    Logger?.LogInfo($"Equipment Mirror Protection triggered — {blockedCount} element(s) in: {string.Join(", ", protectedCategories)}");

                    var protectionService = _eventProtectionGetter?.Invoke();
                    var handler = _interventionHandlerFactory?.Invoke();

                    if (protectionService != null && handler != null)
                    {
                        try { protectionService.RefreshSettings(); } catch { }
                        var settings = protectionService.GetProtectionByDummyCommandId("Ze_EquipmentMirrorProtection");
                        if (settings != null && settings.Enabled)
                        {
                            var uidoc = (sender as UIApplication)?.ActiveUIDocument;
                            var doc = uidoc?.Document;

                            var registeredModelsRepo = _registeredModelsRepoGetter?.Invoke();
                            if (registeredModelsRepo != null)
                            {
                                var mirrorModelGuid = ModelGuidHelper.GetModelGuid(doc, Logger);
                                var isMirrorRegistered = !string.IsNullOrEmpty(mirrorModelGuid) &&
                                    registeredModelsRepo.IsModelRegisteredAsync(mirrorModelGuid).GetAwaiter().GetResult();
                                if (isMirrorRegistered)
                                {
                                    var context = new EventContext
                                    {
                                        Document = doc,
                                        FilePath = doc?.PathName,
                                        CurrentRevitVersion = doc?.Application?.VersionNumber
                                    };

                                    var additionalInfo =
                                        $"Mirror blocked for {blockedCount} element(s) in protected categories:\n" +
                                        $"  • {string.Join("\n  • ", protectedCategories)}";

                                    var result = handler.ProcessIntervention(settings, context, additionalInfo);
                                    LogMirrorAudit(settings, doc, result, protectedCategories);

                                    if (!result.Allowed)
                                    {
                                        e.Cancel = true;
                                        return;
                                    }
                                }
                                else
                                {
                                    Logger?.LogInfo($"Equipment Mirror protection skipped — '{doc?.Title}' is not a registered model.");
                                }
                            }
                        }
                    }
                    // Fall through to CommandProtection and rule interceptor
                }

                // PRIORITY 0: Check CommandProtectionBinding for Notify/Assist/Protect settings
                var commandProtection = _commandProtectionGetter?.Invoke();
                if (commandProtection != null)
                {
                    commandProtection.ProcessCommandBeforeExecution(e);
                    if (e.Cancel)
                    {
                        Logger?.LogInfo("Mirror command cancelled by command protection");
                        return;
                    }
                }

                // Check database rules if present (existing logic)
                _ruleInterceptor?.OnBeforeExecuted(sender, e);
                if (e.Cancel)
                {
                    Logger?.LogInfo($"Mirror command cancelled by rule evaluation");
                    return;
                }

                // Mirror creates mirrored copies without moving originals — no pin blocking needed
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in Mirror BeforeExecuted: {ex.Message}", ex);
            }
        }

        public override void Register()
        {
            // For Mirror, we use RegisterWithBeforeExecute (BeforeExecuted only - no Executed handler)
            RegisterWithBeforeExecute();
        }

        public override void Unregister()
        {
            if (_binding != null)
            {
                _binding.BeforeExecuted -= OnBeforeExecuted;
                // NOTE: Executed handler is not attached, so no need to unsubscribe
                Logger?.LogDebug($"Mirror command binding unregistered ({_mirrorCommand})");
            }
        }

        /// <summary>
        /// Returns true if the current selection contains at least one element in a protected
        /// equipment category. Outputs the distinct list of category names found and the count.
        /// </summary>
        private bool TryGetProtectedEquipment(object sender, out List<string> categoryNames, out int blockedCount)
        {
            categoryNames = new List<string>();
            blockedCount = 0;
            try
            {
                var uidoc = (sender as UIApplication)?.ActiveUIDocument;
                if (uidoc == null) return false;
                var doc = uidoc.Document;

                var selection = uidoc.Selection.GetElementIds();
                if (selection == null || selection.Count == 0) return false;

                var hits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in selection)
                {
                    Element el;
                    try { el = doc.GetElement(id); }
                    catch { continue; }
                    if (el == null || el.Category == null) continue;

                    var cat = el.Category;
                    bool isProtected = false;

                    // BuiltInCategory match (reliable across Revit versions)
                    try
                    {
                        var bic = (BuiltInCategory)cat.Id.GetIdValueAsInt32();
                        if (ProtectedEquipmentBuiltInCategories.Contains(bic))
                            isProtected = true;
                    }
                    catch { /* Non-builtin category id — fall through */ }

                    // Display-name fallback (catches Food Service / Zone Equipment variants)
                    if (!isProtected && !string.IsNullOrEmpty(cat.Name)
                        && ProtectedEquipmentCategoryNames.Contains(cat.Name))
                        isProtected = true;

                    if (isProtected)
                    {
                        blockedCount++;
                        hits.Add(cat.Name);
                    }
                }

                categoryNames = hits.OrderBy(n => n).ToList();
                return blockedCount > 0;
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Equipment Mirror Protection check failed: {ex.Message}");
                return false;
            }
        }

        private void LogMirrorAudit(
            EventProtectionSettings settings,
            Document? doc,
            EventInterventionResult result,
            List<string> categories)
        {
            try
            {
                var repo = _auditRepoGetter?.Invoke();
                if (repo == null) return;

                var entry = new ProtectionAuditEntry
                {
                    AuditLogId = Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow,
                    UserName = Environment.UserName,
                    ModelGuid = TryGetModelGuid(doc),
                    CommandName = "MirrorEquipment",
                    Mode = MapMode(settings.Mode),
                    Action = result.Allowed
                        ? (result.UserOverrode ? ProtectionAction.Override : ProtectionAction.Allowed)
                        : ProtectionAction.Blocked,
                    Reason = result.Reason ?? $"Equipment categories: {string.Join(", ", categories)}",
                    EventSource = "Event Restriction",
                    UserComment = result.UserComment,
                    SessionId = string.Empty
                };
                _ = repo.SaveAuditEntryAsync(entry);
            }
            catch (Exception ex)
            {
                Logger?.LogDebug($"MirrorCommandBinding.LogMirrorAudit failed: {ex.Message}");
            }
        }

        private static string? TryGetModelGuid(Document? doc)
        {
            try { return doc != null ? BIManage.Revit.Helpers.ModelGuidHelper.GetModelGuid(doc, null) : null; }
            catch { return null; }
        }

        private static ProtectionMode MapMode(InterventionMode mode) => mode switch
        {
            InterventionMode.Notify  => ProtectionMode.Notify,
            InterventionMode.Assist  => ProtectionMode.Assist,
            InterventionMode.Protect => ProtectionMode.Protect,
            _                        => ProtectionMode.Notify
        };
    }
}
