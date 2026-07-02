using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BIManage.AI.Interfaces;
using BIManage.AI.Knowledge;
using BIManage.AI.Terminal;
using BIManage.AI.Terminal.OpenAi;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;
using BIManage.Models.AI;

namespace BIManage.AI.Providers
{
    /// <summary>
    /// OpenAI Chat Completions provider (gpt-4o-mini, gpt-4o, etc.).
    /// Uses a sliding-window history and local intent-routing for knowledge injection.
    /// </summary>
    public class OpenAIProvider : IAIProvider
    {
        // ─────────────────────────────────────────────────────────────────────────────
        // INTERNAL-BUILD HARDCODED KEY — last-resort fallback when App.config and the
        // OPENAI_API_KEY env var are both empty. PASTE YOUR KEY BETWEEN THE QUOTES BELOW.
        //
        //   ⚠ DO NOT COMMIT THIS FILE WITH A REAL KEY.
        //   ⚠ DO NOT SHIP A BUILD CONTAINING A REAL KEY TO EXTERNAL CUSTOMERS — the DLL
        //     is trivially decompilable with ILSpy/dnSpy and the key will be extracted.
        //   ⚠ Internal Zestine builds only. Rotate immediately if this DLL leaves a
        //     trusted machine.
        //
        // When you ship to external testers/customers, switch to backend-proxy auth and
        // empty this constant again.
        // ─────────────────────────────────────────────────────────────────────────────
        private const string HardcodedApiKey = "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";

        private readonly string? _apiKey;
        private readonly string _model;
        private readonly List<ConversationMessage> _conversationHistory;
        private readonly IKnowledgeProvider _knowledge;
        private readonly ILogger? _logger;

        // Backend-proxy mode: when enabled, all chat requests go through the ZeManage backend
        // (which holds the OpenAI key in Azure Key Vault) instead of calling OpenAI directly.
        // _apiKey is then unused for transport — the AuthenticatedHttpClient attaches the
        // user's JWT instead. Set via AIProviderConfig.UseBackendProxy + a backend HttpClient
        // injected by the factory. When _useBackendProxy is false, behaviour is identical to
        // before — direct OpenAI calls with the locally-configured key.
        private readonly bool _useBackendProxy;
        private readonly AuthenticatedHttpClient? _backendHttp;

        // Relative path on the backend that mirrors OpenAI's /v1/chat/completions surface.
        // Backend forwards the body verbatim to OpenAI and returns the response unchanged
        // (including streaming SSE chunks for the /stream variant). See BACKEND_AI_PROXY_SPEC.md
        // for the exact contract.
        private const string ProxyChatPath = "/api/v1/ai/chat";
        private const string ProxyChatStreamPath = "/api/v1/ai/chat/stream";

        // Sliding window: only last N messages sent to the API. Bumped from 6 → 12 so
        // multi-turn references like "give me the id" still know what the user is talking
        // about. Each message is also compressed to HistoryAssistantMaxChars when long, so
        // 12 messages is still well inside gpt-4o-mini's input budget.
        private const int MaxHistoryMessages = 12;

        // Long assistant messages in history are compressed to this length
        private const int HistoryAssistantMaxChars = 300;

        // Live model context injected by ModelContextService before each send
        private string? _modelContextBlock;

        // OpenAI function-calling support (Phase 3c-NEW). When set, the provider includes
        // the registry's tool definitions in each request and routes tool_calls back through
        // the dispatcher. When null, behavior is identical to pre-Phase-3c — all existing
        // chat flows are unaffected.
        private ToolRegistry? _toolRegistry;
        private ToolDispatcher? _toolDispatcher;
        private List<OpenAiToolDefinition>? _toolDefinitions;

        // Hard cap on tool-call rounds per user turn. OpenAI may chain multiple tool calls
        // (tool A → result → tool B → result → final answer), but a runaway loop here would
        // burn tokens and time. Five rounds is plenty for any realistic Revit query.
        private const int MaxToolCallRounds = 5;

        public string ProviderName => "OpenAI";

        public OpenAIProvider(
            AIProviderConfig config,
            IKnowledgeProvider knowledge,
            ILogger? logger = null,
            AuthenticatedHttpClient? backendHttpClient = null)
        {
            // Fallback chain: App.config → OPENAI_API_KEY env var → hardcoded constant.
            // The hardcoded constant is the absolute last resort for internal builds when
            // neither of the safer sources is configured (see HardcodedApiKey notice above).
            // _keySource records which source actually supplied the key so the startup log
            // makes it unambiguous what's about to be used (questions like "is it still
            // reading the hardcoded one?" come up every cutover).
            string keySource;
            if (!string.IsNullOrWhiteSpace(config.ApiKey))
            {
                _apiKey = config.ApiKey;
                keySource = "App.config (AI:ApiKey)";
            }
            else if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
            {
                _apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                keySource = "OPENAI_API_KEY env var";
            }
            else if (!string.IsNullOrWhiteSpace(HardcodedApiKey))
            {
                _apiKey = HardcodedApiKey;
                keySource = "HardcodedApiKey constant (DLL-baked, NOT SAFE for external builds)";
            }
            else
            {
                _apiKey = null;
                keySource = "(none — chat will fail unless proxy mode is on)";
            }
            _model  = config.Model  ?? "gpt-4o-mini";
            _knowledge = knowledge;
            _logger    = logger;
            _conversationHistory = new List<ConversationMessage>();

            // Backend-proxy mode is a runtime decision. We only flip on the proxy if BOTH the
            // config flag is true AND a backend HttpClient was provided — opting in via config
            // without wiring up the HttpClient would silently fall back to direct OpenAI calls,
            // which is the opposite of what someone setting the flag wants. Logging the
            // mismatch makes the misconfiguration visible.
            if (config.UseBackendProxy && backendHttpClient != null)
            {
                _useBackendProxy = true;
                _backendHttp = backendHttpClient;
            }
            else
            {
                _useBackendProxy = false;
                _backendHttp = null;
                if (config.UseBackendProxy && backendHttpClient == null)
                {
                    _logger?.LogWarning(
                        "[OpenAIProvider] AI:UseBackendProxy=true but no AuthenticatedHttpClient " +
                        "was supplied — falling back to direct OpenAI calls. Wire the client " +
                        "through AIProviderFactory.Create(...) to enable the proxy.");
                }
            }

            // Final one-stop diagnostic line. Read this and you know exactly what's about to
            // happen — no need to cross-reference the config file separately.
            // Examples:
            //   [OpenAIProvider] Mode=DIRECT-OPENAI  Key=HardcodedApiKey constant (...)  Model=gpt-4o-mini
            //   [OpenAIProvider] Mode=BACKEND-PROXY  Key=(unused — backend pulls from Azure Key Vault)  Model=gpt-4o-mini
            var mode = _useBackendProxy ? "BACKEND-PROXY" : "DIRECT-OPENAI";
            var keyDescription = _useBackendProxy
                ? "(unused — backend pulls from Azure Key Vault)"
                : keySource;
            _logger?.LogInfo($"[OpenAIProvider] Mode={mode}  Key={keyDescription}  Model={_model}");
            _logger?.LogDebug($"[OpenAIProvider] API Key loaded: {(!string.IsNullOrWhiteSpace(_apiKey) ? "YES" : "NO")}");
        }

        /// <summary>
        /// Wires up OpenAI function calling. Once a registry is set, every chat request
        /// includes the tool definitions, and any tool_calls returned by the model are
        /// dispatched and their results fed back until the model produces a final answer.
        /// Pass null to disable tool calling and revert to plain chat behavior.
        /// </summary>
        /// <remarks>
        /// Idempotent — safe to call repeatedly. Tool definitions are rebuilt each call so
        /// the registry can be replaced (e.g. when a feature flag changes the tool set).
        /// </remarks>
        public void SetToolRegistry(ToolRegistry? registry)
        {
            _toolRegistry = registry;
            if (registry == null)
            {
                _toolDispatcher = null;
                _toolDefinitions = null;
                _logger?.LogDebug("[OpenAIProvider] Tool calling disabled");
                return;
            }

            _toolDispatcher = new ToolDispatcher(registry, _logger);
            _toolDefinitions = ToolSchemaBuilder.Build(registry);
            _logger?.LogDebug($"[OpenAIProvider] Tool calling enabled with {_toolDefinitions.Count} tool(s): {string.Join(", ", registry.ToolNames)}");
        }

        /// <summary>True when a tool registry is configured and tool calling is active.</summary>
        public bool ToolCallingEnabled => _toolDispatcher != null && _toolDefinitions is { Count: > 0 };

        /// <summary>
        /// Returns the full URL the chat endpoint should be POSTed to, based on transport mode.
        /// Direct mode → OpenAI. Proxy mode → the ZeManage backend (which holds the key in
        /// Azure Key Vault and forwards the request).
        /// </summary>
        private string GetChatUrl(bool streaming)
        {
            if (_useBackendProxy && _backendHttp != null)
            {
                var baseUrl = _backendHttp.BaseUrl.TrimEnd('/');
                var path = streaming ? ProxyChatStreamPath : ProxyChatPath;
                return baseUrl + path;
            }
            return "https://api.openai.com/v1/chat/completions";
        }

        /// <summary>
        /// Attaches the correct Authorization header to <paramref name="http"/> for this turn.
        /// Direct mode → "Bearer sk-…" using the locally-configured OpenAI key. Proxy mode →
        /// "Bearer &lt;JWT&gt;" using the user's current access token (which the backend
        /// validates before forwarding to OpenAI). The JWT is fetched fresh per call so a
        /// recent token-refresh is picked up without rebuilding the HttpClient.
        /// </summary>
        private async Task AttachChatAuthAsync(HttpClient http)
        {
            if (_useBackendProxy && _backendHttp != null)
            {
                var jwt = await _backendHttp.GetCurrentAccessTokenAsync();
                if (string.IsNullOrEmpty(jwt))
                {
                    throw new AiNetworkUnavailableException(
                        "You're signed out. Sign in to ZeManage and try again.");
                }
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", jwt);
            }
            else
            {
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _apiKey);
            }
        }

        /// <summary>
        /// Sends an HTTP POST with automatic retry on transient errors (429, 5xx).
        /// Retries up to 3 times with increasing delays: immediate, 1s, 3s.
        /// Takes the raw JSON body so we can re-wrap it in a fresh <see cref="StringContent"/>
        /// for each attempt — HttpClient disposes the content stream after a POST, so reusing
        /// the same instance across retries throws ObjectDisposedException on attempt 2+.
        /// </summary>
        private async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient http, string url, string requestJson)
        {
            int[] delaysMs = { 0, 1000, 3000 };
            HttpResponseMessage? response = null;
            Exception? lastTransportException = null;

            for (int attempt = 0; attempt < delaysMs.Length; attempt++)
            {
                if (attempt > 0)
                {
                    _logger?.LogWarning($"[OpenAIProvider] Attempt {attempt + 1} after {delaysMs[attempt]}ms delay...");
                    await Task.Delay(delaysMs[attempt]);
                }

                try
                {
                    // Build fresh content per attempt. Re-using the StringContent from a prior
                    // attempt throws ObjectDisposedException because HttpClient disposes the
                    // request body stream once it's been sent.
                    using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                    response = await http.PostAsync(url, content);
                    lastTransportException = null;
                }
                catch (HttpRequestException ex)
                {
                    // DNS failure, refused connection, TLS handshake failure, etc. The request never
                    // reached OpenAI. Worth retrying briefly, but if it keeps failing we surface a
                    // typed network exception so the ViewModel can show a friendly message.
                    lastTransportException = ex;
                    _logger?.LogWarning($"[OpenAIProvider] Attempt {attempt + 1} transport failure: {ex.Message}");
                    continue;
                }
                catch (TaskCanceledException ex)
                {
                    // HttpClient timeout — also a transport-level failure for our purposes.
                    lastTransportException = ex;
                    _logger?.LogWarning($"[OpenAIProvider] Attempt {attempt + 1} timed out: {ex.Message}");
                    continue;
                }

                if (response.IsSuccessStatusCode)
                    return response;

                var statusCode = (int)response.StatusCode;
                if (statusCode != 429 && statusCode < 500)
                    break;

                _logger?.LogWarning($"[OpenAIProvider] Attempt {attempt + 1} failed ({statusCode}), retrying...");
            }

            if (response == null && lastTransportException != null)
            {
                throw new AiNetworkUnavailableException(
                    "Could not reach the AI service. Check your internet connection.",
                    lastTransportException);
            }

            return response!;
        }

        public async Task<string> SendMessageAsync(string userMessage)
        {
            // Local key only matters when calling OpenAI directly. In proxy mode the backend
            // pulls the key from Azure Key Vault, so an empty _apiKey is expected and fine.
            if (!_useBackendProxy && string.IsNullOrWhiteSpace(_apiKey))
                return "API Key not configured. Please set your OpenAI API key in App.config (AI:ApiKey) or OPENAI_API_KEY environment variable.";

            try
            {
                _conversationHistory.Add(new ConversationMessage("user", userMessage));

                // Sliding window
                if (_conversationHistory.Count > MaxHistoryMessages)
                    _conversationHistory.RemoveRange(0, _conversationHistory.Count - MaxHistoryMessages);

                // Redirect check — block bypass/uninstall/loophole questions
                if (await _knowledge.IsRedirectedAsync(userMessage))
                {
                    var redirectReply = await _knowledge.GetRedirectReplyAsync();
                    _conversationHistory.Add(new ConversationMessage("assistant", redirectReply));
                    return redirectReply;
                }

                // Off-topic check — bypass when the query has already been validated by an
                // upstream classifier (DB query, visibility diagnosis, warning diagnosis).
                // Those paths inject _modelContextBlock with a known prefix and the user's
                // intent is no longer in question.
                bool intentAlreadyValidated = !string.IsNullOrEmpty(_modelContextBlock) &&
                    (_modelContextBlock.StartsWith("DATABASE QUERY RESULTS:", StringComparison.Ordinal) ||
                     _modelContextBlock.Contains("You have access to the local BIManage SQLite database") ||
                     _modelContextBlock.Contains("ELEMENT VISIBILITY DIAGNOSIS") ||
                     _modelContextBlock.Contains("WARNING RESOLUTION DIAGNOSIS"));

                if (!intentAlreadyValidated && await _knowledge.IsOffTopicAsync(userMessage))
                {
                    var snarkyReply = await _knowledge.GetOffTopicReplyAsync();
                    _conversationHistory.Add(new ConversationMessage("assistant", snarkyReply));
                    return snarkyReply;
                }

                // Classify intent → load only the sections this query needs
                var intents = IntentClassifier.Classify(userMessage);
                _logger?.LogDebug($"[OpenAIProvider] Intent sections: {(intents.Count == 0 ? "none" : string.Join(", ", intents))}");

                var relevantKnowledge = await _knowledge.GetRelevantKnowledgeAsync(userMessage, intents);
                var systemContext     = await _knowledge.GetSystemPromptAsync();

                bool hasZeManageKnowledge = !string.IsNullOrEmpty(relevantKnowledge)
                    && relevantKnowledge.Contains("[ZeManage:");

                var zeManageAttributionInstruction = hasZeManageKnowledge ? @"

PRODUCT ATTRIBUTION (MANDATORY when answering from [ZeManage: ...] entries):
- The feature being asked about is a ZeManage capability. Identify it as such.
- Begin the answer by naming the feature and stating it is part of ZeManage
  (e.g. ""Pin Protection is a ZeManage feature that..."" or ""ZeManage's Activity Tracker lets you..."").
- Never present these features as generic Revit functionality — they exist because ZeManage adds them.
- If the user asks ""what is X"" and X matches a [ZeManage: ...] entry, the answer must say so explicitly.
" : "";

                var knowledgeSection = !string.IsNullOrEmpty(relevantKnowledge)
                    ? $"\n\nRELEVANT KNOWLEDGE FROM DATABASE:\n{relevantKnowledge}\n{zeManageAttributionInstruction}"
                    : "";

                bool isVisibilityDiagnosis = !string.IsNullOrEmpty(_modelContextBlock) &&
                    (_modelContextBlock.Contains("ELEMENT VISIBILITY DIAGNOSIS") ||
                     _modelContextBlock.Contains("WARNING RESOLUTION DIAGNOSIS"));

                var modelDataSection = !string.IsNullOrEmpty(_modelContextBlock)
                    ? $"\n\n{_modelContextBlock}\n"
                    : "";

                var lower           = userMessage.ToLower();
                var wantsElaboration = lower.Contains("elaborate") ||
                                       lower.Contains("explain more") ||
                                       lower.Contains("tell me more") ||
                                       lower.Contains("give details") ||
                                       lower.Contains("explain") ||
                                       lower.Contains("how") ||
                                       lower.Contains("why");

                // Build system prompt
                bool isWarningDiagnosis = !string.IsNullOrEmpty(_modelContextBlock) &&
                    _modelContextBlock.Contains("WARNING RESOLUTION DIAGNOSIS");

                string visibilityInstruction;
                if (isWarningDiagnosis)
                {
                    visibilityInstruction = @"
========================================
OVERRIDE ALL INSTRUCTIONS BELOW — WARNING RESOLUTION MODE
========================================
A live WARNING RESOLUTION DIAGNOSIS has been computed directly from the Revit model.
Your response MUST be derived SOLELY from that diagnosis block.

1. Report the total warning count and model health rating.
2. List each warning category with count, sample warnings, affected elements.
3. Provide the FIX instruction for each category exactly as given.
4. End with the priority recommendation.

FORBIDDEN:
  - Do NOT invent warnings not in the diagnosis
  - Do NOT give generic advice beyond what the diagnosis says
  - Do NOT speculate about causes

";
                }
                else if (isVisibilityDiagnosis)
                {
                    visibilityInstruction = @"
========================================
OVERRIDE ALL INSTRUCTIONS BELOW — VISIBILITY DIAGNOSIS MODE
========================================
A live ELEMENT VISIBILITY DIAGNOSIS has been computed directly from the Revit model.
Your response MUST be derived SOLELY from that diagnosis block. NO exceptions.

MANDATORY FORMAT when 'ISSUES FOUND: None':
  Start with: ""All 17 visibility checks passed for [Element Name] (ID [ElementID]) in view '[ViewName]'. No standard visibility issue was detected.""
  Then say: ""However, here are additional things to check:""
  Then list ONLY the bullet points from the 'ADDITIONAL CHECKS TO SUGGEST' section verbatim.
  Do NOT add, invent, or speculate about any other causes.

MANDATORY FORMAT when 'ISSUES FOUND: N visibility problem(s) confirmed':
  Start with: ""I found [N] confirmed visibility issue(s) for [Element Name] (ID [ElementID]) in view '[ViewName]':""
  Then list each ISSUE and its FIX exactly as given in the diagnosis block.
  Do NOT add any other causes or guesses.

FORBIDDEN:
  - ""It looks like..."" or ""This could be..."" or ""potential reasons"" — BANNED
  - Any cause not explicitly listed in the diagnosis block
  - Speculation, guessing, or generic Revit advice

";
                }
                else
                {
                    visibilityInstruction = "";
                }

                // When tool calling is active, append a guidance block that helps the model
                // pick the right tool. Without this, it tends to reach for ai_element_filter
                // for everything and only fall back to send_code_to_revit when truly stuck —
                // which leaves more powerful one-shot Revit API queries on the table.
                string toolGuidance = ToolCallingEnabled ? @"

TOOL SELECTION GUIDANCE — read carefully before answering:

YOU MUST CALL A TOOL when the user asks about THEIR specific model. Generic Revit troubleshooting
advice (""press VG to check Visibility/Graphics"") is the wrong answer when you could inspect the
actual model and tell them EXACTLY what's wrong. Categories that REQUIRE a tool call:
  • ""How many X are in my model"" → ai_element_filter or send_code_to_revit
  • ""I can't see X in my view"" / ""why are X hidden"" → call get_current_view_info first, then
    send_code_to_revit to inspect ActiveView.GetCategoryHidden(...) for the specific category.
    DO NOT default to generic ""check Visibility/Graphics"" advice without investigating first.
  • ""What's the longest/largest/most common X"" → send_code_to_revit (aggregation)
  • ""What level is X on"" / ""where is X"" → send_code_to_revit (reads LevelId)
  • ""What did I select"" / ""these elements"" → get_selected_elements
  • ""What view am I in"" → get_current_view_info
  • ""View range"" / ""cut plane"" / ""can't see X in floor plan"" / ""fix view range"" → get_view_range
    (DO NOT try to write PlanViewRange code yourself — the dedicated tool returns level + offset
    for top, cut, bottom and view depth correctly.)
    THE TOOL ALREADY RETURNS BOTH FEET AND MILLIMETRES (LevelElevationMm, OffsetMm,
    EffectiveHeightMm, and a ready-made DisplayString). When the user asks for mm,
    QUOTE the *Mm fields verbatim — do NOT multiply OffsetFeet by 304.8 yourself.
    LLM unit-conversion arithmetic on long decimals is unreliable (observed 2026-05-26:
    identical 170.96 ft inputs produced ""1785 mm"", ""5200 mm"" and ""1768.70 mm""
    in the same session). Trust the tool's numbers.
  • ""What's in this model"" overview → analyze_model_statistics or list_categories_with_elements
  • ""List my rooms"" / ""room schedule"" → export_room_data
  • ""Material takeoff"" → get_material_quantities

TOOL CHOICE within the model-specific category:
- Simple category counts → ai_element_filter (cheap, fast)
- Custom queries / aggregations / parameter values → send_code_to_revit (most flexible)

When using send_code_to_revit, follow these rules to AVOID compiler errors:
- The wrapper imports System, System.Collections.Generic, System.Linq, Autodesk.Revit.DB, Autodesk.Revit.UI.
  DO NOT write your own `using` statements — they will be rejected by the safety analyzer.
- For type LENGTHS: use BuiltInParameter.CURVE_ELEM_LENGTH (NOT CURVE_LENGTH or DUCT_LENGTH).
- MEP types live in sub-namespaces: Autodesk.Revit.DB.Mechanical.Duct, .Plumbing.Pipe, .Electrical.Wire,
  .Architecture.Room. Reference them with the full path.
- Element.Location is the geometry: cast to LocationCurve and read .Curve.Length for curve-based elements.
- For ANY parameter you're not sure exists, use elem.LookupParameter(""Display Name"")?.AsDouble()
  as a robust fallback — Revit resolves both built-in and shared parameters by their display name.
- Return anonymous types: `return new { name = ..., id = ..., length = ... };`
- READ-ONLY ONLY: NO Transaction, NO Delete, NO Parameter.Set, NO IO/network/reflection.

When the COMPILER REJECTS your code, READ the error message carefully — it now includes
suggestions like ""use BuiltInParameter.CURVE_ELEM_LENGTH"". Apply the suggestion in the next round
INSTEAD of guessing a similar wrong name. If after 2 rounds you can't get it to compile, use
elem.LookupParameter() with the display name as a fallback.

NEVER call send_code_to_revit when a more specific tool would answer the question — it's a
fallback, not the first choice. But also NEVER refuse to call ANY tool just because you're unsure —
calling a wrong tool and reading the result is always better than giving generic advice.

REVIT API REMINDERS (high-frequency mistakes — read before writing code):
- PlanViewRange has NO GetCutPlane / GetBottom / GetTop / GetViewDepth / GetUpperLimit / GetLowerLimit
  / GetBottomLevelId / GetTopLevelId / CutOffset members. The real API is:
    var range = viewPlan.GetViewRange();
    range.GetLevelId(PlanViewPlane.TopClipPlane)    // also: CutPlane, BottomClipPlane, ViewDepthPlane, UnderlayBottom, UnderlayTop
    range.GetOffset(PlanViewPlane.CutPlane)
  But you should call the get_view_range tool instead — it returns everything pre-formatted.
- ElementId(int) is DEPRECATED in Revit 2024+; use new ElementId((long)id) or just call doc.GetElement(elementIdObj).
- Cast view to ViewPlan before GetViewRange() — base View doesn't have that method.

UNITS — DO NOT DO ARITHMETIC YOURSELF.
- All measurement tools (get_view_range, send_code_to_revit numeric returns, etc.) return
  values in Revit's internal feet AND pre-converted millimetres where applicable.
- When the user asks ""in mm"" / ""in metric"", QUOTE the *Mm field that already exists in the
  tool result. Do NOT multiply ft × 304.8 yourself.
- When the user asks ""in ft"", quote the *Feet field.
- If a tool only returned feet and the user wants mm, call the tool again or call
  send_code_to_revit to convert — never compute it in your head.
- Why: LLM arithmetic on long decimal feet values is unreliable. Same input has been observed
  to produce different mm outputs in the same session. Trust the precomputed numbers.

MULTI-TOOL SYNTHESIS — when you call multiple tools in one turn:
- TRUST THE MOST RECENT TOOL RESULT over earlier ones when they conflict. If
  get_health_alerts returns 0 alerts but ai_element_filter then finds 3 elements with
  warning ids, the filter result is the answer — the health snapshot may be stale.
- If the user asks a specific follow-up like ""give me the id"", look for IDs in EVERY
  prior tool result this turn (including ones called many rounds ago) before declaring
  ""I couldn't reach a final answer"".
- NEVER respond ""I called several tools but couldn't reach a final answer"" if any tool
  result contained data relevant to the user's question. Synthesize what you DO have and
  state explicitly what's missing.
" : "";

                var systemPrompt = $@"{visibilityInstruction}{systemContext}
{knowledgeSection}
{modelDataSection}{toolGuidance}
RESPONSE FORMATTING GUIDELINES:
- Use BULLET POINTS for listing steps, options, or multiple items
- Use NUMBERED LISTS (1., 2., 3.) for sequential steps or procedures
- Keep paragraphs SHORT and focused (2-3 sentences max)
- You CAN use **bold** for emphasis on important terms or warnings only
{(wantsElaboration
    ? "- User wants DETAILED explanation — provide comprehensive step-by-step guidance"
    : "- Keep response CONCISE (3-5 key points max)")}

RESPONSE STYLE GUIDELINES:
- Be CONVERSATIONAL and helpful, like a knowledgeable colleague
- Use specific Revit commands with locations (e.g., 'Manage tab > Warnings')
- ALWAYS complete your response — never stop mid-sentence

Always end with 2-3 relevant follow-up questions in this EXACT format (the UI parses it to build clickable pills, so the format must not vary):

You might also want to know:
1. <question one ending with ?>
2. <question two ending with ?>
3. <question three ending with ?>

Use numbered list (1. 2. 3.) — NOT bullets (• or -). Each question must end with '?'.";

                // Build OpenAI messages array
                var messages = new List<object>
                {
                    new { role = "system", content = systemPrompt }
                };

                // Add compressed history (all but the last user message)
                var historyToSend = _conversationHistory.Take(_conversationHistory.Count - 1).ToList();
                foreach (var msg in historyToSend)
                {
                    var content = msg.Role == "assistant" && msg.Content.Length > HistoryAssistantMaxChars
                        ? msg.Content.Substring(0, HistoryAssistantMaxChars) + "…"
                        : msg.Content;
                    messages.Add(new { role = msg.Role, content });
                }

                // Current user turn
                messages.Add(new { role = "user", content = userMessage });

                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(60); // Higher when tool calls may chain
                await AttachChatAuthAsync(http);
                var chatUrl = GetChatUrl(streaming: false);
                _logger?.LogDebug($"[OpenAIProvider] POST {chatUrl}  ({(_useBackendProxy ? "via backend proxy / JWT" : "direct OpenAI / sk-key")})");

                // Tool-calling loop — runs at most MaxToolCallRounds times. Each round either:
                //   a) returns a final assistant text answer, OR
                //   b) returns one or more tool_calls, which we dispatch and append as tool
                //      messages, then re-ask the model.
                //
                // When tool calling is disabled, the loop runs exactly once with no tools
                // attached, behaving identically to the pre-Phase-3c implementation.
                string? finalAnswer = null;
                for (int round = 0; round < MaxToolCallRounds; round++)
                {
                    var requestBody = BuildRequestBody(messages, includeTools: ToolCallingEnabled);
                    var requestJson = JsonSerializer.Serialize(requestBody);

                    var response = await SendWithRetryAsync(http, chatUrl, requestJson);
                    var responseText = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger?.LogError($"[OpenAIProvider] API error ({response.StatusCode}): {responseText}");

                        // When tool calling is enabled and we hit a 4xx, the request body shape
                        // is the most likely culprit — dump the request body too so we can debug
                        // schema/serialization issues without a packet capture.
                        if (ToolCallingEnabled && (int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                        {
                            try
                            {
                                AiDiagLog.ApiCallFailed(
                                    "OpenAI /chat/completions (round " + (round + 1) + ")",
                                    (int)response.StatusCode,
                                    "RESPONSE: " + responseText + "\nREQUEST: " + requestJson);
                            }
                            catch { /* logging must never break the flow */ }
                        }

                        var statusCode = (int)response.StatusCode;
                        var lowerResponse = responseText.ToLowerInvariant();
                        if (statusCode == 429
                            || lowerResponse.Contains("rate_limit_exceeded")
                            || lowerResponse.Contains("insufficient_quota"))
                        {
                            throw new AiQuotaExceededException();
                        }

                        return $"API Error ({response.StatusCode}): Unable to process your request. Please contact ZestineTech support at support@zestinetech.com.";
                    }

                    using var doc = JsonDocument.Parse(responseText);
                    if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                        choices.GetArrayLength() == 0)
                    {
                        return "No response generated.";
                    }

                    var messageElem = choices[0].GetProperty("message");

                    // If the model returned tool_calls, dispatch them and continue the loop.
                    if (ToolCallingEnabled
                        && messageElem.TryGetProperty("tool_calls", out var toolCallsElem)
                        && toolCallsElem.ValueKind == JsonValueKind.Array
                        && toolCallsElem.GetArrayLength() > 0)
                    {
                        // Append the assistant's tool-call message so the next request has
                        // the matching tool_call_ids — OpenAI rejects tool messages without it.
                        messages.Add(BuildAssistantToolCallMessage(messageElem));

                        foreach (var toolCall in toolCallsElem.EnumerateArray())
                        {
                            var toolCallId = toolCall.GetProperty("id").GetString() ?? string.Empty;
                            var function = toolCall.GetProperty("function");
                            var fnName = function.GetProperty("name").GetString() ?? string.Empty;
                            var fnArgs = function.GetProperty("arguments").GetString() ?? "{}";

                            _logger?.LogDebug($"[OpenAIProvider] Tool call round {round + 1}: {fnName}");
                            try { AiDiagLog.ToolCall(round + 1, fnName, fnArgs); } catch { /* no-op */ }

                            // ToolDispatcher has its own try/catch and serializes errors as JSON,
                            // so this call is safe to make synchronously without further wrapping.
                            var toolStartTicks = Environment.TickCount;
                            var toolResult = _toolDispatcher!.Dispatch(fnName, fnArgs, toolCallId);
                            var toolElapsedMs = Environment.TickCount - toolStartTicks;
                            try { AiDiagLog.ToolResult(round + 1, fnName, toolResult, toolElapsedMs); } catch { /* no-op */ }

                            messages.Add(new
                            {
                                role = "tool",
                                tool_call_id = toolCallId,
                                content = toolResult
                            });
                        }
                        continue; // Re-ask the model with the tool results in scope.
                    }

                    // No tool calls — this is the final answer.
                    finalAnswer = messageElem.TryGetProperty("content", out var contentProp)
                        ? contentProp.GetString()
                        : null;
                    break;
                }

                if (string.IsNullOrEmpty(finalAnswer))
                {
                    _logger?.LogWarning(
                        $"[OpenAIProvider] Tool-call loop exhausted {MaxToolCallRounds} rounds without a final answer — asking model to summarize what it found");

                    // Rescue attempt: ask GPT one more time WITHOUT tools, instructing it to
                    // synthesize an answer from the tool results already in `messages`. This
                    // converts the dead-end "couldn't reach a final answer" into a real reply
                    // built from what the tools actually returned. We force `includeTools:false`
                    // so the model can't request another round.
                    try
                    {
                        messages.Add(new {
                            role = "user",
                            content = "Based on the tool results above, please give your best answer to my original question. " +
                                      "If the tools didn't return enough info, say what's missing and suggest one Revit menu/command I could check manually."
                        });

                        var rescueBody = BuildRequestBody(messages, includeTools: false);
                        var rescueJson = JsonSerializer.Serialize(rescueBody);
                        var rescueResponse = await SendWithRetryAsync(http, chatUrl, rescueJson);

                        if (rescueResponse.IsSuccessStatusCode)
                        {
                            var rescueText = await rescueResponse.Content.ReadAsStringAsync();
                            using var rescueDoc = JsonDocument.Parse(rescueText);
                            var rescueChoice = rescueDoc.RootElement.GetProperty("choices")[0];
                            var rescueMessage = rescueChoice.GetProperty("message");
                            if (rescueMessage.TryGetProperty("content", out var rc))
                                finalAnswer = rc.GetString();
                        }
                    }
                    catch (Exception rescueEx)
                    {
                        _logger?.LogWarning($"[OpenAIProvider] Rescue summarize call failed: {rescueEx.Message}");
                    }

                    if (string.IsNullOrEmpty(finalAnswer))
                        finalAnswer = "I gathered some information but couldn't synthesize a final answer. Try rephrasing your question, or check the relevant Revit menu directly.";
                }

                _conversationHistory.Add(new ConversationMessage("assistant", finalAnswer));
                return finalAnswer;
            }
            catch (AiQuotaExceededException)
            {
                throw; // propagated to ViewModel
            }
            catch (AiNetworkUnavailableException)
            {
                throw; // propagated to ViewModel for friendly "AI unavailable" handling
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[OpenAIProvider] SendMessageAsync error: {ex.Message}", ex);
                return $"Error: {ex.Message}";
            }
        }

        /// <summary>
        /// Builds the request body for /chat/completions, optionally including the
        /// tool definitions when tool calling is enabled.
        /// </summary>
        private object BuildRequestBody(List<object> messages, bool includeTools)
        {
            // Anonymous-typed payloads — System.Text.Json serializes properties in declaration order.
            // When tools is null, OpenAI ignores the field (so we just include both shapes for clarity).
            if (includeTools && _toolDefinitions != null)
            {
                return new
                {
                    model = _model,
                    messages = messages.ToArray(),
                    temperature = 0.7,
                    max_tokens = 1500,
                    tools = _toolDefinitions
                };
            }
            return new
            {
                model = _model,
                messages = messages.ToArray(),
                temperature = 0.7,
                max_tokens = 1500
            };
        }

        /// <summary>
        /// Reconstructs the assistant message that originated a set of tool calls so it can
        /// be appended to the conversation. OpenAI requires this exact shape (role=assistant,
        /// tool_calls array) to precede the matching tool messages, otherwise the next request
        /// fails with "messages with role 'tool' must follow an assistant message with tool_calls".
        /// </summary>
        private static object BuildAssistantToolCallMessage(JsonElement assistantMessage)
        {
            var toolCallsElem = assistantMessage.GetProperty("tool_calls");
            var toolCalls = new List<object>();
            foreach (var tc in toolCallsElem.EnumerateArray())
            {
                var fn = tc.GetProperty("function");
                toolCalls.Add(new
                {
                    id = tc.GetProperty("id").GetString(),
                    type = "function",
                    function = new
                    {
                        name = fn.GetProperty("name").GetString(),
                        arguments = fn.GetProperty("arguments").GetString()
                    }
                });
            }

            // OpenAI accepts content=null when tool_calls is set, but some SDKs object;
            // empty string is universally accepted.
            var content = assistantMessage.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? string.Empty
                : string.Empty;

            return new
            {
                role = "assistant",
                content,
                tool_calls = toolCalls.ToArray()
            };
        }

        public bool SupportsStreaming => true;

        public async Task<string> StreamMessageAsync(string userMessage, Action<string> onToken)
        {
            // Tool calling is incompatible with our current streaming pipeline — tool_calls
            // arrive across many delta chunks and need full reassembly before dispatch.
            // For Phase 3c-NEW we fall back to the non-streaming path when tools are enabled
            // and emit the final answer as a single onToken callback. The user still sees the
            // answer; they just don't watch it type out. Phase 3g may revisit this.
            if (ToolCallingEnabled)
            {
                var fullAnswer = await SendMessageAsync(userMessage);
                onToken(fullAnswer);
                return fullAnswer;
            }

            // Local key only matters when calling OpenAI directly. In proxy mode the backend
            // pulls the key from Azure Key Vault, so an empty _apiKey is expected and fine.
            if (!_useBackendProxy && string.IsNullOrWhiteSpace(_apiKey))
            {
                var msg = "API Key not configured. Please set your OpenAI API key in App.config (AI:ApiKey) or OPENAI_API_KEY environment variable.";
                onToken(msg);
                return msg;
            }

            try
            {
                _conversationHistory.Add(new ConversationMessage("user", userMessage));
                if (_conversationHistory.Count > MaxHistoryMessages)
                    _conversationHistory.RemoveRange(0, _conversationHistory.Count - MaxHistoryMessages);

                // Redirect check — block bypass/uninstall/loophole questions
                if (await _knowledge.IsRedirectedAsync(userMessage))
                {
                    var redirectReply = await _knowledge.GetRedirectReplyAsync();
                    _conversationHistory.Add(new ConversationMessage("assistant", redirectReply));
                    onToken(redirectReply);
                    return redirectReply;
                }

                // Off-topic check — bypass when an upstream classifier (DB query / visibility /
                // warning diagnosis) has already validated intent and injected context.
                bool streamIntentValidated = !string.IsNullOrEmpty(_modelContextBlock) &&
                    (_modelContextBlock.StartsWith("DATABASE QUERY RESULTS:", StringComparison.Ordinal) ||
                     _modelContextBlock.Contains("You have access to the local BIManage SQLite database") ||
                     _modelContextBlock.Contains("ELEMENT VISIBILITY DIAGNOSIS") ||
                     _modelContextBlock.Contains("WARNING RESOLUTION DIAGNOSIS"));

                if (!streamIntentValidated && await _knowledge.IsOffTopicAsync(userMessage))
                {
                    var snarkyReply = await _knowledge.GetOffTopicReplyAsync();
                    _conversationHistory.Add(new ConversationMessage("assistant", snarkyReply));
                    onToken(snarkyReply);
                    return snarkyReply;
                }

                // Build same prompt as SendMessageAsync
                var intents = Knowledge.IntentClassifier.Classify(userMessage);
                var relevantKnowledge = await _knowledge.GetRelevantKnowledgeAsync(userMessage, intents);
                var systemContext = await _knowledge.GetSystemPromptAsync();

                bool hasZeManageKnowledge = !string.IsNullOrEmpty(relevantKnowledge)
                    && relevantKnowledge.Contains("[ZeManage:");

                var zeManageAttributionInstruction = hasZeManageKnowledge ? @"

PRODUCT ATTRIBUTION (MANDATORY when answering from [ZeManage: ...] entries):
- The feature being asked about is a ZeManage capability. Identify it as such.
- Begin the answer by naming the feature and stating it is part of ZeManage
  (e.g. ""Pin Protection is a ZeManage feature that..."" or ""ZeManage's Activity Tracker lets you..."").
- Never present these features as generic Revit functionality — they exist because ZeManage adds them.
- If the user asks ""what is X"" and X matches a [ZeManage: ...] entry, the answer must say so explicitly.
" : "";

                var knowledgeSection = !string.IsNullOrEmpty(relevantKnowledge)
                    ? $"\n\nRELEVANT KNOWLEDGE FROM DATABASE:\n{relevantKnowledge}\n{zeManageAttributionInstruction}" : "";
                var modelDataSection = !string.IsNullOrEmpty(_modelContextBlock)
                    ? $"\n\n{_modelContextBlock}\n" : "";

                var lower = userMessage.ToLower();
                var wantsElaboration = lower.Contains("elaborate") || lower.Contains("explain more") ||
                    lower.Contains("tell me more") || lower.Contains("explain") || lower.Contains("how") || lower.Contains("why");

                var systemPrompt = $@"{systemContext}
{knowledgeSection}
{modelDataSection}
RESPONSE FORMATTING: Use bullet points, numbered lists. Keep paragraphs short.
{(wantsElaboration ? "Provide DETAILED explanation." : "Keep response CONCISE (3-5 key points).")}
Always end with 2-3 follow-up questions in this EXACT format (the UI parses it to build clickable pills):

You might also want to know:
1. <question one ending with ?>
2. <question two ending with ?>

Use numbered list (1. 2. 3.) — NOT bullets (• or -). Each question must end with '?'.";

                var messages = new List<object> { new { role = "system", content = systemPrompt } };
                var historyToSend = _conversationHistory.Take(_conversationHistory.Count - 1).ToList();
                foreach (var msg in historyToSend)
                {
                    var content = msg.Role == "assistant" && msg.Content.Length > HistoryAssistantMaxChars
                        ? msg.Content.Substring(0, HistoryAssistantMaxChars) + "…" : msg.Content;
                    messages.Add(new { role = msg.Role, content });
                }
                messages.Add(new { role = "user", content = userMessage });

                var requestBody = new
                {
                    model = _model,
                    messages = messages.ToArray(),
                    temperature = 0.7,
                    max_tokens = 1500,
                    stream = true
                };

                using var http = new System.Net.Http.HttpClient();
                http.Timeout = TimeSpan.FromSeconds(60);
                await AttachChatAuthAsync(http);

                var jsonContent = new System.Net.Http.StringContent(
                    JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

                var streamUrl = GetChatUrl(streaming: true);
                _logger?.LogDebug($"[OpenAIProvider] POST {streamUrl}  (stream={(_useBackendProxy ? "via backend proxy / JWT" : "direct OpenAI / sk-key")})");

                var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, streamUrl)
                {
                    Content = jsonContent
                };

                System.Net.Http.HttpResponseMessage response;
                try
                {
                    response = await http.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                }
                catch (HttpRequestException ex)
                {
                    // Streaming path doesn't retry — surface transport failure as a typed exception
                    // so the ViewModel shows the friendly "AI unavailable" message.
                    throw new AiNetworkUnavailableException(
                        "Could not reach the AI service. Check your internet connection.", ex);
                }
                catch (TaskCanceledException ex)
                {
                    throw new AiNetworkUnavailableException(
                        "The AI service did not respond in time. Check your connection and try again.", ex);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errorText = await response.Content.ReadAsStringAsync();
                    _logger?.LogError($"[OpenAIProvider] Stream API error ({response.StatusCode}): {errorText}");

                    var statusCode = (int)response.StatusCode;
                    var lowerResponse = errorText.ToLowerInvariant();
                    if (statusCode == 429 || lowerResponse.Contains("rate_limit_exceeded") || lowerResponse.Contains("insufficient_quota"))
                        throw new AiQuotaExceededException();

                    var errMsg = $"API Error ({response.StatusCode}): Unable to process your request.";
                    onToken(errMsg);
                    return errMsg;
                }

                var fullResponse = new StringBuilder();
                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new System.IO.StreamReader(stream);

                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (!line.StartsWith("data: ")) continue;
                    var data = line.Substring(6).Trim();
                    if (data == "[DONE]") break;
                    if (string.IsNullOrEmpty(data)) continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                            choices.GetArrayLength() > 0)
                        {
                            var delta = choices[0].GetProperty("delta");
                            if (delta.TryGetProperty("content", out var contentProp))
                            {
                                var token = contentProp.GetString();
                                if (!string.IsNullOrEmpty(token))
                                {
                                    fullResponse.Append(token);
                                    onToken(token);
                                }
                            }
                        }
                    }
                    catch { /* skip malformed SSE chunk */ }
                }

                var result = fullResponse.ToString();
                if (string.IsNullOrEmpty(result))
                    result = "No response generated.";

                _conversationHistory.Add(new ConversationMessage("assistant", result));
                return result;
            }
            catch (AiQuotaExceededException) { throw; }
            catch (AiNetworkUnavailableException) { throw; }
            catch (Exception ex)
            {
                _logger?.LogError($"[OpenAIProvider] StreamMessageAsync error: {ex.Message}", ex);
                var errMsg = $"Error: {ex.Message}";
                onToken(errMsg);
                return errMsg;
            }
        }

        public void SetModelContext(string? contextBlock)
        {
            _modelContextBlock = contextBlock;
            _logger?.LogDebug($"[OpenAIProvider] Model context {(contextBlock != null ? $"set ({contextBlock.Length} chars)" : "cleared")}");
        }

        public void ClearConversation()
        {
            _conversationHistory.Clear();
            _modelContextBlock = null;
            _logger?.LogDebug("[OpenAIProvider] Conversation history cleared");
        }
    }
}
