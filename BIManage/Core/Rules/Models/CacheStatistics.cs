using System;

namespace BIManage.Core.Rules.Models
{
    /// <summary>
    /// Cache statistics for monitoring rule cache performance
    /// </summary>
    public class CacheStatistics
    {
        public int TotalRules { get; set; }
        public int EnabledRules { get; set; }
        public int DisabledRules { get; set; }
        public DateTime LastRefresh { get; set; }
        public int MonitorRules { get; set; }
        public int GuideRules { get; set; }
        public int PreventRules { get; set; }

        public override string ToString()
        {
            return $"Rules: {TotalRules} total ({EnabledRules} enabled, {DisabledRules} disabled) | " +
                   $"Modes: {MonitorRules} monitor, {GuideRules} guide, {PreventRules} prevent | " +
                   $"Last refresh: {LastRefresh:yyyy-MM-dd HH:mm:ss}";
        }
    }
}
