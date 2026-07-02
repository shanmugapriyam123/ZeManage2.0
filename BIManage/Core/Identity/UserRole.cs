namespace BIManage.Core.Identity
{
    /// <summary>
    /// User role enumeration for BIManage RBAC system
    /// Uses explicit numeric values for serialization stability
    /// </summary>
    public enum UserRole
    {
        /// <summary>
        /// Company Administrator - Full access across all projects in the organization
        /// </summary>
        CompanyAdministrator = 100,

        /// <summary>
        /// Project Administrator - Access to assigned projects only
        /// </summary>
        ProjectAdministrator = 101,

        /// <summary>
        /// Normal User - Standard user with protections enforced
        /// </summary>
        NormalUser = 102
    }
}
