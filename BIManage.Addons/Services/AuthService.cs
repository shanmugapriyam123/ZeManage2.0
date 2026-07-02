namespace BIManage.Addons.Services
{
    /// <summary>
    /// Presents a Revit-scoped 3-legged token to all APS data calls.
    /// </summary>
    internal static class AuthService
    {
        public static string AccessToken => RevitSsoTokenProvider.GetAccessToken();
    }
}
