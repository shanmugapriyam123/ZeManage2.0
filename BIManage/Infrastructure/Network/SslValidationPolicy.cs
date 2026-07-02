using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BIManage.Core.Features;

namespace BIManage.Infrastructure.Network
{
    /// <summary>
    ///     Centralizes SSL certificate validation policy.
    ///     Controlled by the "StrictSslValidation" feature flag (default: off).
    ///     When off: accepts all certificates (self-signed dev server).
    ///     When on: enforces proper TLS validation for production.
    /// </summary>
    public class SslValidationPolicy
    {
        private readonly IFeatureToggleService _features;

        public SslValidationPolicy(IFeatureToggleService features)
            => _features = features;

        /// <summary>
        ///     For <see cref="System.Net.Http.HttpClientHandler.ServerCertificateCustomValidationCallback"/>.
        /// </summary>
        public bool Validate(System.Net.Http.HttpRequestMessage msg,
            X509Certificate2? cert, X509Chain? chain, SslPolicyErrors errors)
        {
            if (!_features.IsFeatureEnabled("StrictSslValidation")) return true;
            return errors == SslPolicyErrors.None;
        }

        /// <summary>
        ///     For <see cref="System.Net.ServicePointManager.ServerCertificateValidationCallback"/>
        ///     (net48 global) and WebSocket connections.
        /// </summary>
        public bool ValidateLegacy(object? sender,
            System.Security.Cryptography.X509Certificates.X509Certificate? cert,
            System.Security.Cryptography.X509Certificates.X509Chain? chain,
            System.Net.Security.SslPolicyErrors errors)
        {
            if (!_features.IsFeatureEnabled("StrictSslValidation")) return true;
            return errors == System.Net.Security.SslPolicyErrors.None;
        }
    }
}
