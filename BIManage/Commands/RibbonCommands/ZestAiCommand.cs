using System;
using System.Diagnostics;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.AI;
using BIManage.AI.Interfaces;
using BIManage.AI.Knowledge;
using BIManage.AI.Providers;
using BIManage.AI.Terminal;
using BIManage.Core.Identity;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;
using BIManage.ViewModels.AI;
using BIManageRevit.BIManage.Views.AI;

namespace BIManageRevit.Commands.RibbonCommands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class ZestAiCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            if (LicenseGuard.IsBlockedByLicense(commandData)) return Result.Succeeded;

            ILogger? logger = null;
            try
            {
                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;

                if (services == null)
                {
                    TaskDialog.Show("Zest AI", "Unable to access application services.");
                    return Result.Failed;
                }

                logger = services.GetService<ILogger>();

                var aiProvider = services.GetService<IAIProvider>();
                if (aiProvider == null)
                {
                    TaskDialog.Show("Zest AI",
                        "AI services are not configured.\n\n" +
                        "Please check that AI provider settings are configured in App.config.");
                    return Result.Failed;
                }

                // Phase 3c-NEW: enable OpenAI function calling for the chat session.
                // Discovers all 10 AI Terminal tools and wires them into the provider so the
                // model can query the live Revit document mid-conversation. If the provider
                // isn't the OpenAI one, this no-ops — every other provider keeps its old
                // behavior unchanged.
                try
                {
                    if (aiProvider is OpenAIProvider openAi)
                    {
                        var toolRegistry = ToolRegistry.DiscoverAndCreate(commandData.Application);
                        openAi.SetToolRegistry(toolRegistry);
                        logger?.LogInfo($"[ZestAiCommand] Tool calling enabled with {toolRegistry.Count} tool(s)");
                    }
                }
                catch (Exception toolEx)
                {
                    // Tool calling is additive — if discovery fails for any reason, fall back to
                    // the pre-Phase-3c chat experience instead of blocking the user from chatting.
                    logger?.LogWarning($"[ZestAiCommand] Tool calling could not be initialised — chat will run without tools. {toolEx.Message}");
                }

                // Set role-based product knowledge access on the knowledge providers
                try
                {
                    var userService = services.GetService<IUserService>();
                    var jsonKnowledge = services.GetService<JsonKnowledgeProvider>();
                    if (jsonKnowledge != null && userService != null)
                        jsonKnowledge.IsAdmin = userService.HasAdminPrivileges;

                    var knowledgeProvider = services.GetService<IKnowledgeProvider>();
                    if (knowledgeProvider is ApiKnowledgeProvider apiKnowledge && userService != null)
                        apiKnowledge.IsAdmin = userService.HasAdminPrivileges;
                }
                catch { /* non-critical — defaults to normal user access */ }

                // Resolve the ModelContextService (may be null if API not configured — ViewModel handles this gracefully)
                var httpClient      = services.GetService<AuthenticatedHttpClient>();
                var modelContextSvc = httpClient != null ? new ModelContextService(httpClient, logger) : null;

                // Resolve ChatRepository for persistent chat history
                var chatRepo = services.GetService<ChatRepository>();

                // Resolve LocalDbQueryService for AI database queries
                var dbQueryService = services.GetService<LocalDbQueryService>();

                // Determine admin status for role-based access
                bool isAdmin = false;
                try
                {
                    var userSvc = services.GetService<IUserService>();
                    isAdmin = userSvc?.HasAdminPrivileges ?? false;
                }
                catch { /* defaults to false */ }

                // Get the active Revit document and UIDocument (ActiveView is read fresh on each query)
                var uidoc = commandData.Application.ActiveUIDocument;
                var doc   = uidoc?.Document;

                logger?.LogInfo("Opening Zest AI Assistant dialog");

                var viewModel = new ZestAiViewModel(aiProvider, logger, modelContextSvc, doc, uidoc, chatRepo, dbQueryService, isAdmin);

                try
                {
                    var username = commandData.Application.Application.Username;
                    viewModel.SetRevitUsername(username);
                }
                catch { /* fallback already set in ViewModel */ }

                var dialog = new ZestAiDialog(viewModel, logger);

                var helper = new WindowInteropHelper(dialog)
                {
                    Owner = Process.GetCurrentProcess().MainWindowHandle
                };

                dialog.Show();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                logger?.LogError($"ZestAiCommand failed: {ex.Message}", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
