using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BIManage.Addons.Services
{
    /// <summary>
    /// Retrieves the Autodesk SSO OAuth2 access token from Revit's SSONET.dll via reflection.
    /// Caches the token for 50 minutes (JWT lifetime is ~60 min).
    /// </summary>
    internal static class RevitSsoTokenProvider
    {
        private static string _cached;
        private static DateTime _expires = DateTime.MinValue;

        public static string GetAccessToken()
        {
            if (_cached != null && DateTime.UtcNow < _expires)
                return _cached;

            string revitRoot = Path.GetDirectoryName(
                Process.GetCurrentProcess().MainModule.FileName);
            Assembly ssoAsm = Assembly.LoadFrom(Path.Combine(revitRoot, "SSONET.dll"));

            object webSvc = ssoAsm.GetTypes()
                .First(t => t.FullName == "Autodesk.Revit.AdWebServicesBase")
                .GetMethod("GetInstance", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, null);

            string token = (string)webSvc.GetType()
                .GetMethod("GetOAuth2AccessToken")
                .Invoke(webSvc, null);

            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("User is not signed into Autodesk.");

            _cached = token;
            _expires = DateTime.UtcNow.AddMinutes(50);
            return token;
        }
    }
}
