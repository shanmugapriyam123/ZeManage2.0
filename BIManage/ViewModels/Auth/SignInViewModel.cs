using System;
using System.Net.Http;
using System.Threading.Tasks;
using BIManage.Common.Helpers;
using BIManage.Core.Identity;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Auth.Models;
using BIManage.Infrastructure.Logging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BIManage.ViewModels.Auth
{
    public class SignInViewModel : ObservableObject
    {
        private readonly AuthApiService _authApi;
        private readonly AuthTokenManager _tokenManager;
        private readonly IUserService? _userService;
        private readonly SecureTokenStorage? _secureStorage;
        private readonly ILogger? _logger;
        private readonly SessionSyncService? _sessionSyncService;
        private readonly string? _sessionId;

        private string _email = string.Empty;
        private string _statusMessage = string.Empty;
        private bool _hasError;
        private bool _isBusy;
        private bool _isAuthenticated;
        private string _authenticatedUser = string.Empty;
        private string _roleDisplay = string.Empty;
        private string _companyDisplay = string.Empty;

        public string Email
        {
            get => _email;
            set
            {
                SetProperty(ref _email, value);
                ClearStatus();
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool HasError
        {
            get => _hasError;
            set => SetProperty(ref _hasError, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        public bool IsAuthenticated
        {
            get => _isAuthenticated;
            set => SetProperty(ref _isAuthenticated, value);
        }

        public string AuthenticatedUser
        {
            get => _authenticatedUser;
            set => SetProperty(ref _authenticatedUser, value);
        }

        /// <summary>
        /// Current role display text (e.g., "Company Admin", "Project Admin")
        /// </summary>
        public string RoleDisplay
        {
            get => _roleDisplay;
            set => SetProperty(ref _roleDisplay, value);
        }

        /// <summary>
        /// Company name for display
        /// </summary>
        public string CompanyDisplay
        {
            get => _companyDisplay;
            set => SetProperty(ref _companyDisplay, value);
        }

        public bool Success { get; private set; }

        public SignInViewModel(AuthApiService authApi, AuthTokenManager tokenManager, ILogger? logger = null, IUserService? userService = null, SecureTokenStorage? secureStorage = null, SessionSyncService? sessionSyncService = null, string? sessionId = null)
        {
            _authApi = authApi;
            _tokenManager = tokenManager;
            _logger = logger;
            _userService = userService;
            _secureStorage = secureStorage;
            _sessionSyncService = sessionSyncService;
            _sessionId = sessionId;

            // Check current auth state - restore from persisted identity
            if (userService?.IsAuthenticated == true)
            {
                IsAuthenticated = true;
                var user = userService.CurrentUser;
                AuthenticatedUser = user?.UserName ?? user?.Email ?? "Unknown";
                Email = user?.Email ?? string.Empty;
                CompanyDisplay = user?.CompanyName ?? string.Empty;

                // Prefer the server's own stored role text (e.g. "Proj"); fall back to the
                // boolean-derived label only if nothing was ever persisted. See SignInAsync
                // for why the raw text is shown rather than a normalized literal.
                var storedIdentity = secureStorage?.LoadUserIdentity();
                RoleDisplay = !string.IsNullOrEmpty(storedIdentity?.RoleName)
                    ? storedIdentity.RoleName
                    : GetRoleDisplayName(user);

                // Populate user display name cache for current user
                if (!string.IsNullOrWhiteSpace(user?.ProfileId) && !string.IsNullOrWhiteSpace(user?.UserName))
                {
                    UserDisplayNameCache.Set(user.ProfileId, user.UserName);
                    _logger?.LogDebug($"Restored user cache: {user.ProfileId} → {user.UserName}");
                }
            }
        }

        public async Task<bool> SignInAsync(string password)
        {
            if (string.IsNullOrWhiteSpace(Email))
            {
                SetError("Please enter your email address.");
                return false;
            }

            if (!IsValidEmail(Email))
            {
                SetError("Please enter a valid email address.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                SetError("Please enter your password.");
                return false;
            }

            IsBusy = true;
            ClearStatus();

            try
            {
                var machineId = MachineIdentifier.GetMachineId(_logger);
                _logger?.LogInfo($"Sign in attempt for: {Email} on device: {machineId}");

                // admin-login requires this Revit session to already exist server-side
                // (it resolves UserId via RevitSessions.SessionId). On a fresh launch the
                // session-open sync can still be in flight in the background, so a login
                // attempted too quickly used to fail with "Session not found or has no
                // UserId" even though the session would have synced a moment later. Do a
                // direct, synchronous sync attempt right here instead of hoping the
                // background timer/immediate-trigger already won the race — this is a real
                // HTTP call, not the queued/offline path, so it resolves in one round trip
                // when the server is reachable.
                if (_sessionSyncService != null && !string.IsNullOrEmpty(_sessionId)
                    && !_sessionSyncService.IsSessionConfirmedOnServer(_sessionId))
                {
                    _logger?.LogInfo($"Session {_sessionId} not yet confirmed — syncing before admin-login");
                    await _sessionSyncService.SyncSessionAsync(_sessionId);
                }

                var response = await _authApi.AdminLoginAsync(machineId, _sessionId ?? string.Empty, Email.Trim(), password);

                if (response?.AccessToken != null)
                {
                    _tokenManager.SetTokensFromLogin(response);

                    var username = response.DisplayName ?? response.Email ?? Email;
                    var companyId = response.CompanyId ?? (response.Companies?.Count > 0 ? response.Companies[0].CompanyId : null);
                    var companyName = response.CompanyName ?? (response.Companies?.Count > 0 ? response.Companies[0].CompanyName : null);

                    // Parse roleName from admin-login API response. The server stores some
                    // roles as truncated codes (observed: RoleId "RLID006" → RoleName "Proj",
                    // not "Project Admin") rather than always the full descriptive text, so
                    // "company"/"project" must also match as a PREFIX of the role name (or
                    // vice versa), not only as a substring alongside "admin" — "proj" contains
                    // neither "project" nor "admin", so the old Contains-both check silently
                    // classified it as a plain User and the signed-in panel never appeared.
                    var roleName = response.RoleName?.Trim().ToLowerInvariant() ?? "";
                    var isCompanyAdmin = roleName.StartsWith("comp")
                        || (roleName.Contains("company") && roleName.Contains("admin"));
                    var isProjectAdmin = !isCompanyAdmin
                        && (roleName.StartsWith("proj")
                            || (roleName.Contains("project") && roleName.Contains("admin")));

                    // Deliberately NOT falling back to "any role containing the word 'admin'
                    // → Company Admin" — roles like "Superadmin" are a distinct internal tier,
                    // not a tenant Company/Project Admin, and must NOT get the admin ribbon
                    // panel or protection-bypass buttons. Only an explicit comp*/proj* (or
                    // "company"+"admin" / "project"+"admin") match counts as admin; anything
                    // else — including any other "...admin..." role name — is a normal user.

                    _logger?.LogInfo($"API roleName: \"{response.RoleName}\" → CompanyAdmin={isCompanyAdmin}, ProjectAdmin={isProjectAdmin}");

                    // Populate user display name cache
                    if (!string.IsNullOrWhiteSpace(response.ProfileId) && !string.IsNullOrWhiteSpace(username))
                    {
                        UserDisplayNameCache.Set(response.ProfileId, username);
                        _logger?.LogDebug($"Cached user: {response.ProfileId} → {username}");
                    }

                    // Update user identity and role
                    if (_userService != null)
                    {
                        var userIdentity = new UserIdentity
                        {
                            UserId = response.User?.UserId ?? username,
                            UserName = username,
                            Email = response.Email ?? response.User?.Email ?? Email,
                            RevitUserName = Environment.UserName,
                            CompanyId = companyId,
                            CompanyName = companyName,
                            ProfileId = response.ProfileId,
                            IsCompanyAdmin = isCompanyAdmin,
                            IsProjectAdmin = isProjectAdmin,
                            IsAuthorized = true,
                            AdminToken = response.AccessToken,
                            LastUpdated = DateTime.UtcNow
                        };

                        _userService.SetCurrentUser(userIdentity);

                        // Persist identity for auto-restore on next Revit startup
                        _secureStorage?.SaveUserIdentity(new StoredUserIdentity
                        {
                            UserId = userIdentity.UserId,
                            UserName = userIdentity.UserName,
                            Email = userIdentity.Email,
                            CompanyId = userIdentity.CompanyId,
                            CompanyName = userIdentity.CompanyName,
                            IsCompanyAdmin = isCompanyAdmin,
                            IsProjectAdmin = isProjectAdmin,
                            RoleName = response.RoleName,
                            ProfileId = response.ProfileId
                        });
                    }

                    IsAuthenticated = true;
                    AuthenticatedUser = username;
                    Email = response.Email ?? response.User?.Email ?? Email;
                    CompanyDisplay = companyName ?? string.Empty;
                    // Show the server's own role text verbatim (e.g. "Proj", "Company Admin")
                    // rather than a hardcoded label — the isCompanyAdmin/isProjectAdmin flags
                    // above already drive visibility/badge classification independently
                    // (SwitchView/UpdateRoleBadge in SignInDialog.xaml.cs use prefix matching,
                    // not an exact-text check), so this string only ever needs to be accurate,
                    // not normalized to a specific literal.
                    RoleDisplay = !string.IsNullOrWhiteSpace(response.RoleName)
                        ? response.RoleName
                        : (isCompanyAdmin ? "Company Admin" : isProjectAdmin ? "Project Admin" : "User");

                    HasError = false;
                    StatusMessage = "Signed in successfully";
                    Success = true;

                    _logger?.LogInfo($"Sign in successful: {username} (Role: {response.RoleName})");

                    return true;
                }
                else
                {
                    SetError("Sign in failed. Please check your credentials.");
                    _logger?.LogWarning("Sign in failed - no tokens returned");
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                // API returned an error — message is already user-friendly from AuthApiService
                SetError(ex.Message);
                _logger?.LogError($"Sign in API error: {ex.Message}", ex);
                return false;
            }
            catch (TaskCanceledException)
            {
                SetError("Connection timed out. Please check your network and try again.");
                _logger?.LogWarning("Sign in request timed out");
                return false;
            }
            catch (Exception ex)
            {
                var innerMsg = ex.InnerException?.Message ?? ex.Message;
                // Translate common network errors to user-friendly messages
                if (innerMsg.Contains("No connection could be made") ||
                    innerMsg.Contains("actively refused") ||
                    innerMsg.Contains("Unable to connect") ||
                    innerMsg.Contains("No such host"))
                {
                    SetError("Unable to reach the server. Please check your network connection.");
                }
                else
                {
                    SetError("An unexpected error occurred. Please try again.");
                }
                _logger?.LogError($"Sign in error: {innerMsg}", ex);
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Hard sign-out for the Sign In dialog's "Sign out" button. ALWAYS runs the
        /// destructive branch — clears admin tokens, device tokens, refresh token,
        /// identity.dat, in-memory user, display-name cache — and sets
        /// IsAuthenticated=false so the dialog flips back to the email/password form.
        /// Unlike the regular <see cref="SignOut"/>, this does NOT take the
        /// "admin-downgrade" shortcut that preserves the device session and leaves the
        /// signed-in panel visible with a different role card. Per user request the
        /// Sign Out button must always return to the sign-in form, never demote to a
        /// different account profile.
        /// </summary>
        public void ForceFullSignOut()
        {
            // Best-effort server admin-logout if we have an admin session — same
            // courtesy call SignOut() does for the downgrade path, so the server
            // can revoke the admin token. Fire-and-forget; network failures are
            // swallowed by AdminLogoutAsync so the local wipe always completes.
            if (_tokenManager.IsAdminSession && !string.IsNullOrEmpty(_sessionId))
            {
                var adminToken = _tokenManager.GetCachedAdminAccessToken();
                try { _ = _authApi.AdminLogoutAsync(_sessionId, adminToken); }
                catch { /* best-effort */ }
            }

            _tokenManager.ClearTokens();
            _secureStorage?.ClearUserIdentity();
            _userService?.Logout();
            UserDisplayNameCache.Clear();
            IsAuthenticated = false;
            AuthenticatedUser = string.Empty;
            RoleDisplay = string.Empty;
            CompanyDisplay = string.Empty;
            StatusMessage = "Signed out";
            HasError = false;
            Success = false;
            _logger?.LogInfo("User force-signed-out via Sign Out button — full token + identity wipe, dialog returns to sign-in form");
        }

        public void SignOut()
        {
            // Admin elevation present → DOWNGRADE only. Revoke the admin session on the
            // server, drop the admin token slot, demote the in-memory user to NormalUser.
            // Device refresh token, identity.dat, and the active heartbeats are preserved
            // so the user continues working without re-validating the machine. Mirrors
            // ForceLogoutListener.cs Mode="Downgrade" path.
            if (_tokenManager.IsAdminSession)
            {
                if (!string.IsNullOrEmpty(_sessionId))
                {
                    // Capture the admin token before ClearAdminTokens() wipes it — the
                    // /admin-logout endpoint requires it in Authorization (returns 401
                    // without). Fire-and-forget; AdminLogoutAsync swallows network errors
                    // so the local downgrade still completes if the server is unreachable.
                    var adminToken = _tokenManager.GetCachedAdminAccessToken();
                    _ = _authApi.AdminLogoutAsync(_sessionId, adminToken);
                }

                _tokenManager.ClearAdminTokens();
                _userService?.UpdateAdminFlags(isCompanyAdmin: false, isProjectAdmin: false);

                // Re-derive role display from the (now demoted) identity — the user
                // record is still populated with name/email/company; only the admin
                // flags changed.
                var user = _userService?.CurrentUser;
                RoleDisplay = GetRoleDisplayName(user);
                StatusMessage = "Signed out of admin role";
                HasError = false;

                _logger?.LogInfo(
                    "Admin signed out of elevation — device session preserved, " +
                    "demoted to NormalUser (mirrors SignalR ForceLogout/Downgrade).");
                return;
            }

            // No admin elevation in play → existing destructive full sign-out.
            // Device token + refresh token + identity.dat are all wiped; the user
            // will need to re-validate the device via license key or credentials
            // on next launch. This is the original behavior — kept intentionally
            // for non-admin sign-out which is a true "log me out" intent.
            _tokenManager.ClearTokens();
            _secureStorage?.ClearUserIdentity();
            _userService?.Logout();
            UserDisplayNameCache.Clear(); // Clear cached user display names
            IsAuthenticated = false;
            AuthenticatedUser = string.Empty;
            RoleDisplay = string.Empty;
            CompanyDisplay = string.Empty;
            StatusMessage = "Signed out";
            HasError = false;
            Success = false;
            _logger?.LogInfo("User signed out and cache cleared");
        }

        private static string GetRoleDisplayName(UserIdentity? user)
        {
            if (user == null) return "User";
            if (user.IsCompanyAdmin) return "Company Admin";
            if (user.IsProjectAdmin) return "Project Admin";
            return "User";
        }

        private static bool IsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            var trimmed = email.Trim();
            var atIndex = trimmed.IndexOf('@');
            if (atIndex <= 0) return false;
            var dotIndex = trimmed.LastIndexOf('.');
            return dotIndex > atIndex + 1 && dotIndex < trimmed.Length - 1;
        }

        private void SetError(string message)
        {
            HasError = true;
            StatusMessage = message;
        }

        private void ClearStatus()
        {
            HasError = false;
            StatusMessage = string.Empty;
        }
    }
}
