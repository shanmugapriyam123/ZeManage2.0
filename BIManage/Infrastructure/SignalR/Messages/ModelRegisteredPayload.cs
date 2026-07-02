using System;

namespace BIManage.Infrastructure.SignalR.Messages
{
    /// <summary>
    /// Revit-side DTO matching the API's ModelRegisterResponse.
    /// Deserialized from the "ModelRegistered" SignalR message payload.
    /// </summary>
    public class ModelRegisteredPayload
    {
        public Guid ModelGuid { get; set; }
        public Guid CompanyId { get; set; }
        public string ModelName { get; set; }
        public string CentralModelPath { get; set; }
        public string LocalProjectName { get; set; }
        public Guid? ZemanageProjectId { get; set; }
        public bool IsActive { get; set; }
        public DateTime? RegisteredAt { get; set; }
        public string RegisteredBy { get; set; }
        public DateTime? DeactivatedAt { get; set; }
        public string DeactivatedBy { get; set; }
        public string DeactivationReason { get; set; }
        public string Notes { get; set; }
        public DateTime? LastOpenedAt { get; set; }
        public string LastOpenedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public bool IsWorkshared { get; set; }
        public bool IsFamily { get; set; }
        public bool IsCloudModel { get; set; }

    }
}
