namespace BIManage.AI.Interfaces
{
    /// <summary>
    /// Configuration for selecting and configuring the AI provider.
    /// Read from App.config or environment variables.
    /// </summary>
    public class AIProviderConfig
    {
        /// <summary>
        /// Provider type: "openai".
        /// </summary>
        public string Provider { get; set; } = "openai";

        /// <summary>
        /// API key for the selected provider.
        /// </summary>
        public string? ApiKey { get; set; }

        /// <summary>
        /// Model identifier (e.g., "gpt-4o-mini", "gpt-4o").
        /// </summary>
        public string? Model { get; set; }

        /// <summary>
        /// Optional API base URL override for self-hosted endpoints.
        /// </summary>
        public string? BaseUrl { get; set; }

        /// <summary>
        /// When true, the provider posts chat requests to the ZeManage backend's AI proxy
        /// endpoint instead of calling OpenAI directly. The backend pulls the OpenAI key
        /// from Azure Key Vault and forwards the request, so no key ever ships in the DLL.
        ///
        /// Wire-up cost: the backend must expose POST /api/v1/ai/chat (and /chat/stream)
        /// that accepts the OpenAI request body verbatim and returns OpenAI's response
        /// unchanged. Until that endpoint exists, leave this false and the provider falls
        /// back to direct OpenAI calls with the locally-configured ApiKey.
        ///
        /// Read from App.config key "AI:UseBackendProxy" (default false).
        /// </summary>
        public bool UseBackendProxy { get; set; } = false;
    }
}
