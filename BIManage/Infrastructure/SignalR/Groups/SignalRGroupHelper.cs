namespace BIManage.Infrastructure.SignalR.Groups
{
    /// <summary>
    /// Implementation of SignalR group name generation
    /// Uses consistent naming patterns matching the API server's NotificationHub conventions
    /// Format: "prefix:id" (e.g., "company:abc-123", "model:def-456")
    /// </summary>
    public class SignalRGroupHelper : ISignalRGroupHelper
    {
        /// <inheritdoc />
        public string GetCompanyGroup(string companyId)
        {
            if (string.IsNullOrWhiteSpace(companyId))
                return null;

            return $"company:{companyId.Trim()}";
        }

        /// <inheritdoc />
        public string GetMachineGroup(string machineId)
        {
            if (string.IsNullOrWhiteSpace(machineId))
                return null;

            return $"machine:{machineId.Trim()}";
        }

        /// <inheritdoc />
        public string GetUserGroup(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return null;

            return $"user:{userId.Trim()}";
        }

        /// <inheritdoc />
        public string GetAdminGroup(string companyId)
        {
            if (string.IsNullOrWhiteSpace(companyId))
                return null;

            return $"admin:{companyId.Trim()}";
        }

        /// <inheritdoc />
        public string GetProjectGroup(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return null;

            return $"project:{projectId.Trim()}";
        }

        /// <inheritdoc />
        public string GetModelGroup(string modelGuid)
        {
            if (string.IsNullOrWhiteSpace(modelGuid))
                return null;

            return $"model:{modelGuid.Trim()}";
        }

        /// <inheritdoc />
        public string GetSyncGroup(string modelGuid)
        {
            if (string.IsNullOrWhiteSpace(modelGuid))
                return null;

            return $"sync:{modelGuid.Trim()}";
        }

        /// <inheritdoc />
        public string GetConversationGroup(string conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId))
                return null;

            return $"conversation:{conversationId.Trim()}";
        }

        /// <inheritdoc />
        public string GetConversationId(string participantIdA, string participantIdB)
        {
            if (string.IsNullOrWhiteSpace(participantIdA) || string.IsNullOrWhiteSpace(participantIdB))
                return null;

            var a = participantIdA.Trim();
            var b = participantIdB.Trim();
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
                ? $"{a}_{b}"
                : $"{b}_{a}";
        }
    }
}
