using System;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Identity;
using BIManage.Common.Helpers;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.DependencyInjection;
using BIManage.Infrastructure.Logging;
using BIManageRevit.BIManage.Views.Bindings;

namespace BIManageRevit.Commands.RibbonCommands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class GenerateOtpCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            try
            {
                var app = BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                if (app == null)
                {
                    TaskDialog.Show("Error", "Application not initialized.");
                    return Result.Failed;
                }

                var services = app.GetType().GetField("_services",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as ServiceRegistry;

                var httpClient = services?.GetService<AuthenticatedHttpClient>();
                if (httpClient == null)
                {
                    TaskDialog.Show("Error", "Not connected to server. Please sign in first.");
                    return Result.Failed;
                }

                var logger = services?.GetService<ILogger>();

                if (!httpClient.IsAuthenticated)
                {
                    logger?.LogWarning("[OTP] HTTP client not authenticated - attempting token refresh");
                    var tokenManager = services?.GetService<AuthTokenManager>();
                    if (tokenManager != null)
                    {
                        try
                        {
                            // Try smart refresh first (admin-refresh for admin sessions, device-refresh for device)
                            var token = Task.Run(() => tokenManager.GetAccessTokenAsync()).GetAwaiter().GetResult();
                            if (token != null)
                            {
                                logger?.LogInfo($"[OTP] Token refresh succeeded (admin={tokenManager.IsAdminSession})");
                            }
                            else if (!tokenManager.IsAdminSession)
                            {
                                // Only fall back to device re-auth for non-admin sessions.
                                // Device re-auth would downgrade admin tokens (losing profileId).
                                logger?.LogInfo("[OTP] Refresh failed, attempting device re-authentication");
                                var authApi = services?.GetService<AuthApiService>();
                                if (authApi != null)
                                {
                                    var machineId = MachineIdentifier.GetMachineId(logger);
                                    var response = Task.Run(() => authApi.ValidateDeviceAsync(machineId)).GetAwaiter().GetResult();
                                    if (response?.AccessToken != null)
                                    {
                                        tokenManager.SetTokensFromDevice(response);
                                        logger?.LogInfo("[OTP] Device re-authenticated successfully");
                                    }
                                }
                            }
                            else
                            {
                                logger?.LogWarning("[OTP] Admin token refresh failed - user must sign in again");
                            }
                        }
                        catch (Exception authEx)
                        {
                            logger?.LogWarning($"[OTP] Re-auth failed: {authEx.Message}");
                        }
                    }

                    if (!httpClient.IsAuthenticated)
                    {
                        TaskDialog.Show("Authentication Required",
                            "Not authenticated with the server.\n\n" +
                            "Please sign in with your admin account first.");
                        return Result.Failed;
                    }
                }

                // Use authenticated user's display name (from login), not Revit username
                var userService = services?.GetService<IUserService>();
                var displayName = userService?.CurrentUser?.UserName
                    ?? userService?.CurrentUser?.Email
                    ?? Environment.UserName;
                var profileId = userService?.CurrentUser?.ProfileId;

                if (string.IsNullOrEmpty(profileId))
                {
                    // Local CurrentUser.ProfileId can be cleared independently of the admin
                    // access token — see SecureTokenStorage.ClearProfileId() called from
                    // AuthTokenManager.SetTokensFromDevice() when device re-auth runs while
                    // _isAdminSession=false. The admin token slot stays valid (which is why
                    // protection updates / event-protection PUTs continue to work even when
                    // local ProfileId is null), so before declaring the session expired,
                    // try to recover profileId from the admin JWT's claim.
                    var tokenManagerForOtp = services?.GetService<AuthTokenManager>();
                    if (tokenManagerForOtp != null && tokenManagerForOtp.IsAdminSession)
                    {
                        try
                        {
                            var adminToken = Task.Run(() => tokenManagerForOtp.GetAdminAccessTokenAsync()).GetAwaiter().GetResult();
                            if (!string.IsNullOrWhiteSpace(adminToken))
                            {
                                profileId = global::BIManage.Common.Helpers.JwtHelper.GetFirstClaim(
                                    adminToken, "profileId", "profile_id", "sub", "uid");
                                if (!string.IsNullOrWhiteSpace(profileId))
                                {
                                    logger?.LogInfo("[OTP] ProfileId recovered from admin JWT — admin session is still valid (local profile cache was cleared by a device re-auth).");
                                }
                            }
                        }
                        catch (Exception jwtEx)
                        {
                            logger?.LogDebug($"[OTP] Admin JWT profileId recovery failed: {jwtEx.Message}");
                        }
                    }
                }

                if (string.IsNullOrEmpty(profileId))
                {
                    logger?.LogWarning("[OTP] ProfileId is null AND admin JWT recovery failed — user is not signed in as admin");

                    // Offer to re-sign in. Themed confirmation dialog (replaces Revit's
                    // TaskDialog so the visual style matches the rest of BIManage).
                    var choice = global::BIManage.Views.Common.ZeConfirmDialog.ConfirmShow(
                        title: "Admin Session Required",
                        headline: "Your admin session has expired.",
                        message: "Generating an OTP requires an active admin session. " +
                                 "Please sign out and sign in again with your admin account.",
                        primaryText: "Sign in again",
                        secondaryText: "Cancel",
                        ownerHandle: commandData.Application.MainWindowHandle);


                    if (choice == global::BIManage.Views.Common.ZeConfirmResult.Primary)
                    {
                        try
                        {
                            // Log out the current (degraded) session
                            var secureStorage = services?.GetService<SecureTokenStorage>();
                            var tokenManagerForLogout = services?.GetService<AuthTokenManager>();
                            secureStorage?.ClearUserIdentity();
                            tokenManagerForLogout?.ClearTokens();
                            userService?.Logout();
                            logger?.LogInfo("[OTP] User logged out — opening Sign In dialog");

                            // Open Sign In dialog
                            var authApi = services?.GetService<AuthApiService>();
                            var sessionSyncService = services?.GetService<SessionSyncService>();
                            var revitContext = services?.GetService<global::BIManage.Revit.Context.IRevitContext>();
                            var sessionId = revitContext?.SessionId.ToString();

                            if (authApi != null && tokenManagerForLogout != null)
                            {
                                var signInDialog = new BIManageRevit.BIManage.Views.Auth.SignInDialog(
                                    authApi, tokenManagerForLogout, logger, userService,
                                    secureStorage, sessionSyncService, sessionId);
                                new System.Windows.Interop.WindowInteropHelper(signInDialog)
                                { Owner = commandData.Application.MainWindowHandle };
                                signInDialog.ShowDialog();

                                // Refresh ribbon visibility after sign-in
                                try
                                {
                                    var appInst = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                                    var visManager = appInst?.GetType().GetField("_ribbonVisibilityManager",
                                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                                        ?.GetValue(appInst) as global::BIManage.Revit.Applications.RibbonVisibilityManager;
                                    visManager?.UpdateAllButtonVisibility();
                                }
                                catch { }

                                // Re-read ProfileId after sign-in and continue if successful
                                profileId = userService?.CurrentUser?.ProfileId;
                                displayName = userService?.CurrentUser?.UserName
                                    ?? userService?.CurrentUser?.Email
                                    ?? Environment.UserName;

                                if (string.IsNullOrEmpty(profileId))
                                {
                                    // User cancelled or sign-in failed — don't proceed with OTP
                                    return Result.Cancelled;
                                }
                            }
                            else
                            {
                                return Result.Failed;
                            }
                        }
                        catch (Exception reauthEx)
                        {
                            logger?.LogError($"[OTP] Re-sign-in flow failed: {reauthEx.Message}", reauthEx);
                            return Result.Failed;
                        }
                    }
                    else
                    {
                        return Result.Cancelled;
                    }
                }

                logger?.LogInfo($"[OTP] Opening dialog for user={displayName}, profileId={profileId}");

                var dialog = new OtpGenerateDialog(httpClient, displayName, logger, profileId);
                new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = commandData.Application.MainWindowHandle };
                dialog.ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                global::BIManage.Views.Common.ZeMessageBox.Show(
                    "OTP Generation Failed",
                    $"Could not generate the one-time password.\n\n{ex.Message}",
                    global::BIManage.Views.Common.ZeMessageType.Error);
                return Result.Failed;
            }
        }
    }
}
