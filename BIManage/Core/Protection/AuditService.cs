using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BIManage.Common.Helpers;
using BIManage.Core.Protection.Models;
using BIManage.Core.Rules.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Helpers;

namespace BIManage.Core.Protection
{
    /// <summary>
    /// Comprehensive audit service for protection actions
    /// </summary>
    public class AuditService
    {
        private readonly AuditRepository _repository;
        private readonly ILogger _logger;

        public AuditService(AuditRepository repository, ILogger logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Log Monitor mode action (passive tracking)
        /// </summary>
        public void LogMonitorAction(
            RuleEvaluationResult evaluationResult,
            IEnumerable<Element> elements,
            int commandId,
            string commandName)
        {
            try
            {
                var elementList = elements?.ToList() ?? new List<Element>();
                var firstRule = evaluationResult?.MatchedRules?.FirstOrDefault();

                var entry = new ProtectionAuditEntry
                {
                    Timestamp = DateTime.UtcNow,
                    UserName = elementList.FirstOrDefault()?.Document?.Application?.Username ?? Environment.UserName,
                    ModelGuid = GetModelGuidFromElements(elementList),
                    ProtectionId = firstRule?.RuleId,
                    CommandName = commandName,
                    Mode = ProtectionMode.Notify,
                    Action = ProtectionAction.Allowed,
                    ElementIds = GetElementIdsString(elementList),
                    ElementCount = elementList.Count,
                    Reason = evaluationResult?.CombinedMessage ?? "Monitor mode tracking",
                    EventSource = "Rule Management",
                    // Per user request: every audit entry starts at sent_mail=1 (true)
                    // so the row shows [v] in the DB from the moment it's inserted.
                    SentMail = true
                };

                _repository.SaveAuditEntry(entry);
                _logger?.LogDebug($"Monitor action logged: {entry.ElementCount} elements, command {commandName}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to log monitor action: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Log Guide mode action (user decision)
        /// </summary>
        public void LogGuideAction(
            RuleEvaluationResult evaluationResult,
            IEnumerable<Element> elements,
            int commandId,
            string commandName,
            bool userAllowed,
            string userComment = null)
        {
            try
            {
                var elementList = elements?.ToList() ?? new List<Element>();
                var firstRule = evaluationResult?.MatchedRules?.FirstOrDefault();

                var entry = new ProtectionAuditEntry
                {
                    Timestamp = DateTime.UtcNow,
                    UserName = elementList.FirstOrDefault()?.Document?.Application?.Username ?? Environment.UserName,
                    ModelGuid = GetModelGuidFromElements(elementList),
                    ProtectionId = firstRule?.RuleId,
                    CommandName = commandName,
                    Mode = ProtectionMode.Assist,
                    Action = userAllowed ? ProtectionAction.Allowed : ProtectionAction.Cancelled,
                    ElementIds = GetElementIdsString(elementList),
                    ElementCount = elementList.Count,
                    Reason = evaluationResult?.CombinedMessage ?? "Guide mode intervention",
                    UserComment = userComment,
                    EventSource = "Rule Management",
                    // Per user request: every audit entry starts at sent_mail=1 (true)
                    // so the row shows [v] in the DB from the moment it's inserted.
                    SentMail = true
                };

                _repository.SaveAuditEntry(entry);
                _logger?.LogInfo($"Guide action logged: {entry.Action}, {entry.ElementCount} elements, command {commandName}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to log guide action: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Log Prevent mode action (blocked or override)
        /// </summary>
        public void LogPreventAction(
            RuleEvaluationResult evaluationResult,
            IEnumerable<Element> elements,
            int commandId,
            string commandName,
            bool overrideUsed)
        {
            try
            {
                var elementList = elements?.ToList() ?? new List<Element>();
                var firstRule = evaluationResult?.MatchedRules?.FirstOrDefault();

                var entry = new ProtectionAuditEntry
                {
                    Timestamp = DateTime.UtcNow,
                    UserName = elementList.FirstOrDefault()?.Document?.Application?.Username ?? Environment.UserName,
                    ModelGuid = GetModelGuidFromElements(elementList),
                    ProtectionId = firstRule?.RuleId,
                    CommandName = commandName,
                    Mode = ProtectionMode.Protect,
                    Action = overrideUsed ? ProtectionAction.Override : ProtectionAction.Blocked,
                    ElementIds = GetElementIdsString(elementList),
                    ElementCount = elementList.Count,
                    Reason = evaluationResult?.CombinedMessage ?? "Prevent mode enforcement",
                    OverrideMethod = overrideUsed ? "AdminPassword" : null,
                    EventSource = "Rule Management",
                    // Per user request: every audit entry starts at sent_mail=1 (true)
                    // so the row shows [v] in the DB from the moment it's inserted.
                    SentMail = true
                };

                _repository.SaveAuditEntry(entry);
                _logger?.LogWarning($"Prevent action logged: {entry.Action}, {entry.ElementCount} elements, command {commandName}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to log prevent action: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Log command-based protection action (for CommandInterventionHandler)
        /// </summary>
        public void LogCommandProtection(
            string commandId,
            string commandName,
            ProtectionMode mode,
            bool allowed,
            string reason = null,
            bool overrideUsed = false)
        {
            try
            {
                var entry = new ProtectionAuditEntry
                {
                    Timestamp = DateTime.UtcNow,
                    UserName = Environment.UserName,
                    CommandName = commandName,
                    Mode = mode,
                    Action = allowed ? ProtectionAction.Allowed : ProtectionAction.Blocked,
                    ElementCount = 0,
                    Reason = reason ?? $"Command protection: {mode}",
                    OverrideMethod = overrideUsed ? "AdminPassword" : null,
                    EventSource = "Command Restriction"
                };

                _repository.SaveAuditEntry(entry);
                _logger?.LogInfo($"Command protection logged: {commandName}, Mode: {mode}, Action: {entry.Action}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to log command protection: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get audit entries for reporting
        /// </summary>
        public List<ProtectionAuditEntry> GetAuditEntries(DateTime? startDate = null, DateTime? endDate = null, int limit = 100)
        {
            try
            {
                return Task.Run(() => _repository.GetAuditEntriesAsync(startDate, endDate, limit)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to retrieve audit entries: {ex.Message}", ex);
                return new List<ProtectionAuditEntry>();
            }
        }

        /// <summary>
        /// Get audit statistics
        /// </summary>
        public AuditStatistics GetStatistics(DateTime? startDate = null, DateTime? endDate = null)
        {
            try
            {
                var entries = GetAuditEntries(startDate, endDate, 10000);

                var stats = new AuditStatistics
                {
                    TotalActions = entries.Count,
                    MonitorActions = entries.Count(e => e.Mode == ProtectionMode.Notify),
                    GuideActions = entries.Count(e => e.Mode == ProtectionMode.Assist),
                    PreventActions = entries.Count(e => e.Mode == ProtectionMode.Protect),
                    AllowedActions = entries.Count(e => e.Action == ProtectionAction.Allowed),
                    BlockedActions = entries.Count(e => e.Action == ProtectionAction.Blocked),
                    CancelledActions = entries.Count(e => e.Action == ProtectionAction.Cancelled),
                    OverrideActions = entries.Count(e => e.Action == ProtectionAction.Override),
                    TotalElements = entries.Sum(e => e.ElementCount),
                    UniqueUsers = entries.Select(e => e.UserName).Distinct().Count()
                };

                return stats;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to calculate audit statistics: {ex.Message}", ex);
                return new AuditStatistics();
            }
        }

        private string GetModelGuidFromElements(List<Element> elements)
        {
            try
            {
                var doc = elements?.FirstOrDefault()?.Document;
                return doc != null ? ModelGuidHelper.GetModelGuid(doc, _logger) : null;
            }
            catch { return null; }
        }

        private string GetElementIdsString(List<Element> elements)
        {
            if (elements == null || elements.Count == 0)
                return string.Empty;

            return string.Join(",", elements.Select(e => e.Id.GetIdValue()));
        }
    }

    /// <summary>
    /// Statistics about audit entries
    /// </summary>
    public class AuditStatistics
    {
        public int TotalActions { get; set; }
        public int MonitorActions { get; set; }
        public int GuideActions { get; set; }
        public int PreventActions { get; set; }
        public int AllowedActions { get; set; }
        public int BlockedActions { get; set; }
        public int CancelledActions { get; set; }
        public int OverrideActions { get; set; }
        public int TotalElements { get; set; }
        public int UniqueUsers { get; set; }
    }
}
