using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace BIManage.Core.Identity
{
    /// <summary>
    /// Represents the identity and role information for a BIManage user
    /// Provides role-based access control fields
    /// </summary>
    public class UserIdentity : INotifyPropertyChanged
    {
        private bool _isCompanyAdmin;
        private bool _isProjectAdmin;

        /// <summary>
        /// Unique identifier for the user
        /// </summary>
        public string UserId { get; set; }

        /// <summary>
        /// User's display name
        /// </summary>
        public string UserName { get; set; }

        /// <summary>
        /// User's email address (primary identity)
        /// </summary>
        public string Email { get; set; }

        /// <summary>
        /// Revit username (from application)
        /// </summary>
        public string RevitUserName { get; set; }

        /// <summary>
        /// Company/Tenant ID this user belongs to
        /// </summary>
        public string CompanyId { get; set; }

        /// <summary>
        /// Company name for display purposes
        /// </summary>
        public string CompanyName { get; set; }

        /// <summary>
        /// List of project IDs this user is assigned to as project admin
        /// </summary>
        public List<string> ProjectIds { get; set; } = new List<string>();

        /// <summary>
        /// Flag indicating if user is a Company Administrator (global admin)
        /// </summary>
        public bool IsCompanyAdmin
        {
            get => _isCompanyAdmin;
            set
            {
                if (_isCompanyAdmin != value)
                {
                    _isCompanyAdmin = value;
                    OnPropertyChanged(nameof(IsCompanyAdmin));
                    OnPropertyChanged(nameof(HasAdminPrivileges));
                    OnPropertyChanged(nameof(CurrentRole));
                    OnPropertyChanged(nameof(Privilege));
                }
            }
        }

        /// <summary>
        /// Flag indicating if user is a Project Administrator (project-scoped admin)
        /// </summary>
        public bool IsProjectAdmin
        {
            get => _isProjectAdmin;
            set
            {
                if (_isProjectAdmin != value)
                {
                    _isProjectAdmin = value;
                    OnPropertyChanged(nameof(IsProjectAdmin));
                    OnPropertyChanged(nameof(HasAdminPrivileges));
                    OnPropertyChanged(nameof(CurrentRole));
                    OnPropertyChanged(nameof(Privilege));
                }
            }
        }

        /// <summary>
        /// Computed property: true if user has any admin privileges
        /// </summary>
        public bool HasAdminPrivileges => IsCompanyAdmin || IsProjectAdmin;

        /// <summary>
        /// Resolved user role based on admin flags
        /// Company Admin takes precedence over Project Admin
        /// </summary>
        public UserRole CurrentRole
        {
            get
            {
                if (IsCompanyAdmin)
                    return UserRole.CompanyAdministrator;
                if (IsProjectAdmin)
                    return UserRole.ProjectAdministrator;
                return UserRole.NormalUser;
            }
        }

        /// <summary>
        /// User privilege string for display (e.g., "Company Admin", "Project Admin")
        /// </summary>
        public string Privilege
        {
            get
            {
                if (IsCompanyAdmin)
                    return "Company Admin";
                if (IsProjectAdmin)
                    return "Project Admin";
                return "User";
            }
        }

        /// <summary>
        /// Flag indicating if user is authenticated
        /// </summary>
        public bool? IsAuthorized { get; set; }

        /// <summary>
        /// Profile ID from the API (used for ModifiedBy/CreatedBy in backend sync)
        /// </summary>
        public string ProfileId { get; set; }

        /// <summary>
        /// Authentication token for API calls
        /// </summary>
        public string AdminToken { get; set; }

        /// <summary>
        /// Automatically add this user as admin to new projects they access
        /// </summary>
        public bool AutoAddAsAdminToProjects { get; set; }

        /// <summary>
        /// Permission to be added as admin to projects
        /// </summary>
        public bool AllowAddAsAdminToProjects { get; set; }

        /// <summary>
        /// Timestamp when user identity was last updated
        /// </summary>
        public DateTime? LastUpdated { get; set; }

        /// <summary>
        /// Check if user has admin privileges for a specific project
        /// </summary>
        /// <param name="projectId">Project ID to check (null for global check)</param>
        /// <returns>True if user is admin for this project</returns>
        public bool HasAdminPrivilegesForProject(string projectId)
        {
            // Company admin has access to all projects
            if (IsCompanyAdmin)
                return true;

            // No project specified - only company admin has global access
            if (string.IsNullOrEmpty(projectId))
                return false;

            // Project admin - check if user is assigned to this project
            return IsProjectAdmin && ProjectIds.Contains(projectId);
        }

        /// <summary>
        /// Create a UserIdentity for the current Windows user (unauthenticated)
        /// </summary>
        public static UserIdentity CreateWindowsUser()
        {
            return new UserIdentity
            {
                UserId = Environment.UserName,
                UserName = Environment.UserName,
                RevitUserName = Environment.UserName,
                Email = null,
                IsCompanyAdmin = false,
                IsProjectAdmin = false,
                IsAuthorized = false
            };
        }

        /// <summary>
        /// Create an authenticated admin user
        /// </summary>
        public static UserIdentity CreateCompanyAdmin(string userId, string email, string companyId, string companyName)
        {
            return new UserIdentity
            {
                UserId = userId,
                UserName = email.Split('@')[0], // Extract name from email
                Email = email,
                RevitUserName = Environment.UserName,
                CompanyId = companyId,
                CompanyName = companyName,
                IsCompanyAdmin = true,
                IsProjectAdmin = false,
                IsAuthorized = true,
                LastUpdated = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Create an authenticated project admin user
        /// </summary>
        public static UserIdentity CreateProjectAdmin(string userId, string email, string companyId, string companyName, List<string> projectIds)
        {
            return new UserIdentity
            {
                UserId = userId,
                UserName = email.Split('@')[0],
                Email = email,
                RevitUserName = Environment.UserName,
                CompanyId = companyId,
                CompanyName = companyName,
                IsCompanyAdmin = false,
                IsProjectAdmin = true,
                ProjectIds = projectIds ?? new List<string>(),
                IsAuthorized = true,
                LastUpdated = DateTime.UtcNow
            };
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
