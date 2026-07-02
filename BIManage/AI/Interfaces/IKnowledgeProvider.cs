using System.Collections.Generic;
using System.Threading.Tasks;
using BIManage.AI.Knowledge;

namespace BIManage.AI.Interfaces
{
    /// <summary>
    /// Abstraction for knowledge retrieval.
    /// Current implementation: loads from local JSON files.
    /// Future implementation: calls a remote API endpoint.
    /// </summary>
    public interface IKnowledgeProvider
    {
        /// <summary>
        /// Returns the system prompt / persona for the AI assistant.
        /// </summary>
        Task<string> GetSystemPromptAsync();

        /// <summary>
        /// Returns relevant knowledge chunks for the given user query.
        /// When <paramref name="sections"/> is null, all sections are searched (safe fallback).
        /// When <paramref name="sections"/> is empty, returns empty string (no knowledge needed).
        /// When <paramref name="sections"/> has entries, only those sections are searched.
        /// </summary>
        Task<string> GetRelevantKnowledgeAsync(string query, HashSet<KnowledgeSection>? sections = null);

        /// <summary>
        /// Returns true if the question attempts to bypass protections, uninstall the tool,
        /// or find loopholes. These are redirected to admin contact.
        /// </summary>
        Task<bool> IsRedirectedAsync(string question);

        /// <summary>
        /// Returns a redirect reply message for bypass/uninstall attempts.
        /// </summary>
        Task<string> GetRedirectReplyAsync();

        /// <summary>
        /// Returns true if the question is off-topic (not Revit/BIM related).
        /// </summary>
        Task<bool> IsOffTopicAsync(string question);

        /// <summary>
        /// Returns a random off-topic reply message.
        /// </summary>
        Task<string> GetOffTopicReplyAsync();
    }
}
