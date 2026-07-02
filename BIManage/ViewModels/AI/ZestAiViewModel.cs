using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.AI;
using BIManage.AI.Interfaces;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Models.AI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;

namespace BIManage.ViewModels.AI
{
    public sealed class ZestAiViewModel : ObservableObject
    {
        private readonly IAIProvider _aiProvider;
        private readonly ILogger? _logger;
        private readonly SessionInfo _sessionInfo;
        private readonly ModelContextService? _modelContextService;
        private readonly ChatRepository? _chatRepository;
        private readonly LocalDbQueryService? _dbQueryService;
        private readonly Document? _revitDoc;
        private readonly UIDocument? _uidoc;
        private bool _isAdmin;

        private string? _chatSessionId;

        // Tracks which chat sessions have had their title persisted, so we set the title
        // exactly once per session (on the first user message of THAT session). Using
        // _sessionInfo.UserPromptCount was wrong because that counter is per-window, not
        // per-chat — clicking "New Chat" mid-window left it >1 and the new session's
        // title never got set, producing "(no title)" rows in the history popup.
        private readonly HashSet<string> _sessionsWithTitleSet = new();

        // Cached model context — fetched once on the first relevant query, reused in the same session
        private string? _cachedModelGuid;
        private ModelContextResult? _cachedContext;
        private List<HealthAlert>? _healthAlerts;

        private string _userMessage = string.Empty;
        private bool _isSending;
        private bool _isThinking;

        /// <param name="aiProvider">Required AI chat provider.</param>
        /// <param name="logger">Optional diagnostic logger.</param>
        /// <param name="modelContextService">
        ///   Optional service that fetches live metrics from the backend.
        ///   May be null when API is not configured — the ViewModel degrades gracefully.
        /// </param>
        /// <param name="revitDoc">
        ///   The active Revit document used to extract the model GUID.
        ///   May be null, in which case model-context enrichment is skipped.
        /// </param>
        /// <param name="uidoc">
        ///   The active UIDocument. ActiveView is read fresh on each query so the
        ///   diagnosis always uses the view the user is currently looking at.
        /// </param>
        public ZestAiViewModel(
            IAIProvider aiProvider,
            ILogger? logger = null,
            ModelContextService? modelContextService = null,
            Document? revitDoc = null,
            UIDocument? uidoc = null,
            ChatRepository? chatRepository = null,
            LocalDbQueryService? dbQueryService = null,
            bool isAdmin = false)
        {
            _aiProvider          = aiProvider;
            _logger              = logger;
            _modelContextService = modelContextService;
            _revitDoc            = revitDoc;
            _uidoc               = uidoc;
            _chatRepository      = chatRepository;
            _dbQueryService      = dbQueryService;
            _isAdmin             = isAdmin;
            _sessionInfo         = new SessionInfo();
            _sessionInfo.UserName = Environment.UserName;

            Messages = new ObservableCollection<ChatMessage>();
            SendMessageCommand = new AsyncRelayCommand(SendMessageAsync, CanSendMessage);
            FollowUpClickCommand = new RelayCommand<string>(OnFollowUpClick);
            ThumbsUpCommand = new RelayCommand<ChatMessage>(msg => OnFeedback(msg, 1));
            ThumbsDownCommand = new RelayCommand<ChatMessage>(msg => OnFeedback(msg, -1));
            ExportChatCommand = new RelayCommand(ExportChat, () => Messages.Count > 1);

            // Cache model GUID immediately (synchronous call, safe to do here)
            if (_modelContextService != null && _revitDoc != null)
            {
                _cachedModelGuid = _modelContextService.GetModelGuid(_revitDoc);
                _logger?.LogDebug($"[ZeAI] Model GUID for context enrichment: {_cachedModelGuid ?? "null"}");
            }

            var modelLabel = string.IsNullOrEmpty(_cachedModelGuid) ? string.Empty
                             : $" I can see your model's live metrics — just ask!";

            Messages.Add(new ChatMessage
            {
                Text      = $"Hello! I'm your Ze AI Assistant.{modelLabel} Ask me anything about Revit, BIM workflows, or your current project!",
                IsUser    = false,
                Timestamp = DateTime.Now
            });

            GenerateSuggestionChips();
            InitializeChatSessionAsync();
            CheckHealthAlertsAsync();

            _logger?.LogInfo($"ZestAiViewModel initialized with provider: {aiProvider.ProviderName}");

            // Write session header to diagnostic log so we can confirm GUID extraction worked
            AiDiagLog.SessionStart(
                providerName: aiProvider.ProviderName,
                modelGuid:    _cachedModelGuid,
                docTitle:     _revitDoc?.Title);
        }

        public void SetRevitUsername(string username)
        {
            if (!string.IsNullOrEmpty(username))
                _sessionInfo.UserName = username;
        }

        public ObservableCollection<ChatMessage> Messages { get; }
        public ObservableCollection<SuggestionChip> SuggestionChips { get; } = new ObservableCollection<SuggestionChip>();

        /// <summary>
        /// Last 5 chat sessions for the currently-open Revit model, ordered newest-first.
        /// Refreshed when the History popup is opened. Click an item to resume that session.
        /// </summary>
        public ObservableCollection<ChatSessionSummary> RecentSessions { get; } = new ObservableCollection<ChatSessionSummary>();

        public string UserMessage
        {
            get => _userMessage;
            set
            {
                if (SetProperty(ref _userMessage, value))
                    ((AsyncRelayCommand)SendMessageCommand).NotifyCanExecuteChanged();
            }
        }

        public bool IsSending
        {
            get => _isSending;
            set => SetProperty(ref _isSending, value);
        }

        public bool IsThinking
        {
            get => _isThinking;
            set => SetProperty(ref _isThinking, value);
        }

        public ICommand SendMessageCommand { get; }
        public ICommand FollowUpClickCommand { get; }
        public ICommand ThumbsUpCommand { get; }
        public ICommand ThumbsDownCommand { get; }
        public ICommand ExportChatCommand { get; }

        /// <summary>
        /// Display name for the welcome-screen greeting ("Good Morning {UserName}!").
        /// Falls back to the Windows account name when the Revit username hasn't been set
        /// via <see cref="SetRevitUsername"/>.
        /// </summary>
        public string UserName =>
            string.IsNullOrWhiteSpace(_sessionInfo.UserName) ? Environment.UserName : _sessionInfo.UserName;

        /// <summary>
        /// Time-of-day greeting used in the welcome card. Resolved once when the welcome
        /// screen binds — refreshing on the hour isn't worth the complexity.
        /// </summary>
        public string Greeting
        {
            get
            {
                var hour = DateTime.Now.Hour;
                if (hour < 12) return "Good Morning";
                if (hour < 18) return "Good Afternoon";
                return "Good Evening";
            }
        }

        private bool CanSendMessage() =>
            !string.IsNullOrWhiteSpace(UserMessage) && !IsSending;

        private void OnFollowUpClick(string? question)
        {
            if (string.IsNullOrWhiteSpace(question) || IsSending) return;
            UserMessage = question;
        }


        private async void OnFeedback(ChatMessage? msg, int rating)
        {
            if (msg == null) return;
            // Toggle: clicking same rating again clears it
            msg.FeedbackRating = msg.FeedbackRating == rating ? null : rating;

            if (_chatRepository != null && !string.IsNullOrEmpty(msg.ChatMessageId))
            {
                try
                {
                    await _chatRepository.UpdateFeedbackAsync(msg.ChatMessageId, msg.FeedbackRating ?? 0);
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"[ZestAI] Failed to save feedback: {ex.Message}", ex);
                }
            }
        }

        // Matches one trailing follow-up item. The model picks its own list style depending on
        // formatting context — observed in tester logs:
        //   "1. What is X?"   (numbered)
        //   "- What is X?"    (dash)
        //   "• What is X?"    (bullet)
        //   "* What is X?"    (asterisk, markdown)
        // All four collapse to the same captured question text. Anchor at start-of-(trimmed)-line
        // so paragraph prose containing a hyphen doesn't get mis-detected.
        private static readonly Regex FollowUpItemRegex = new Regex(
            @"^(?:\d+\.|[-•*])\s+(.+)$", RegexOptions.Compiled);

        /// <summary>
        /// Extracts the trailing follow-up questions from an AI response.
        ///
        /// Why this is necessary: the system prompt asks the model to end with
        /// "You might also want to know:" + a short list. We strip that block from the
        /// rendered bubble and rebind it as clickable pills (see <see cref="ChatMessage.FollowUpQuestions"/>).
        /// Without stripping, users see the questions twice — once as prose, once as pills —
        /// and they can't click the prose copy. Tester complaint 2026-05-25.
        ///
        /// Returns the cleaned text (without the questions block) and the list of question strings.
        /// </summary>
        private static (string cleanedText, List<string> questions) ExtractFollowUpQuestions(string response)
        {
            var questions = new List<string>();
            if (string.IsNullOrWhiteSpace(response))
                return (response, questions);

            var lines = response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            // Find the last non-empty line
            int lastContentLine = lines.Length - 1;
            while (lastContentLine >= 0 && string.IsNullOrWhiteSpace(lines[lastContentLine]))
                lastContentLine--;

            if (lastContentLine < 0)
                return (response, questions);

            // Walk backwards from the end to find consecutive list items (any of the supported
            // bullet styles). Stop the moment we hit a non-list line.
            int firstQuestionLine = -1;
            for (int i = lastContentLine; i >= 0; i--)
            {
                var trimmed = lines[i].TrimStart();
                if (FollowUpItemRegex.IsMatch(trimmed))
                    firstQuestionLine = i;
                else
                    break;
            }

            // Need at least 2 items at the end
            if (firstQuestionLine < 0 || (lastContentLine - firstQuestionLine + 1) < 2)
                return (response, questions);

            // Check for a lead-in line (ending with ':') or verify items are questions (ending with '?')
            int cutLine = firstQuestionLine;
            bool hasLeadIn = false;
            if (cutLine > 0)
            {
                var prevLine = lines[cutLine - 1].Trim();
                if (prevLine.EndsWith(":"))
                {
                    hasLeadIn = true;
                    cutLine--;
                }
            }

            // Extract question texts
            for (int i = firstQuestionLine; i <= lastContentLine; i++)
            {
                var match = FollowUpItemRegex.Match(lines[i].TrimStart());
                if (match.Success)
                    questions.Add(match.Groups[1].Value.Trim());
            }

            // Only extract if 2-4 items, and either has a lead-in line or items end with '?'
            if (questions.Count < 2 || questions.Count > 4)
                return (response, new List<string>());

            if (!hasLeadIn)
            {
                bool areQuestions = true;
                foreach (var q in questions)
                {
                    if (!q.EndsWith("?")) { areQuestions = false; break; }
                }
                if (!areQuestions)
                    return (response, new List<string>());
            }

            // Build cleaned text
            var cleanedLines = new string[cutLine];
            Array.Copy(lines, cleanedLines, cutLine);
            var cleanedText = string.Join("\n", cleanedLines).TrimEnd();

            if (string.IsNullOrWhiteSpace(cleanedText))
                return (response, new List<string>());

            return (cleanedText, questions);
        }

        // ──────────────────────────────────────────────────────────────────────
        // Context-aware suggestion chips
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Regenerates suggestion chips based on the current active view type.
        /// Called from code-behind on New Chat to refresh for potentially different view.
        /// </summary>
        public void RefreshSuggestionChips() => GenerateSuggestionChips();

        private void GenerateSuggestionChips()
        {
            SuggestionChips.Clear();

            View? activeView = null;
            try { activeView = _uidoc?.ActiveView; } catch { /* no-op */ }

            if (activeView is ViewPlan)
            {
                SuggestionChips.Add(new SuggestionChip
                {
                    Title = "View Range Help", Subtitle = "Fix missing elements in plans",
                    Prompt = "How do I fix view range settings so I can see all elements in this floor plan?",
                    IconGlyph = "\xE81E", IconColor = "#000000", IconBackground = "#E8F0FA"
                });
                SuggestionChips.Add(new SuggestionChip
                {
                    Title = "Floor Plan Best Practices", Subtitle = "Standards & documentation",
                    Prompt = "Best practices for setting up floor plans in Revit",
                    IconGlyph = "\xE8A5", IconColor = "#0288D1", IconBackground = "#E6F6FE"
                });
            }
            else if (activeView is View3D)
            {
                SuggestionChips.Add(new SuggestionChip
                {
                    Title = "3D View Tips", Subtitle = "Section box & navigation",
                    Prompt = "Tips for working with 3D views, section boxes, and orientations in Revit",
                    IconGlyph = "\xF158", IconColor = "#000000", IconBackground = "#E8F0FA"
                });
                SuggestionChips.Add(new SuggestionChip
                {
                    Title = "Visual Styles", Subtitle = "Rendering & appearance",
                    Prompt = "How to use visual styles and graphic display options in 3D views",
                    IconGlyph = "\xE790", IconColor = "#0288D1", IconBackground = "#E6F6FE"
                });
            }
            else if (activeView is ViewSheet)
            {
                SuggestionChips.Add(new SuggestionChip
                {
                    Title = "Sheet Setup", Subtitle = "Place views & title blocks",
                    Prompt = "How to place views on sheets and configure title blocks in Revit",
                    IconGlyph = "\xE8A1", IconColor = "#000000", IconBackground = "#E8F0FA"
                });
                SuggestionChips.Add(new SuggestionChip
                {
                    Title = "Print & Export", Subtitle = "PDF & DWG output",
                    Prompt = "Best practices for printing sheets and exporting to PDF in Revit",
                    IconGlyph = "\xE749", IconColor = "#0288D1", IconBackground = "#E6F6FE"
                });
            }
            else if (activeView is ViewSection)
            {
                SuggestionChips.Add(new SuggestionChip
                {
                    Title = "Section Views", Subtitle = "Depth, extents & detail",
                    Prompt = "How to adjust section depth, far clip, and detail level in Revit section views",
                    IconGlyph = "\xE81E", IconColor = "#000000", IconBackground = "#E8F0FA"
                });
                SuggestionChips.Add(new SuggestionChip
                {
                    Title = "Annotation Tips", Subtitle = "Dimensions & tags",
                    Prompt = "Best practices for annotating section views with dimensions, tags, and text notes",
                    IconGlyph = "\xE8D2", IconColor = "#0288D1", IconBackground = "#E6F6FE"
                });
            }
            else if (activeView is ViewSchedule)
            {
                SuggestionChips.Add(new SuggestionChip
                {
                    Title = "Schedule Filters", Subtitle = "Sort, filter & group",
                    Prompt = "How to filter, sort, and group data in Revit schedules",
                    IconGlyph = "\xE71C", IconColor = "#000000", IconBackground = "#E8F0FA"
                });
                SuggestionChips.Add(new SuggestionChip
                {
                    Title = "Calculated Values", Subtitle = "Formulas & parameters",
                    Prompt = "How to add calculated parameters and formulas to Revit schedules",
                    IconGlyph = "\xE8EF", IconColor = "#0288D1", IconBackground = "#E6F6FE"
                });
            }
            else
            {
                // Default / fallback suggestions
                SuggestionChips.Add(new SuggestionChip
                {
                    Title = "Family Creation", Subtitle = "Build custom components",
                    Prompt = "Help me with Family Creation",
                    IconGlyph = "\xE8D4", IconColor = "#000000", IconBackground = "#E8F0FA"
                });
                SuggestionChips.Add(new SuggestionChip
                {
                    Title = "BIM Coordination", Subtitle = "Clash detection & workflows",
                    Prompt = "BIM Coordination best practices",
                    IconGlyph = "\xE774", IconColor = "#0288D1", IconBackground = "#E6F6FE"
                });
            }

            // Always-on tail. Together with the 2 view-aware suggestions above this gives the
            // 6 Quick Action tiles the welcome screen renders in a 3×2 grid. Each chip's Prompt
            // is phrased to land GPT on a specific tool when possible — generic prompts like
            // "Performance tips" make GPT generate generic answers; model-specific prompts like
            // "Check this model's warnings…" force a tool call and produce concrete data.
            SuggestionChips.Add(new SuggestionChip
            {
                Title = "Model Health", Subtitle = "Live warnings & alerts",
                Prompt = "Check this model's health alerts and tell me the top issues to fix.",
                IconGlyph = "\xE95E", IconColor = "#DC2626", IconBackground = "#FEF2F2"
            });
            SuggestionChips.Add(new SuggestionChip
            {
                Title = "Model Overview", Subtitle = "What's in this file",
                Prompt = "Give me an overview of what's in this model — categories, element counts, families.",
                IconGlyph = "\xE8A5", IconColor = "#0288D1", IconBackground = "#E6F6FE"
            });
            SuggestionChips.Add(new SuggestionChip
            {
                Title = "Performance Tips", Subtitle = "Speed up your model",
                Prompt = "Give me practical performance optimization tips for this Revit model.",
                IconGlyph = "\xE9F5", IconColor = "#E6930A", IconBackground = "#FFF8E7"
            });
            SuggestionChips.Add(new SuggestionChip
            {
                Title = "Protection Rules", Subtitle = "What's guarding this model",
                Prompt = "What ZeManage protection rules are applied to this model right now?",
                IconGlyph = "\xE840", IconColor = "#7C3AED", IconBackground = "#F3E8FF"
            });
        }

        // ──────────────────────────────────────────────────────────────────────
        // Core: send message, optionally inject live model context
        // ──────────────────────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task SendMessageAsync()
        {
            if (string.IsNullOrWhiteSpace(UserMessage)) return;

            var userText = UserMessage;
            UserMessage = string.Empty;

            Messages.Add(new ChatMessage { Text = userText, IsUser = true, Timestamp = DateTime.Now });
            PersistMessageAsync("user", userText);

            // ── Local Guardrail: Prevent token waste on off-topic questions ──
            var preformedResponse = GetPreformedAnswerIfOffTopic(userText);
            if (preformedResponse != null)
            {
                Messages.Add(new ChatMessage
                {
                    Text = preformedResponse,
                    IsUser = false,
                    Timestamp = DateTime.Now,
                    FollowUpQuestions = new List<string> { "How do I purge the 576 unused items?", "Should I link CAD files instead of importing?" }
                });
                PersistMessageAsync("assistant", preformedResponse);
                return;
            }

            IsSending   = true;
            IsThinking  = true;

            // ── Revit API calls MUST happen on the Revit main thread (here, before any await) ──
            // DiagnoseAndFormat calls elem.IsHidden(), GetCategoryHidden(), doc.GetWarnings(), etc.
            // Once we hit the first await below, the continuation may resume on a thread-pool
            // thread where those calls silently return wrong results or throw.
            string? visibilityContextBlock = null;
            if (ElementVisibilityService.IsVisibilityQuery(userText))
            {
                if (_revitDoc != null && !_revitDoc.IsFamilyDocument)
                {
                    // Read ActiveView fresh each time — the user may have switched views since the dialog opened
                    var currentView = _uidoc?.ActiveView;
                    visibilityContextBlock = ElementVisibilityService.DiagnoseAndFormat(_revitDoc, userText, currentView);
                    _logger?.LogDebug($"[ZestAI] Visibility diagnosis captured on main thread ({visibilityContextBlock?.Length ?? 0} chars)");
                }
                else
                {
                    visibilityContextBlock =
                        "ELEMENT VISIBILITY DIAGNOSIS\n" +
                        "NOTE: No active Revit project document is open. Cannot run live checks.\n\n" +
                        "Please ensure a project (.rvt) file is open in Revit and reopen the AI assistant.";
                    _logger?.LogDebug("[ZestAI] Visibility query detected but no project document available");
                }
            }
            else if (WarningResolutionService.IsWarningQuery(userText))
            {
                if (_revitDoc != null && !_revitDoc.IsFamilyDocument)
                {
                    visibilityContextBlock = WarningResolutionService.DiagnoseAndFormat(_revitDoc, userText);
                    _logger?.LogDebug($"[ZestAI] Warning diagnosis captured on main thread ({visibilityContextBlock?.Length ?? 0} chars)");
                }
                else
                {
                    visibilityContextBlock =
                        "WARNING RESOLUTION DIAGNOSIS\n" +
                        "NOTE: No active Revit project document is open. Cannot run live checks.\n\n" +
                        "Please ensure a project (.rvt) file is open in Revit and reopen the AI assistant.";
                }
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                _sessionInfo.UserPromptCount++;
                _sessionInfo.Messages.Add(new MessageMetadata("user", userText));

                // Inject live model context into the AI's system prompt (if applicable)
                await InjectModelContextAsync(userText, visibilityContextBlock);

                // ── Local DB query: fast path (pre-built templates) ──
                bool isDbQuery = _dbQueryService != null &&
                    BIManage.AI.Knowledge.IntentClassifier.IsLocalDbQuery(userText) &&
                    visibilityContextBlock == null; // don't interfere with visibility/warning diagnosis

                if (isDbQuery)
                {
                    var templateResult = _dbQueryService!.TryRunTemplateQuery(userText, _isAdmin);
                    if (templateResult != null)
                    {
                        // Fast path: inject results as context, AI formats the answer
                        _aiProvider.SetModelContext(
                            $"DATABASE QUERY RESULTS:\n{templateResult}\n\nAnswer the user's question based ONLY on this data. Be precise with dates, numbers, and names. " +
                            "If the data spans multiple models (multiple distinct model_name values), GROUP your response by model — show each model's syncs under a clear heading or bullet — so the user can see the full picture across every model they synced. " +
                            "Don't omit rows. If only one model is present, summarize concisely.");
                        _logger?.LogDebug($"[ZestAI] DB fast path: template query matched, {templateResult.Length} chars injected");
                    }
                    else
                    {
                        // Flexible path: inject schema so AI can generate SQL
                        var schema = _dbQueryService.GetSchemaDescription(_isAdmin);
                        _aiProvider.SetModelContext(
                            $"You have access to the local BIManage SQLite database.\n{schema}\n\nIf you need data to answer, generate a SQL query wrapped in [SQL]...[/SQL] tags. Only SELECT. Always include LIMIT.");
                        _logger?.LogDebug("[ZestAI] DB flexible path: schema injected for AI-generated SQL");
                    }
                }

                string response;

                if (_aiProvider.SupportsStreaming)
                {
                    // Streaming: add an empty AI message, then append tokens as they arrive
                    var aiMessage = new ChatMessage { Text = "", IsUser = false, Timestamp = DateTime.Now };
                    Messages.Add(aiMessage);
                    bool firstToken = true;

                    response = await _aiProvider.StreamMessageAsync(userText, token =>
                    {
                        if (firstToken) { IsThinking = false; firstToken = false; }
                        aiMessage.Text += token;
                    });

                    stopwatch.Stop();
                    _sessionInfo.AIResponseCount++;
                    _sessionInfo.Messages.Add(new MessageMetadata("assistant", response, stopwatch.ElapsedMilliseconds));

                    // Post-process: extract follow-ups from complete response
                    var (cleanedText, followUps) = ExtractFollowUpQuestions(response);
                    var contextFollowUps = GenerateContextualFollowUps();
                    foreach (var ctx in contextFollowUps)
                    {
                        if (followUps.Count >= 4) break;
                        if (!followUps.Any(f => f.Equals(ctx, StringComparison.OrdinalIgnoreCase)))
                            followUps.Add(ctx);
                    }

                    aiMessage.Text = cleanedText;
                    aiMessage.FollowUpQuestions = followUps;
                    PersistMessageAsync("assistant", cleanedText, (int)stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    // Non-streaming fallback
                    response = await _aiProvider.SendMessageAsync(userText);
                    stopwatch.Stop();
                    _sessionInfo.AIResponseCount++;
                    _sessionInfo.Messages.Add(new MessageMetadata("assistant", response, stopwatch.ElapsedMilliseconds));

                    var (cleanedText, followUps) = ExtractFollowUpQuestions(response);
                    var contextFollowUps = GenerateContextualFollowUps();
                    foreach (var ctx in contextFollowUps)
                    {
                        if (followUps.Count >= 4) break;
                        if (!followUps.Any(f => f.Equals(ctx, StringComparison.OrdinalIgnoreCase)))
                            followUps.Add(ctx);
                    }

                    Messages.Add(new ChatMessage
                    {
                        Text = cleanedText,
                        IsUser = false,
                        Timestamp = DateTime.Now,
                        FollowUpQuestions = followUps
                    });
                    PersistMessageAsync("assistant", cleanedText, (int)stopwatch.ElapsedMilliseconds);
                }

                AiDiagLog.AiResponse(response, stopwatch.ElapsedMilliseconds);
                AiDiagLog.FullConversationEntry(
                    userText, response, stopwatch.ElapsedMilliseconds,
                    _aiProvider.ProviderName, _cachedModelGuid);

                // ── Local DB query: flexible path (AI-generated SQL) ──
                if (isDbQuery && _dbQueryService != null)
                {
                    var sql = LocalDbQueryService.ExtractSqlFromResponse(response);
                    if (sql != null)
                    {
                        _logger?.LogDebug($"[ZestAI] DB flexible path: AI generated SQL: {sql}");
                        var queryResult = _dbQueryService.ExecuteReadOnlyQuery(sql, _isAdmin);

                        // Second AI call with results — non-streaming for simplicity
                        _aiProvider.SetModelContext($"DATABASE QUERY RESULTS:\n{queryResult}");
                        var finalResponse = await _aiProvider.SendMessageAsync(
                            "Based on these database query results, provide a clear and concise answer to the user's original question. Be precise with dates, numbers, and names.");
                        _aiProvider.SetModelContext(null);

                        var (finalClean, finalFollowUps) = ExtractFollowUpQuestions(finalResponse);

                        // Update the last AI message with the final answer
                        if (Messages.Count > 0 && !Messages[Messages.Count - 1].IsUser)
                        {
                            Messages[Messages.Count - 1].Text = finalClean;
                            Messages[Messages.Count - 1].FollowUpQuestions = finalFollowUps;
                        }

                        PersistMessageAsync("assistant", finalClean);
                        AiDiagLog.FullConversationEntry(
                            $"[DB Follow-up] {userText}", finalResponse, 0,
                            _aiProvider.ProviderName, _cachedModelGuid);
                    }

                    // Clean up context after DB query
                    _aiProvider.SetModelContext(null);
                }
            }
            catch (AiQuotaExceededException)
            {
                _logger?.LogWarning("ZestAI: AI usage limit reached");
                WpfMessageBox.Show(
                    "Your Zest AI usage limit has been reached.\n\nPlease contact ZestineTech support at support@zestinetech.com for assistance.",
                    "Zest AI — Limit Reached",
                    WpfMessageBoxButton.OK,
                    WpfMessageBoxImage.Information);
            }
            catch (AiNetworkUnavailableException ex)
            {
                // Network/connectivity failure — show a friendly bubble, keep the chat open,
                // restore the user's typed message so they can press Send again without retyping.
                _logger?.LogWarning($"ZestAI: AI service unreachable — {ex.Message}");

                // Drop the empty streaming bubble (if any) that was added before we knew it would fail.
                if (Messages.Count > 0 && !Messages[Messages.Count - 1].IsUser
                    && string.IsNullOrEmpty(Messages[Messages.Count - 1].Text))
                {
                    Messages.RemoveAt(Messages.Count - 1);
                }

                Messages.Add(new ChatMessage
                {
                    Text = "Ze AI is unavailable right now — I couldn't reach the AI service. " +
                           "Please check your internet connection and try again in a moment. " +
                           "Your message has been kept in the input box so you can resend it.",
                    IsUser = false,
                    Timestamp = DateTime.Now
                });

                // Restore the typed text so the user can hit Send again.
                UserMessage = userText;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"ZestAiViewModel.SendMessageAsync error: {ex.Message}", ex);
                Messages.Add(new ChatMessage
                {
                    Text      = $"Error: {ex.Message}",
                    IsUser    = false,
                    Timestamp = DateTime.Now
                });
            }
            finally
            {
                IsSending  = false;
                IsThinking = false;
            }
        }

        /// <summary>
        /// If the query looks like a model-analysis question, fetches (or reuses) live metrics
        /// and calls <see cref="IAIProvider.SetModelContext"/> so the data gets injected into
        /// the AI system prompt — NOT the user message (which the provider adds as a separate content item).
        /// Visibility queries are handled first using a pre-computed block captured on the main thread.
        /// </summary>
        /// <param name="preComputedVisibilityBlock">
        /// Visibility diagnosis already computed on the Revit main thread before this async method was called.
        /// Revit API calls (IsHidden, GetCategoryHidden, etc.) must NOT be made here — wrong thread.
        /// </param>
        private async System.Threading.Tasks.Task InjectModelContextAsync(
            string userText,
            string? preComputedVisibilityBlock = null)
        {
            AiDiagLog.QueryReceived(userText,
                isModelQuery: _modelContextService?.IsModelAnalysisQuery(userText) ?? false,
                modelGuid:    _cachedModelGuid);

            // ── Visibility query: use the pre-computed block (captured on main thread) ──
            if (preComputedVisibilityBlock != null)
            {
                _logger?.LogInfo($"[ZestAI] Element visibility diagnosis injected ({preComputedVisibilityBlock.Length} chars)");
                _aiProvider.SetModelContext(preComputedVisibilityBlock);
                AiDiagLog.ContextInjected(preComputedVisibilityBlock.Length);
                return;
            }

            if (_modelContextService == null || string.IsNullOrEmpty(_cachedModelGuid))
            {
                AiDiagLog.ContextSkipped(
                    _modelContextService == null ? "ModelContextService is null (not wired in DI)" : "model GUID is null");
                return;
            }

            // For non-model questions, clear any previously injected context
            if (!_modelContextService.IsModelAnalysisQuery(userText))
            {
                _aiProvider.SetModelContext(null);
                return;
            }

            try
            {
                // Fetch once per session, reuse after that
                if (_cachedContext == null)
                {
                    _logger?.LogInfo("[ZestAI] Model analysis question detected — fetching live metrics...");
                    _cachedContext = await _modelContextService.GetModelContextAsync(
                        _cachedModelGuid, _revitDoc?.Title);
                }

                if (_cachedContext == null || !_cachedContext.HasAnyData)
                {
                    _logger?.LogWarning("[ZestAI] No metrics data found for this model GUID — AI will answer generically");
                    _aiProvider.SetModelContext(null);
                    return;
                }

                var contextBlock = _modelContextService.FormatSlimContextBlock(_cachedContext);

                // Suppress thin context — empirically anything under ~500 chars is just
                // metadata stubs ("Model: Testing | GUID: abc | Last sync: …") with no
                // actual metric data. Injecting it makes the LLM think it has model
                // awareness and confidently hallucinate counts. Silence is better.
                // The 1741-char syncsave-with-warnings case still injects fine.
                const int MinUsefulContextChars = 500;
                if (contextBlock.Length < MinUsefulContextChars)
                {
                    _logger?.LogInfo($"[ZestAI] Context too thin ({contextBlock.Length} chars < {MinUsefulContextChars}) — suppressing injection so AI doesn't hallucinate from incomplete data");
                    _aiProvider.SetModelContext(null);
                    return;
                }

                _logger?.LogInfo($"[ZestAI] Injecting {contextBlock.Length} chars of slim model context into system prompt");
                _aiProvider.SetModelContext(contextBlock);
                AiDiagLog.ContextInjected(contextBlock.Length);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[ZestAI] Context injection failed (non-critical): {ex.Message}");
                _aiProvider.SetModelContext(null);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Smart follow-ups from model context
        // ──────────────────────────────────────────────────────────────────────

        private List<string> GenerateContextualFollowUps()
        {
            var suggestions = new List<string>();
            if (_cachedContext == null) return suggestions;

            var ss = _cachedContext.LatestSyncSave;
            if (ss != null)
            {
                if (ss.WarningsCount > 10)
                    suggestions.Add($"How do I resolve the {ss.WarningsCount} warnings in my model?");
                if (ss.FileSizeBytes > 200_000_000)
                    suggestions.Add($"My model is {ss.FileSizeBytes / 1_000_000}MB — how can I reduce the file size?");
                if (ss.DuplicateElementsCount > 0)
                    suggestions.Add($"What's the best way to fix {ss.DuplicateElementsCount} duplicate elements?");
                if (ss.ImportedDwgCount > 3)
                    suggestions.Add($"I have {ss.ImportedDwgCount} imported DWGs — should I link them instead?");
            }

            var manual = _cachedContext.LatestManual;
            if (manual != null)
            {
                if (manual.PurgeableElementsCount > 0)
                    suggestions.Add($"How do I purge the {manual.PurgeableElementsCount} unused elements?");
                if (manual.FamiliesOver5mbCount > 0)
                    suggestions.Add($"How can I optimize the {manual.FamiliesOver5mbCount} families over 5MB?");
            }

            var periodic = _cachedContext.LatestPeriodic;
            if (periodic != null)
            {
                if (periodic.UnenclosedRoomsCount > 0)
                    suggestions.Add($"How do I fix {periodic.UnenclosedRoomsCount} unenclosed rooms?");
                if (periodic.ViewsNotOnSheetsCount > 10)
                    suggestions.Add($"I have {periodic.ViewsNotOnSheetsCount} views not on sheets — is that a problem?");
                var disconnected = periodic.WallsNotConnectedCount + periodic.PipesNotConnectedCount + periodic.DuctsNotConnectedCount;
                if (disconnected > 0)
                    suggestions.Add($"How do I fix {disconnected} disconnected elements (walls/pipes/ducts)?");
            }

            return suggestions.Take(3).ToList();
        }

        // ──────────────────────────────────────────────────────────────────────
        // Conversation export
        // ──────────────────────────────────────────────────────────────────────

        private void ExportChat()
        {
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                    FileName = $"ZestAI_Chat_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                    Title = "Export Chat"
                };

                if (dialog.ShowDialog() == true)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"Zest AI Chat Export - {DateTime.Now:yyyy-MM-dd HH:mm}");
                    sb.AppendLine($"Model: {_revitDoc?.Title ?? "N/A"}");
                    sb.AppendLine(new string('=', 60));

                    foreach (var msg in Messages)
                    {
                        var role = msg.IsUser ? "You" : "Zest AI";
                        sb.AppendLine($"\n[{msg.Timestamp:HH:mm}] {role}:");
                        sb.AppendLine(msg.Text);
                    }

                    File.WriteAllText(dialog.FileName, sb.ToString());
                    _logger?.LogInfo($"[ZestAI] Chat exported to: {dialog.FileName}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ZestAI] Chat export failed: {ex.Message}", ex);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Chat history persistence
        // ──────────────────────────────────────────────────────────────────────

        private async void InitializeChatSessionAsync()
        {
            if (_chatRepository == null) return;
            try
            {
                _chatSessionId = await _chatRepository.CreateSessionAsync(
                    null, _cachedModelGuid, _revitDoc?.Title, Environment.UserName);
                _logger?.LogDebug($"[ZestAI] Chat session created: {_chatSessionId}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ZestAI] Failed to create chat session: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Closes the currently-active chat session and starts a fresh one. Called by
        /// the dialog's "New Chat" button.
        ///
        /// Why this exists: tester reported that clicking New Chat mid-conversation
        /// (without first closing the Ze AI dialog) made the prior conversation
        /// disappear from the history panel. Two reasons:
        ///   1. We never called EndSession on the old row, so its end_at stayed NULL
        ///      and the recents query treated it as in-progress.
        ///   2. _chatSessionId still pointed at the old row, and
        ///      RefreshRecentSessionsAsync skips whatever session matches that id
        ///      ("don't show the chat you're already in").
        /// Combined: the old chat was invisible until the dialog closed and reopened.
        /// This method ends the old session, clears the id, then creates a new row,
        /// so the old session shows up in history immediately on next refresh.
        /// </summary>
        public async System.Threading.Tasks.Task StartNewChatSessionAsync()
        {
            if (_chatRepository == null) return;
            try
            {
                if (!string.IsNullOrEmpty(_chatSessionId))
                {
                    await _chatRepository.EndSessionAsync(_chatSessionId);
                    _logger?.LogDebug($"[ZestAI] Closed chat session {_chatSessionId} before starting new chat");
                }

                _chatSessionId = await _chatRepository.CreateSessionAsync(
                    null, _cachedModelGuid, _revitDoc?.Title, Environment.UserName);
                _logger?.LogInfo($"[ZestAI] New chat session created: {_chatSessionId}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ZestAI] StartNewChatSessionAsync failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Loads the last 5 chat sessions for the current Revit model into <see cref="RecentSessions"/>.
        /// Called by the dialog when the user opens the History popup. Safe to call repeatedly.
        /// </summary>
        public async System.Threading.Tasks.Task RefreshRecentSessionsAsync()
        {
            if (_chatRepository == null) return;
            try
            {
                var sessions = await _chatRepository.GetRecentSessionsForModelAsync(_cachedModelGuid, 5);

                // Exclude the in-progress session (no point offering to resume the one we're already in)
                RecentSessions.Clear();
                foreach (var s in sessions)
                {
                    if (s.ChatSessionId == _chatSessionId) continue;
                    RecentSessions.Add(s);
                }
                _logger?.LogDebug($"[ZestAI] Loaded {RecentSessions.Count} recent sessions for model {_cachedModelGuid ?? "(none)"}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ZestAI] Failed to load recent sessions: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Replaces the current conversation with the messages from a past session and
        /// switches the active chat session ID so subsequent messages append to that session.
        /// Triggered by clicking an item in the history popup.
        /// </summary>
        public async System.Threading.Tasks.Task ResumeSessionAsync(string chatSessionId)
        {
            if (_chatRepository == null || string.IsNullOrEmpty(chatSessionId)) return;
            try
            {
                var history = await _chatRepository.GetSessionMessagesAsync(chatSessionId);
                if (history.Count == 0)
                {
                    _logger?.LogWarning($"[ZestAI] Tried to resume empty session {chatSessionId}");
                    return;
                }

                Messages.Clear();
                _aiProvider.ClearConversation();

                foreach (var m in history)
                {
                    Messages.Add(new ChatMessage
                    {
                        Text = m.Content,
                        IsUser = m.Role == "user",
                        Timestamp = m.Timestamp,
                        ChatMessageId = m.ChatMessageId,
                        FeedbackRating = m.FeedbackRating
                    });
                }

                _chatSessionId = chatSessionId;
                // Resumed sessions already have a title from when they were first created.
                // Mark as already-set so the next user message doesn't overwrite it.
                _sessionsWithTitleSet.Add(chatSessionId);
                _logger?.LogInfo($"[ZestAI] Resumed session {chatSessionId} with {history.Count} messages");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ZestAI] Failed to resume session: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Deletes a single chat session from the local DB and removes it from the
        /// RecentSessions UI collection if present. Does not touch the currently-active
        /// session — if you delete the in-progress chat, a new session is created on next
        /// send so the conversation isn't lost mid-stream.
        /// </summary>
        public async System.Threading.Tasks.Task DeleteSessionAsync(string chatSessionId)
        {
            if (_chatRepository == null || string.IsNullOrEmpty(chatSessionId)) return;
            try
            {
                await _chatRepository.DeleteSessionAsync(chatSessionId);
                for (int i = RecentSessions.Count - 1; i >= 0; i--)
                {
                    if (RecentSessions[i].ChatSessionId == chatSessionId)
                        RecentSessions.RemoveAt(i);
                }
                // If the user deleted the session they're currently chatting in, drop the
                // active-session reference so the next message starts a fresh row.
                if (_chatSessionId == chatSessionId)
                {
                    _chatSessionId = null;
                    _logger?.LogInfo("[ZestAI] Active chat session was deleted; a new one will be created on next send");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ZestAI] DeleteSessionAsync failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Deletes every chat session for the currently-open Revit model. The in-progress
        /// session (if any) is dropped from <see cref="_chatSessionId"/> so the next send
        /// creates a fresh row. UI messages are NOT cleared — that's the caller's choice;
        /// here we only touch persisted history.
        /// </summary>
        public async System.Threading.Tasks.Task ClearAllHistoryForCurrentModelAsync()
        {
            if (_chatRepository == null) return;
            try
            {
                var deleted = await _chatRepository.DeleteAllSessionsForModelAsync(_cachedModelGuid);
                _logger?.LogInfo($"[ZestAI] Cleared {deleted} chat session(s) for model {_cachedModelGuid ?? "(none)"}");
                RecentSessions.Clear();
                _chatSessionId = null;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ZestAI] ClearAllHistoryForCurrentModelAsync failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Checks model health asynchronously and updates the welcome message with alerts.
        /// Called from constructor — fire-and-forget to avoid blocking UI.
        /// </summary>
        private async void CheckHealthAlertsAsync()
        {
            if (_modelContextService == null || string.IsNullOrEmpty(_cachedModelGuid))
                return;

            try
            {
                var alertService = new ModelHealthAlertService(_modelContextService, _logger);
                _healthAlerts = await alertService.CheckHealthAsync(_cachedModelGuid, _revitDoc?.Title);

                // Publish to runtime context so the `get_health_alerts` tool can read live values
                // without needing DI access to ModelContextService. See ZeManageRuntimeContext.
                BIManage.AI.Terminal.ZeManageRuntimeContext.SetHealthAlerts(_cachedModelGuid, _healthAlerts);

                // Cache the context result for smart follow-ups
                if (_cachedContext == null)
                    _cachedContext = await _modelContextService.GetModelContextAsync(_cachedModelGuid, _revitDoc?.Title);

                if (_healthAlerts != null && _healthAlerts.Count > 0)
                {
                    var alertSummary = ModelHealthAlertService.FormatWelcomeAlertSummary(_healthAlerts);
                    _logger?.LogInfo($"[ZestAI] Health alerts: {_healthAlerts.Count} issue(s) found");

                    // Update the welcome message to mention health issues
                    if (Messages.Count > 0 && !Messages[0].IsUser)
                    {
                        Messages[0].Text += alertSummary + " Ask me to analyze them!";
                    }

                    // Inject alert context so AI is aware even without explicit questions
                    var alertBlock = ModelHealthAlertService.FormatAlertsForPrompt(_healthAlerts);
                    _aiProvider.SetModelContext(alertBlock);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[ZestAI] Health alert check failed (non-critical): {ex.Message}");
            }
        }

        private async void PersistMessageAsync(string role, string content, int? responseTimeMs = null)
        {
            if (_chatRepository == null || string.IsNullOrEmpty(_chatSessionId)) return;
            try
            {
                var messageId = await _chatRepository.SaveMessageAsync(
                    _chatSessionId, role, content, responseTimeMs);

                // Auto-set session title from the first user message of this chat session.
                // Tracked per-_chatSessionId (not per-window) so each new chat gets its
                // own title, not "(no title)" — see _sessionsWithTitleSet field comment.
                if (role == "user" && !_sessionsWithTitleSet.Contains(_chatSessionId))
                {
                    var title = content.Length > 60 ? content.Substring(0, 57) + "..." : content;
                    await _chatRepository.UpdateSessionTitleAsync(_chatSessionId, title);
                    _sessionsWithTitleSet.Add(_chatSessionId);
                }

                // Store the message ID on the latest ChatMessage for feedback tracking
                if (Messages.Count > 0)
                {
                    var lastMsg = Messages[Messages.Count - 1];
                    if (!lastMsg.IsUser || role == "user")
                        lastMsg.ChatMessageId = messageId;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ZestAI] Failed to persist message: {ex.Message}", ex);
            }
        }

        public async void EndChatSessionAsync()
        {
            if (_chatRepository == null || string.IsNullOrEmpty(_chatSessionId)) return;
            try
            {
                await _chatRepository.EndSessionAsync(_chatSessionId);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ZestAI] Failed to end chat session: {ex.Message}", ex);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Session persistence (JSON — legacy, kept for backward compat)
        // ──────────────────────────────────────────────────────────────────────

        public void SaveSessionToJson()
        {
            try
            {
                _sessionInfo.EndSession();

                var sessionFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BIManageRevit", "AI", "Sessions");

                Directory.CreateDirectory(sessionFolder);

                var fileName = $"session_{_sessionInfo.SessionStart:yyyyMMdd_HHmmss}.json";
                var filePath = Path.Combine(sessionFolder, fileName);

                var options = new JsonSerializerOptions
                {
                    WriteIndented       = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                File.WriteAllText(filePath, JsonSerializer.Serialize(_sessionInfo, options));
                _logger?.LogDebug($"[ZestAI] Session saved to: {filePath}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ZestAI] Error saving session: {ex.Message}", ex);
            }
        }

        private string? GetPreformedAnswerIfOffTopic(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var lower = text.ToLowerInvariant();

            // 1. Identity & Small Talk (Save tokens, don't send to AI)
            if (lower.Contains("favourite color") || lower.Contains("how are you") || lower.Contains("who are you") || lower.Contains("what is your name"))
            {
                return "I am your Ze AI Assistant, specialized in Revit and BIM workflows. I don't have personal feelings, but I'm ready to help you with your project!";
            }

            // 2. Hard Off-Topic Block (Politics, recipes, general knowledge, etc.)
            // KEEP this list TIGHT \u2014 only terms that have no plausible Revit interpretation.
            var offTopicKeywords = new[] { "prime minister", "president", "weather", "boil water", "recipe", "movie", "song", "joke", "politics", "religion" };
            if (offTopicKeywords.Any(k => lower.Contains(k)))
            {
                return "I'm designed to assist specifically with Revit and Engineering queries. I cannot provide information on general topics, politics, or personal advice. How can I help with your Revit model today?";
            }

            // 3. Simple Pre-programmed Revit Answers (Fast path, 0 tokens)
            if (lower == "what can you do?" || lower == "help")
            {
                return "I can help you with:\n\u2022 Troubleshooting Revit visibility issues\n\u2022 Explaining BIM best practices\n\u2022 Analyzing your model's health and warnings\n\u2022 Finding specific family or parameter information\n\nWhat would you like to start with?";
            }

            if (lower.Contains("purge unused") && (lower.Contains("how") || lower.Contains("what")))
            {
                return "Purging unused elements helps reduce file size. Go to the **Manage** tab > **Purge Unused**. It's best practice to run this 3 times to clear nested dependencies. Note: It won't remove elements currently in use or loaded as 'essential' by some families.";
            }

            // 4. (Removed) \u2014 Previously we blocked any prompt > 4 words that didn't contain a
            //    fixed Revit keyword. This rejected legitimate questions like "How do I copy a
            //    wall to multiple levels?" (when "wall" stem mismatched), "How to draw duct",
            //    "what is BIM", etc. The LLM's system prompt already enforces Revit/BIM scope,
            //    and the IsOffTopicAsync check still runs upstream with the BaselineRevitKeywords
            //    floor. Letting the LLM decide is more accurate AND cheaper in user frustration
            //    than the false-positive rate of this heuristic. The hard-block list above (2.)
            //    still catches the genuinely off-topic cases like politics/recipes.

            return null;
        }
    }
}
