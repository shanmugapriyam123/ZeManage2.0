using BIManage.Core.Features;

namespace BIManage.Revit.EventRegistry
{
    /// <summary>
    ///     Centralized toggle checks for Revit event subscriptions.
    /// </summary>
    public class EventTogglePolicy : IEventTogglePolicy
    {
        private readonly IFeatureToggleService _featureToggleService;

        public EventTogglePolicy(IFeatureToggleService featureToggleService)
        {
            _featureToggleService = featureToggleService;
        }

        public bool ShouldMonitorDocuments() => _featureToggleService.IsFeatureEnabled("DocumentLifecycleEvents");
        public bool ShouldMonitorCommands() => _featureToggleService.IsFeatureEnabled("CommandInterception");
        public bool ShouldMonitorIdling() => _featureToggleService.IsFeatureEnabled("IdlingEventPolling");
        public bool ShouldMonitorUITracking() => _featureToggleService.IsFeatureEnabled("UITracking");
    }
}
