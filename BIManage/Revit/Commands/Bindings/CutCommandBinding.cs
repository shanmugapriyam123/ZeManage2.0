using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.Commands.Bindings
{
    /// <summary>
    /// Dedicated binding for Cut to Clipboard command.
    /// Ensures Rule Management fires for Cut even when CommandProtectionBinding
    /// would otherwise claim exclusive ownership of the binding.
    /// Uses BeforeExecuted only — Revit handles the actual cut natively.
    /// </summary>
    public class CutCommandBinding : CommandBindingBase
    {
        private AddInCommandBinding _binding;
        private readonly IRuleCommandInterceptor? _ruleInterceptor;
        private readonly Func<CommandProtectionBinding?> _commandProtectionGetter;
        private readonly BIManage.Core.Features.IFeatureToggleService? _featureToggleService;

        public CutCommandBinding(
            UIApplication uiApp,
            ILogger logger,
            IRuleCommandInterceptor? ruleInterceptor = null,
            Func<CommandProtectionBinding?>? commandProtectionGetter = null,
            BIManage.Core.Features.IFeatureToggleService? featureToggleService = null)
            : base(uiApp, logger)
        {
            _ruleInterceptor = ruleInterceptor;
            _commandProtectionGetter = commandProtectionGetter ?? (() => null);
            _featureToggleService = featureToggleService;

            // Excel-verified: CutToClipboard | ID_EDIT_CUT; fallback to PostableCommand enum
            CommandId = RevitCommandId.LookupCommandId("ID_EDIT_CUT")
                     ?? RevitCommandId.LookupPostableCommandId(PostableCommand.CutToClipboard);
        }

        public override void RegisterWithBeforeExecute()
        {
            if (!CanRegister())
                return;

            try
            {
                _binding = UIApp.CreateAddInCommandBinding(CommandId);
                _binding.BeforeExecuted += OnBeforeExecuted;
                MarkAsRegistered(CommandId.Name);
                Logger?.LogInfo($"Cut command binding registered: {CommandId.Name} (ID: {CommandId.Id})");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Failed to register Cut binding: {ex.Message}", ex);
            }
        }

        private void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e)
        {
            try
            {
                if (_featureToggleService?.IsGlobalPaused == true || _featureToggleService?.IsEmployeeCaptureDisabled == true)
                    return;

                Logger?.LogInfo($"Cut command intercepted: {e.CommandId.Name}");

                // PRIORITY 0: Command Protection (Notify/Assist/Protect)
                var commandProtection = _commandProtectionGetter?.Invoke();
                if (commandProtection != null)
                {
                    commandProtection.ProcessCommandBeforeExecution(e);
                    if (e.Cancel)
                    {
                        Logger?.LogInfo("Cut command cancelled by command protection");
                        return;
                    }
                }

                // PRIORITY 1: Rule Management
                _ruleInterceptor?.OnBeforeExecuted(sender, e);
                if (e.Cancel)
                    Logger?.LogInfo("Cut command cancelled by rule evaluation");
            }
            catch (Exception ex)
            {
                Logger?.LogError($"Error in Cut BeforeExecuted: {ex.Message}", ex);
            }
        }

        public override void Register() => RegisterWithBeforeExecute();

        public override void Unregister()
        {
            if (_binding != null)
            {
                _binding.BeforeExecuted -= OnBeforeExecuted;
                Logger?.LogDebug("Cut command binding unregistered");
            }
        }
    }
}
