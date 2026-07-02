using System.Collections.Generic;

namespace BIManage.Core.Identity
{
    /// <summary>
    /// Service interface for managing user identity and authentication
    /// Provides access to current user information and role checking
    /// </summary>
    public interface IUserService
    {
        /// <summary>
        /// Get the current authenticated user
        /// Returns Windows user identity if not authenticated
        /// </summary>
        UserIdentity CurrentUser { get; }

        /// <summary>
        /// Check if a user is authenticated (logged in)
        /// </summary>
        bool IsAuthenticated { get; }

        /// <summary>
        /// Check if current user has admin privileges for a specific project
        /// </summary>
        /// <param name="projectId">Project ID to check (null for global)</param>
        /// <returns>True if user is admin for this project</returns>
        bool HasAdminPrivilegesForProject(string projectId);

        /// <summary>
        /// Check if current user is a company administrator
        /// </summary>
        bool IsCompanyAdmin { get; }

        /// <summary>
        /// Check if current user is a project administrator
        /// </summary>
        bool IsProjectAdmin { get; }

        /// <summary>
        /// Check if current user has any admin privileges
        /// </summary>
        bool HasAdminPrivileges { get; }

        /// <summary>
        /// Get current user's role
        /// </summary>
        UserRole CurrentRole { get; }

        /// <summary>
        /// Update user identity (typically after login)
        /// </summary>
        /// <param name="user">New user identity</param>
        void SetCurrentUser(UserIdentity user);

        /// <summary>
        /// Logout current user and revert to Windows user
        /// </summary>
        void Logout();

        /// <summary>
        /// Update admin flags for current user
        /// Triggers role change notifications
        /// </summary>
        void UpdateAdminFlags(bool isCompanyAdmin, bool isProjectAdmin, List<string> projectIds = null);

        /// <summary>
        /// Event raised when user role changes
        /// </summary>
        event System.EventHandler<UserRoleChangedEventArgs> RoleChanged;
    }

    /// <summary>
    /// Event arguments for role change notifications
    /// </summary>
    public class UserRoleChangedEventArgs : System.EventArgs
    {
        public UserRole OldRole { get; set; }
        public UserRole NewRole { get; set; }
        public UserIdentity User { get; set; }
    }
}
