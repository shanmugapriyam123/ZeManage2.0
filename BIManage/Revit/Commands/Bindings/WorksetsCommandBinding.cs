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
    /// Times the post-open Worksets dialog. Brackets the dialog with BeforeExecuted /
    /// Executed on PostableCommand.Worksets and accumulates the open→close delta into
    /// document_sessions.total_workset_open_seconds for the active (sessionId, modelGuid)
    /// row. After each accumulation a fire-and-forget re-sync of the model session is
    /// queued so the server picks up the new total.
    ///
    /// Pure measurement — no protection / cancel logic. Initial document-open workset
    /// load is intentionally out of scope (Revit gives no progress event for it).
    /// </summary>
    public class WorksetsCommandBinding : CommandBindingBase
    {
        private AddInCommandBinding? _binding;
        private readonly Func<IRevitContext?> _revitContextGetter;
        private readonly SessionRepository? _sessionRepository;
        private readonly Func<ModelSessionSyncService?> _modelSessionSyncGetter;
        private DateTime? _dialogOpenedAt;
        private string? _activeModelGuidAtOpen;

        public WorksetsCommandBinding(
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

            CommandId = RevitCommandId.LookupPostableCommandId(PostableCommand.Worksets);
        }

        public override void RegisterWithBeforeExecute() => Register();

        public override void Register()
        {
            if (!CanRegister()) return;
            try
            {
                if (HasIndividualBinding(CommandId.Name))
                {
                    Logger?.LogDebug($"WorksetsCommandBinding: {CommandId.Name} already bound — skipping");
                    return;
                }
                _binding = UIApp.CreateAddInCommandBinding(CommandId);
                _binding.BeforeExecuted += OnBeforeExecuted;
                _binding.Executed += OnExecuted;
                MarkAsRegistered(CommandId.Name);
                Logger?.LogInfo($"Worksets command binding registered: {CommandId.Name} (ID: {CommandId.Id})");
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"WorksetsCommandBinding: registration failed: {ex.Message}");
            }
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

                // Capture the model GUID at open-time. Revit may activate a different doc
                // while the dialog is up, but the dialog still acts on the document the
                // command was invoked from — attribute the wait to that document.
                var doc = (sender as UIApplication)?.ActiveUIDocument?.Document
                          ?? UIApp.ActiveUIDocument?.Document;
                _activeModelGuidAtOpen = doc != null ? ModelGuidHelper.GetModelGuid(doc, Logger) : null;
            }
            catch (Exception ex)
            {
                Logger?.LogDebug($"WorksetsCommandBinding.BeforeExecuted: {ex.Message}");
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

            // Fire-and-forget — never block Revit's UI thread on DB or HTTP.
            Task.Run(async () =>
            {
                try
                {
                    var newTotal = await _sessionRepository.IncrementWorksetOpenTimeAsync(
                        sessionId!, modelGuid!, deltaSeconds, revitUsername);
                    if (newTotal == null) return;

                    Logger?.LogInfo($"[Idle] Worksets dialog: +{deltaSeconds:F2}s → total {newTotal.Value:F2}s (model={modelGuid})");

                    var sync = _modelSessionSyncGetter?.Invoke();
                    if (sync != null)
                    {
                        await sync.SyncWorksetOpeningDurationAsync(sessionId!, modelGuid!, newTotal.Value, revitUsername);
                    }
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"WorksetsCommandBinding.OnExecuted async path failed: {ex.Message}");
                }
            });
        }
    }
}
