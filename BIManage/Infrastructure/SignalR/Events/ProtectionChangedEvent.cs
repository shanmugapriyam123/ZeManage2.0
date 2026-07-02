using System.Collections.Generic;

namespace BIManage.Infrastructure.SignalR.Events
{
    /// <summary>
    /// Published via SignalREventBus when a ProtectionSettingsChange message is processed.
    /// Subscribers (e.g. CommandProtectionRepository) react by refetching the scoped slice.
    /// </summary>
    public class ProtectionChangedEvent : SignalREvent
    {
        /// <summary>"Command", "Event", "Rule", "Pin", "HealthMonitor", "Activity".</summary>
        public string ProtectionType { get; set; }

        /// <summary>"Created", "Updated", "Deleted".</summary>
        public string ChangeType { get; set; }

        /// <summary>1=Company, 2=Project, 3=Model.</summary>
        public int LevelScope { get; set; }

        /// <summary>Populated when LevelScope >= 2.</summary>
        public string ProjectId { get; set; }

        /// <summary>Populated when LevelScope == 3.</summary>
        public string ModelGuid { get; set; }

        public string EntityId { get; set; }

        /// <summary>
        /// Model GUIDs refreshed by this push. Used by listeners that map a
        /// company/project-scope change down to the set of affected models.
        /// </summary>
        public List<string> AffectedModelGuids { get; set; } = new List<string>();
    }
}
