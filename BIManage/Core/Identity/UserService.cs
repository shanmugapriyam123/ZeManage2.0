using System;
using System.Collections.Generic;
using BIManage.Infrastructure.Logging;

namespace BIManage.Core.Identity
{
    /// <summary>
    /// Implementation of user identity and authentication service
    /// Manages current user context and role changes
    /// </summary>
    public class UserService : IUserService
    {
        private UserIdentity _currentUser;
        private readonly ILogger _logger;

        public UserService(ILogger logger)
        {
            _logger = logger;

            // Initialize with Windows user identity (unauthenticated)
            _currentUser = UserIdentity.CreateWindowsUser();
            RoleFlagStore.SetFlags(false, false);
            _logger?.LogInfo($"UserService initialized with Windows user: {_currentUser.UserName}");
        }

        /// <summary>
        /// Get the current authenticated user
        /// </summary>
        public UserIdentity CurrentUser => _currentUser;

        /// <summary>
        /// Check if user is authenticated (logged in)
        /// </summary>
        public bool IsAuthenticated => _currentUser?.IsAuthorized == true;

        /// <summary>
        /// Check if current user is a company administrator
        /// </summary>
        public bool IsCompanyAdmin => _currentUser?.IsCompanyAdmin ?? false;

        /// <summary>
        /// Check if current user is a project administrator
        /// </summary>
        public bool IsProjectAdmin => _currentUser?.IsProjectAdmin ?? false;

        /// <summary>
        /// Check if current user has any admin privileges
        /// </summary>
        public bool HasAdminPrivileges => _currentUser?.HasAdminPrivileges ?? false;

        /// <summary>
        /// Get current user's role
        /// </summary>
        public UserRole CurrentRole => _currentUser?.CurrentRole ?? UserRole.NormalUser;

        /// <summary>
        /// Check if current user has admin privileges for a specific project
        /// </summary>
        public bool HasAdminPrivilegesForProject(string projectId)
        {
            if (_currentUser == null)
                return false;

            return _currentUser.HasAdminPrivilegesForProject(projectId);
        }

        /// <summary>
        /// Update user identity (typically after login)
        /// </summary>
        public void SetCurrentUser(UserIdentity user)
        {
            if (user == null)
            {
                _logger?.LogWarning("Attempted to set null user identity, reverting to Windows user");
                Logout();
                return;
            }

            var oldRole = _currentUser?.CurrentRole ?? UserRole.NormalUser;
            var oldUser = _currentUser;

            _currentUser = user;
            _currentUser.LastUpdated = DateTime.UtcNow;
            RoleFlagStore.SetFlags(user.IsCompanyAdmin, user.IsProjectAdmin);

            _logger?.LogInfo($"User identity updated: {user.Email ?? user.UserName} (Role: {user.CurrentRole})");

            // Raise role changed event if role changed
            if (oldRole != user.CurrentRole)
            {
                _logger?.LogInfo($"User role changed: {oldRole} → {user.CurrentRole}");
                OnRoleChanged(new UserRoleChangedEventArgs
                {
                    OldRole = oldRole,
                    NewRole = user.CurrentRole,
                    User = user
                });
            }
        }

        /// <summary>
        /// Logout current user and revert to Windows user
        /// </summary>
        public void Logout()
        {
            var oldRole = _currentUser?.CurrentRole ?? UserRole.NormalUser;
            var oldUserName = _currentUser?.UserName;

            _currentUser = UserIdentity.CreateWindowsUser();
            RoleFlagStore.SetFlags(false, false);

            _logger?.LogInfo($"User logged out: {oldUserName} → Windows user");

            // Raise role changed event (likely changed to NormalUser)
            if (oldRole != UserRole.NormalUser)
            {
                _logger?.LogInfo($"User role changed: {oldRole} → {UserRole.NormalUser}");
                OnRoleChanged(new UserRoleChangedEventArgs
                {
                    OldRole = oldRole,
                    NewRole = UserRole.NormalUser,
                    User = _currentUser
                });
            }
        }

        /// <summary>
        /// Update admin flags for current user
        /// </summary>
        public void UpdateAdminFlags(bool isCompanyAdmin, bool isProjectAdmin, List<string> projectIds = null)
        {
            if (_currentUser == null)
            {
                _logger?.LogWarning("Cannot update admin flags: no current user");
                return;
            }

            var oldRole = _currentUser.CurrentRole;

            _currentUser.IsCompanyAdmin = isCompanyAdmin;
            _currentUser.IsProjectAdmin = isProjectAdmin;
            RoleFlagStore.SetFlags(isCompanyAdmin, isProjectAdmin);

            if (projectIds != null)
            {
                _currentUser.ProjectIds = projectIds;
            }

            _currentUser.LastUpdated = DateTime.UtcNow;

            _logger?.LogInfo($"Admin flags updated: CompanyAdmin={isCompanyAdmin}, ProjectAdmin={isProjectAdmin}");

            // Raise role changed event if role changed
            if (oldRole != _currentUser.CurrentRole)
            {
                _logger?.LogInfo($"User role changed via admin flags: {oldRole} → {_currentUser.CurrentRole}");
                OnRoleChanged(new UserRoleChangedEventArgs
                {
                    OldRole = oldRole,
                    NewRole = _currentUser.CurrentRole,
                    User = _currentUser
                });
            }
        }

        /// <summary>
        /// Event raised when user role changes
        /// </summary>
        public event EventHandler<UserRoleChangedEventArgs> RoleChanged;

        protected virtual void OnRoleChanged(UserRoleChangedEventArgs e)
        {
            RoleChanged?.Invoke(this, e);
            _logger?.LogDebug($"RoleChanged event raised: {e.OldRole} → {e.NewRole}");
        }
    }
}
