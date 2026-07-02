using System;
using System.Threading.Tasks;

namespace BIManage.AI.Interfaces
{
    /// <summary>
    /// Thrown when the AI provider's API quota or rate limit is exceeded.
    /// </summary>
    public class AiQuotaExceededException : Exception
    {
        public AiQuotaExceededException()
            : base("AI usage limit reached. Please contact ZestineTech support.") { }
    }

    /// <summary>
    /// Thrown when the AI provider cannot be reached due to a network/connectivity issue
    /// (DNS failure, no internet, timeout, TLS handshake failure, etc.) — i.e. the request
    /// never got an HTTP response or a transport-level error prevented it.
    /// Distinct from auth/4xx/5xx responses, which are returned as error strings.
    /// </summary>
    public class AiNetworkUnavailableException : Exception
    {
        public AiNetworkUnavailableException(string message, Exception? inner = null)
            : base(message, inner) { }
    }

    /// <summary>
    /// Abstraction for AI chat providers.
    /// Each provider manages its own conversation history and API communication.
    /// </summary>
    public interface IAIProvider
    {
        /// <summary>
        /// Display name of the provider (e.g., "OpenAI").
        /// </summary>
        string ProviderName { get; }

        /// <summary>
        /// Sends a user message and returns the full AI response.
        /// The provider is responsible for maintaining conversation history.
        /// </summary>
        Task<string> SendMessageAsync(string userMessage);

        /// <summary>
        /// Sends a user message with token-by-token streaming.
        /// <paramref name="onToken"/> is called for each token as it arrives.
        /// Returns the full concatenated response when complete.
        /// Falls back to <see cref="SendMessageAsync"/> if streaming is not supported.
        /// </summary>
        Task<string> StreamMessageAsync(string userMessage, Action<string> onToken);

        /// <summary>
        /// Whether this provider supports streaming responses.
        /// </summary>
        bool SupportsStreaming { get; }

        /// <summary>
        /// Injects live model metrics data into the provider's system context.
        /// Call this BEFORE SendMessageAsync so the data is included in the next request.
        /// Pass null to clear any previously injected context.
        /// </summary>
        void SetModelContext(string? contextBlock);

        /// <summary>
        /// Clears the current conversation history.
        /// </summary>
        void ClearConversation();
    }
}
