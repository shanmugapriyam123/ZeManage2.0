using System;

namespace BIManage.Revit.SyncTrafficControl.Models
{
    public enum SyncAction
    {
        Allow,
        Blocked
    }

    public class SyncDecision
    {
        public SyncAction Action { get; set; }
        public string BlockedByUsername { get; set; }
        public DateTime? BlockedSince { get; set; }

        public static SyncDecision Allow() => new SyncDecision { Action = SyncAction.Allow };

        public static SyncDecision Blocked(string username, DateTime? since) => new SyncDecision
        {
            Action = SyncAction.Blocked,
            BlockedByUsername = username,
            BlockedSince = since
        };
    }

    public class SyncerInfo
    {
        public string SessionId { get; set; }
        public string Username { get; set; }
        public string RevitUsername { get; set; }
        public DateTime StartedAtUtc { get; set; }
    }
}
