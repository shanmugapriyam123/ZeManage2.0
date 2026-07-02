using System;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Context;
using BIManage.Revit.Helpers;

namespace BIManage.Revit.Commands.Bindings
{
    /// <summary>
    /// Times the post-open Manage Links dialog. Brackets the dialog with
    /// BeforeExecuted / Executed and accumulates the open→close delta into
    /// document_sessions.total_link_load_seconds for the active (sessionId, modelGuid)
    /// row. After each accumulation a fire-and-forget re-sync of the model session is
    /// queued so the server picks up the new total.
    ///
    /// The Manage Links command id varies between Revit versions; we try the well-known
    /// names in order and bind the first one that succeeds.
    /// </summary>
    public class ManageLinksCommandBinding : CommandBindingBase
    {
        private AddInCommandBinding? _binding;
        private readonly Func<IRevitContext?> _revitContextGetter;
        private readonly SessionRepository? _sessionRepository;
        private readonly Func<ModelSessionSyncService?> _modelSessionSyncGetter;
        private DateTime? _dialogOpenedAt;
        private string? _activeModelGuidAtOpen;

        // Probed in order; first hit wins. ID_LINK_MANAGEMENT is the canonical Manage Links
        // dialog on every Revit version we ship. The remaining entries are defensive fallbacks
        // if Autodesk renames the id in a future release.
        private static readonly string[] CandidateCommandIds =
        {
            "ID_LINK_MANAGEMENT",
            "ID_RVT_LINKS_RELOAD",
            "ID_FILE_MANAGE_LINKS"
        };

        public ManageLinksCommandBinding(
            UIApplication uiApp,
            ILogger logger,
            SessionRepository? sessionRepository,
            Func<IRevitContext?>? revitContextGetter = null,
            Func<ModelSessionSyncService?>? modelSessionSyncGetter = null)
            : base(uiApp, logger)
        {
            _sessionRepository = sessionRepository;
            _revitContextGetter = revitContextGetter ?? (() => null);
            _modelSessionSyncGetter = modelSessionSyncGetter ?? (() => null);
        }

        public override void RegisterWithBeforeExecute() => Register();

        public override void Register()
        {
            foreach (var idStr in CandidateCommandIds)
            {
                try
                {
                    var cmdId = RevitCommandId.LookupCommandId(idStr);
                    if (cmdId == null || !cmdId.CanHaveBinding) continue;
                    if (HasIndividualBinding(cmdId.Name))
                    {
                        Logger?.LogDebug($"ManageLinksCommandBinding: {cmdId.Name} already bound — skipping");
                        return;
                    }

                    CommandId = cmdId;
                    _binding = UIApp.CreateAddInCommandBinding(cmdId);
                    _binding.BeforeExecuted += OnBeforeExecuted;
                    _binding.Executed += OnExecuted;
                    MarkAsRegistered(cmdId.Name);
                    Logger?.LogInfo($"Manage Links command binding registered: {cmdId.Name}");
                    return;
                }
                catch (Exception ex)
                {
                    Logger?.LogDebug($"ManageLinksCommandBinding: {idStr} not bindable ({ex.Message})");
                }
            }
            Logger?.LogWarning("ManageLinksCommandBinding: no candidate command id was bindable on this Revit version");
        }

        public override void Unregister()
        {
            if (_binding != null)
            {
                _binding.BeforeExecuted -= OnBeforeExecuted;
                _binding.Executed -= OnExecuted;
                _binding = null;
                if (CommandId != null) MarkAsUnregistered(CommandId.Name);
            }
        }

        private void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e)
        {
            try
            {
                _dialogOpenedAt = DateTime.UtcNow;
                var doc = (sender as UIApplication)?.ActiveUIDocument?.Document
                          ?? UIApp.ActiveUIDocument?.Document;
                _activeModelGuidAtOpen = doc != null ? ModelGuidHelper.GetModelGuid(doc, Logger) : null;
            }
            catch (Exception ex)
            {
                Logger?.LogDebug($"ManageLinksCommandBinding.BeforeExecuted: {ex.Message}");
            }
        }

        private void OnExecuted(object sender, ExecutedEventArgs e)
        {
            if (_dialogOpenedAt == null) return;
            var deltaSeconds = (DateTime.UtcNow - _dialogOpenedAt.Value).TotalSeconds;
            var modelGuid = _activeModelGuidAtOpen;
            _dialogOpenedAt = null;
            _activeModelGuidAtOpen = null;

            if (deltaSeconds <= 0) return;
            if (string.IsNullOrEmpty(modelGuid)) return;
            if (_sessionRepository == null) return;

            var sessionId = _revitContextGetter?.Invoke()?.SessionId.ToString();
            if (string.IsNullOrEmpty(sessionId)) return;

            var revitUsername = e.ActiveDocument?.Application?.Username ?? Environment.UserName;

            Task.Run(async () =>
            {
                try
                {
                    var newTotal = await _sessionRepository.IncrementLinkLoadTimeAsync(
                        sessionId!, modelGuid!, deltaSeconds, revitUsername);
                    if (newTotal == null) return;

                    Logger?.LogInfo($"[Idle] Manage Links dialog: +{deltaSeconds:F2}s → total {newTotal.Value:F2}s (model={modelGuid})");

                    var sync = _modelSessionSyncGetter?.Invoke();
                    if (sync != null)
                    {
                        await sync.SyncLinkLoadingDurationAsync(sessionId!, modelGuid!, newTotal.Value, revitUsername);
                    }
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"ManageLinksCommandBinding.OnExecuted async path failed: {ex.Message}");
                }
            });
        }
    }
}
