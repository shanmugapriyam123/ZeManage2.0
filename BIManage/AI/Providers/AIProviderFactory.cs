using BIManage.AI.Interfaces;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;

namespace BIManage.AI.Providers
{
    /// <summary>
    /// Creates the IAIProvider instance.
    /// Currently supports OpenAI only. Additional providers can be added later.
    /// </summary>
    public static class AIProviderFactory
    {
        /// <summary>
        /// Creates the configured provider.
        /// <paramref name="backendHttpClient"/> is only used when
        /// <see cref="AIProviderConfig.UseBackendProxy"/> is true — the provider then routes
        /// chat requests through the ZeManage backend (which holds the OpenAI key in Azure
        /// Key Vault). When the flag is false, this parameter is ignored and the provider
        /// calls OpenAI directly.
        /// </summary>
        public static IAIProvider Create(
            AIProviderConfig config,
            IKnowledgeProvider knowledge,
            ILogger? logger = null,
            AuthenticatedHttpClient? backendHttpClient = null)
        {
            return new OpenAIProvider(config, knowledge, logger, backendHttpClient);
        }
    }
}
