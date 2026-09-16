using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BIManage.Core.Features;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.SignalR.Listeners
{
    /// <summary>
    /// Listens for the server-pushed admin active/inactive toggle (see
    /// SignalRNotificationService.NotifyEmployeeActiveStatusChangedAsync backend-side) — the fast
    /// path for the same toggle AuthTokenManager also picks up from every device-auth/refresh
    /// response's IsActive field (the reliable fallback for whenever this push doesn't land,
    /// since this codebase's SignalR connection has known disconnect history). Sets
    /// FeatureToggleService.IsEmployeeCaptureDisabled, which CommandInterceptionService and the
    /// rest of the capture pipeline already gate on.
    /// </summary>
    public class EmployeeActivationListener : SignalListenerBase
    {
        private readonly IFeatureToggleService _featureToggle;

        public override string Name => "EmployeeActivation";

        public override IEnumerable<string> SupportedMethods => new[]
        {
            SignalRMethods.EmployeeActiveStatusChanged
        };

        public EmployeeActivationListener(IFeatureToggleService featureToggle, ILogger logger) : base(logger)
        {
            _featureToggle = featureToggle ?? throw new ArgumentNullException(nameof(featureToggle));
        }

        protected override Task ProcessMessageAsync(SignalRMessageInfo message)
        {
            LogMessageReceived(message);

            var payload = GetPayloadSafe<EmployeeActiveStatusChangedPayload>(message);
            if (payload == null)
                return Task.CompletedTask;

            _featureToggle.IsEmployeeCaptureDisabled = !payload.IsActive;
            _logger?.LogWarning($"[EmployeeActivation] Capture {(payload.IsActive ? "resumed" : "paused (admin deactivated)")}");

            return Task.CompletedTask;
        }

        private class EmployeeActiveStatusChangedPayload
        {
            public bool IsActive { get; set; } = true;
            public DateTime? Timestamp { get; set; }
        }
    }
}
