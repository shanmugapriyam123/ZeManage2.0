namespace BIManage.Infrastructure.SignalR.Groups
{
    /// <summary>
    /// Interface for generating SignalR group names
    /// Ensures consistent group naming matching the API server's NotificationHub
    /// </summary>
    public interface ISignalRGroupHelper
    {
        /// <summary>
        /// Gets the company-level group name for receiving company-wide broadcasts
        /// </summary>
        /// <param name="companyId">Company identifier</param>
        /// <returns>Group name in format: company:{companyId}</returns>
        string GetCompanyGroup(string companyId);

        /// <summary>
        /// Gets the machine-level group name for device-specific notifications
        /// </summary>
        /// <param name="machineId">Machine identifier</param>
        /// <returns>Group name in format: machine:{machineId}</returns>
        string GetMachineGroup(string machineId);

        /// <summary>
        /// Gets the user-level group name for provider-specific notifications
        /// </summary>
        /// <param name="userId">User identifier</param>
        /// <returns>Group name in format: user:{userId}</returns>
        string GetUserGroup(string userId);

        /// <summary>
        /// Gets the admin group name for company administrators
        /// </summary>
        /// <param name="companyId">Company identifier</param>
        /// <returns>Group name in format: admin:{companyId}</returns>
        string GetAdminGroup(string companyId);

        /// <summary>
        /// Gets the project-level group name for receiving project updates
        /// </summary>
        /// <param name="projectId">Project identifier</param>
        /// <returns>Group name in format: project:{projectId}</returns>
        string GetProjectGroup(string projectId);

        /// <summary>
        /// Gets the model-level group name for receiving model-specific messages
        /// </summary>
        /// <param name="modelGuid">Model GUID</param>
        /// <returns>Group name in format: model:{modelGuid}</returns>
        string GetModelGroup(string modelGuid);

        /// <summary>
        /// Gets the sync coordination group name for a model
        /// </summary>
        /// <param name="modelGuid">Model GUID</param>
        /// <returns>Group name in format: sync:{modelGuid}</returns>
        string GetSyncGroup(string modelGuid);

        /// <summary>
        /// Gets the conversation group name for 1-to-1 direct messaging
        /// </summary>
        /// <param name="conversationId">Conversation identifier</param>
        /// <returns>Group name in format: conversation:{conversationId}</returns>
        string GetConversationGroup(string conversationId);

        /// <summary>
        /// Generates a deterministic conversation ID between two participants.
        /// Pass profileId or machineId — works for admin↔device and device↔device.
        /// Sorts alphabetically so both sides produce the same result.
        /// </summary>
        /// <param name="participantIdA">First participant (profileId or machineId)</param>
        /// <param name="participantIdB">Second participant (profileId or machineId)</param>
        /// <returns>Deterministic conversation ID: {sorted-first}_{sorted-second}</returns>
        string GetConversationId(string participantIdA, string participantIdB);
    }
}
