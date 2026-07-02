using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using BIManage.Core.Identity;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.Applications
{
    /// <summary>
    /// Manages ribbon button visibility based on user role.
    /// Uses ExternalEvent to update button visibility on the main Revit thread.
    ///
    /// Unlike IExternalCommandAvailability which only grays out buttons,
    /// this manager completely hides/shows buttons based on role.
    /// </summary>
    public class RibbonVisibilityManager
    {
        private readonly ILogger _logger;
        private readonly IUserService _userService;

        // Button registrations by required role
        private readonly List<(PushButton Button, UserRole MinimumRole)> _roleRestrictedButtons = new();

        // External event for thread-safe visibility updates
        private ExternalEvent _updateVisibilityEvent;
        private UpdateVisibilityHandler _updateVisibilityHandler;

        public RibbonVisibilityManager(ILogger logger, IUserService userService)
        {
            _logger = logger;
            _userService = userService;
        }

        /// <summary>
        /// Initialize the external event handler.
        /// Must be called from the main Revit thread during Application startup.
        /// </summary>
        public void Initialize()
        {
            _updateVisibilityHandler = new UpdateVisibilityHandler(this, _logger);
            _updateVisibilityEvent = ExternalEvent.Create(_updateVisibilityHandler);

            // Subscribe to role changes
            if (_userService != null)
            {
                _userService.RoleChanged += OnRoleChanged;
            }

            _logger?.LogInfo("RibbonVisibilityManager initialized");
        }

        /// <summary>
        /// Register a button that should only be visible to admins (Company or Project).
        /// </summary>
        public void RegisterAdminOnlyButton(PushButton button)
        {
            if (button == null) return;
            _roleRestrictedButtons.Add((button, UserRole.ProjectAdministrator));
            _logger?.LogDebug($"Registered admin-only button: {button.Name}");
        }

        /// <summary>
        /// Register a button that should only be visible to Company Admins.
        /// </summary>
        public void RegisterCompanyAdminOnlyButton(PushButton button)
        {
            if (button == null) return;
            _roleRestrictedButtons.Add((button, UserRole.CompanyAdministrator));
            _logger?.LogDebug($"Registered company-admin-only button: {button.Name}");
        }

        /// <summary>
        /// Register a button with a specific minimum role requirement.
        /// </summary>
        public void RegisterButton(PushButton button, UserRole minimumRole)
        {
            if (button == null) return;
            _roleRestrictedButtons.Add((button, minimumRole));
            _logger?.LogDebug($"Registered button '{button.Name}' with minimum role: {minimumRole}");
        }

        /// <summary>
        /// Update all registered button visibility based on current role.
        /// Called internally by the ExternalEvent handler.
        /// </summary>
        internal void UpdateAllButtonVisibility()
        {
            if (_userService == null) return;

            var currentRole = _userService.CurrentRole;
            var isCompanyAdmin = _userService.IsCompanyAdmin;
            var isProjectAdmin = _userService.IsProjectAdmin;
            var hasAdminPrivileges = _userService.HasAdminPrivileges;

            _logger?.LogInfo($"Updating button visibility for role: {currentRole} (CompanyAdmin: {isCompanyAdmin}, ProjectAdmin: {isProjectAdmin})");

            foreach (var (button, minimumRole) in _roleRestrictedButtons)
            {
                try
                {
                    bool shouldBeVisible = minimumRole switch
                    {
                        UserRole.CompanyAdministrator => isCompanyAdmin,
                        UserRole.ProjectAdministrator => hasAdminPrivileges, // Company or Project admin
                        UserRole.NormalUser => true, // Always visible
                        _ => true
                    };

                    button.Visible = shouldBeVisible;
                    _logger?.LogDebug($"Button '{button.Name}' visibility: {shouldBeVisible}");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to update visibility for button '{button.Name}': {ex.Message}");
                }
            }

            _logger?.LogInfo($"Updated visibility for {_roleRestrictedButtons.Count} buttons");
        }

        /// <summary>
        /// Trigger a visibility update. Can be called from any thread.
        /// </summary>
        public void RefreshVisibility()
        {
            _updateVisibilityEvent?.Raise();
        }

        /// <summary>
        /// Handle role change events.
        /// </summary>
        private void OnRoleChanged(object sender, UserRoleChangedEventArgs e)
        {
            _logger?.LogInfo($"Role changed from {e.OldRole} to {e.NewRole} - triggering visibility update");
            RefreshVisibility();
        }

        /// <summary>
        /// Clean up subscriptions.
        /// </summary>
        public void Shutdown()
        {
            if (_userService != null)
            {
                _userService.RoleChanged -= OnRoleChanged;
            }
            _roleRestrictedButtons.Clear();
            _logger?.LogInfo("RibbonVisibilityManager shutdown");
        }

        /// <summary>
        /// External event handler for updating button visibility on the main thread.
        /// </summary>
        private class UpdateVisibilityHandler : IExternalEventHandler
        {
            private readonly RibbonVisibilityManager _manager;
            private readonly ILogger _logger;

            public UpdateVisibilityHandler(RibbonVisibilityManager manager, ILogger logger)
            {
                _manager = manager;
                _logger = logger;
            }

            public void Execute(UIApplication app)
            {
                try
                {
                    _manager.UpdateAllButtonVisibility();
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"Error updating button visibility: {ex.Message}", ex);
                }
            }

            public string GetName() => "UpdateRibbonVisibility";
        }
    }
}
