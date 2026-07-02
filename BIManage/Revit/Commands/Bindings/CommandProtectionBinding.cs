using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIManage.Common.Helpers;
using BIManage.Core.Evidence;
using BIManage.Core.Protection.Models;
using BIManage.Core.Rules.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Context;
using BIManage.Revit.Helpers;
using BIManage.Revit.Protection;
using BIManage.Views.Protection;
using BIManage.ViewModels.Protection;
using BIManageRevit.BIManage.ViewModels.Protection;

namespace BIManage.Revit.Commands.Bindings
{
    /// <summary>
    /// Dynamic command protection binding using BeforeExecuted event only.
    /// Loads enabled commands from command_settings table (updated via SignalR).
    /// Supports Monitor/Guide/Prevent modes with screenshot capture and OTP override.
    /// </summary>
    public class CommandProtectionBinding
    {
        private readonly UIApplication _uiApp;
        private readonly ILogger? _logger;
        private readonly CommandProtectionRepository _repository;
        private readonly OtpRepository? _otpRepository;
        private readonly AuditRepository? _auditRepository;
        private readonly EvidenceRepository? _evidenceRepository;
        private readonly Func<IScreenshotService?>? _screenshotServiceGetter;
        private readonly Func<IRevitContext?>? _revitContextGetter;
        private readonly Func<bool> _isAdminCheck;
        private readonly CacheRepository? _cacheRepository;
        private readonly ICommandInterceptionService? _commandInterceptionService;

        // Thread-safe dictionary of active command bindings
        private readonly ConcurrentDictionary<string, CommandBindingInfo> _activeBindings = new();

        // Cache of command settings for quick lookup during BeforeExecuted
        private readonly ConcurrentDictionary<string, CommandSettingViewModel> _commandSettings = new();

        // UI-thread ManagedThreadId captured at construction time. All Revit API calls
        // (including the ExternalEvent that creates CommandProtectionBinding) happen on
        // this thread. Any caller of LoadAndRegisterCommands on a different thread is
        // off-UI and must be marshalled back via ExternalEvent — otherwise
        // CreateAddInCommandBinding registers without an AddInData scope and Revit
        // crashes with "Cannot find the host add-in which added this handler" on the
        // first user command. Field log BIManageRevit_20260523_1532.log line 416+
        // shows the API callback thread firing LoadAndRegisterCommands at 15:35:58
        // (after SetupProtectionBindingsExternalEvent already ended), and journal.0284
        // line 2569 shows the resulting Revit serious-error dialog.
        private readonly int _uiThreadId;
        private readonly ReloadHandler _reloadHandler;
        private readonly Autodesk.Revit.UI.ExternalEvent _reloadEvent;
        private int? _pendingReloadProjectId;
        private readonly object _pendingReloadLock = new object();

        public CommandProtectionBinding(
            UIApplication uiApp,
            ILogger? logger,
            CommandProtectionRepository repository,
            Func<bool> isAdminCheck,
            OtpRepository? otpRepository = null,
            AuditRepository? auditRepository = null,
            EvidenceRepository? evidenceRepository = null,
            Func<IScreenshotService?>? screenshotServiceGetter = null,
            Func<IRevitContext?>? revitContextGetter = null,
            CacheRepository? cacheRepository = null,
            ICommandInterceptionService? commandInterceptionService = null)
        {
            _uiApp = uiApp ?? throw new ArgumentNullException(nameof(uiApp));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _isAdminCheck = isAdminCheck ?? throw new ArgumentNullException(nameof(isAdminCheck));
            _logger = logger;
            _otpRepository = otpRepository;
            _auditRepository = auditRepository;
            _evidenceRepository = evidenceRepository;
            _screenshotServiceGetter = screenshotServiceGetter;
            _revitContextGetter = revitContextGetter;
            _cacheRepository = cacheRepository;
            _commandInterceptionService = commandInterceptionService;

            // Capture UI thread + create reload ExternalEvent.
            // CommandProtectionBinding is constructed inside SetupProtectionBindingsExternalEventHandler.Execute,
            // which is Revit's UI thread — so the captured thread id and the created
            // ExternalEvent are anchored to the correct thread.
            _uiThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            _reloadHandler = new ReloadHandler(this);
            try
            {
                _reloadEvent = Autodesk.Revit.UI.ExternalEvent.Create(_reloadHandler);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"CommandProtectionBinding: failed to create reload ExternalEvent — off-thread reloads will be dropped (fail-safe): {ex.Message}");
                _reloadEvent = null!;
            }
        }

        /// <summary>
        /// Marshals an off-UI-thread LoadAndRegisterCommands call to the Revit UI thread.
        /// Critical: <c>UIApplication.CreateAddInCommandBinding</c> can only be called from
        /// inside an AddInData scope (which exists on the UI thread under any IExternalEventHandler
        /// or BeforeExecuted callback). Calling it from a threadpool callback registers the
        /// binding with empty addin attribution, and Revit's AddInManager later throws a
        /// serious error when looking up the host addin. See file header comment.
        /// </summary>
        private sealed class ReloadHandler : Autodesk.Revit.UI.IExternalEventHandler
        {
            private readonly CommandProtectionBinding _owner;
            public ReloadHandler(CommandProtectionBinding owner) { _owner = owner; }
            public void Execute(UIApplication app)
            {
                int? projectId;
                lock (_owner._pendingReloadLock)
                {
                    projectId = _owner._pendingReloadProjectId;
                    _owner._pendingReloadProjectId = null;
                }
                _owner.LoadAndRegisterCommandsCore(projectId);
            }
            public string GetName() => "BIManage.CommandProtectionBinding.Reload";
        }

        /// <summary>
        /// Thin instance wrapper around <see cref="BIManage.Revit.Helpers.RevitWindowHelper.SetOwner"/>.
        /// Kept as an instance method so existing call sites (<c>SetRevitOwner(dialog)</c>)
        /// don't have to change. Routes through the centralized helper so there is exactly
        /// one source of truth for the WPF Owner HWND — see RevitWindowHelper for the
        /// root-cause analysis.
        /// </summary>
        private void SetRevitOwner(System.Windows.Window dialog)
            => BIManage.Revit.Helpers.RevitWindowHelper.SetOwner(dialog);

        /// <summary>
        /// Get user-friendly display name for intervention mode.
        /// </summary>
        private static string GetModeDisplayName(InterventionMode mode) => mode switch
        {
            InterventionMode.Notify => "Notify",
            InterventionMode.Assist => "Assist",
            InterventionMode.Protect => "Protect",
            _ => mode.ToString()
        };

        /// <summary>
        /// Sanitize settings that are incompatible with Notify mode.
        /// Notify mode has no dialog, so RequireComment cannot be collected.
        /// </summary>
        private void SanitizeNotifyModeSettings(CommandSettingViewModel setting)
        {
            if (setting.Mode == InterventionMode.Notify && setting.RequireComment)
            {
                setting.RequireComment = false;
                _logger?.LogDebug($"CommandProtection: Stripped RequireComment for '{setting.CommandCode}' — not applicable in Notify mode");
            }
        }

        /// <summary>
        /// Load command settings from database and register bindings.
        /// Call this on startup and when settings change via SignalR.
        /// </summary>
        public void LoadAndRegisterCommands(int? projectId = null)
        {
            // Off-UI-thread call (SignalR / HTTP / async API callback) — marshal to the
            // Revit UI thread via ExternalEvent. The actual registration must run inside
            // an AddInData scope, otherwise Revit's AddInManager loses the host-addin
            // attribution and throws on the first user command. See ReloadHandler comment
            // and the file-header explanation above.
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _uiThreadId)
            {
                if (_reloadEvent == null)
                {
                    _logger?.LogWarning("CommandProtectionBinding: off-thread LoadAndRegisterCommands dropped — no ExternalEvent available (will retry on next on-thread call).");
                    return;
                }
                lock (_pendingReloadLock)
                {
                    _pendingReloadProjectId = projectId;
                }
                try
                {
                    if (!_reloadEvent.IsPending) _reloadEvent.Raise();
                    _logger?.LogDebug($"CommandProtectionBinding: deferred reload to UI thread (caller tid={System.Threading.Thread.CurrentThread.ManagedThreadId}, uiTid={_uiThreadId}).");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"CommandProtectionBinding: ExternalEvent.Raise failed: {ex.Message}");
                }
                return;
            }
            LoadAndRegisterCommandsCore(projectId);
        }

        private void LoadAndRegisterCommandsCore(int? projectId)
        {
            var totalSw = Stopwatch.StartNew();
            try
            {
                _logger?.LogInfo("CommandProtectionBinding: Loading command settings from database...");

                // Load all enabled command settings from database
                var dbSw = Stopwatch.StartNew();
                var settings = _repository.LoadAllCommandSettings(projectId)
                    .Where(s => s.IsEnabled)
                    .ToList();
                dbSw.Stop();
                _logger?.LogInfo($"CommandProtectionBinding: Found {settings.Count} enabled commands (DB query: {dbSw.ElapsedMilliseconds}ms)");

                // Build lookup cache and set of enabled revit command names
                // OPTIMIZATION: Cache LookupCommandId results to avoid double API calls
                var lookupSw = Stopwatch.StartNew();
                var commandIdCache = new Dictionary<string, RevitCommandId>(StringComparer.OrdinalIgnoreCase);
                var enabledRevitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var setting in settings)
                {
                    var commandId = LookupCommandId(setting.CommandCode);
                    if (commandId != null)
                    {
                        commandIdCache[setting.CommandCode] = commandId;
                        enabledRevitNames.Add(commandId.Name);
                    }
                }
                lookupSw.Stop();
                _logger?.LogInfo($"CommandProtectionBinding: LookupCommandId loop took {lookupSw.ElapsedMilliseconds}ms for {settings.Count} commands");

                // Update settings cache and register new bindings (using cached lookups)
                var registerSw = Stopwatch.StartNew();
                foreach (var setting in settings)
                {
                    // Use cached commandId instead of looking up again
                    if (commandIdCache.TryGetValue(setting.CommandCode, out var cachedCommandId))
                    {
                        RegisterCommandWithCachedId(setting, cachedCommandId);
                    }
                }
                registerSw.Stop();
                _logger?.LogInfo($"CommandProtectionBinding: RegisterCommand loop took {registerSw.ElapsedMilliseconds}ms");

                // Unregister commands that are no longer enabled (from _activeBindings)
                var bindingsToRemove = _activeBindings
                    .Where(kvp => !enabledRevitNames.Contains(kvp.Key))
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var revitName in bindingsToRemove)
                {
                    UnregisterCommand(revitName);
                }

                // Also clean up settings cache for commands that are no longer enabled
                // This is important for individual binding commands (Delete, Move, etc.)
                // which are only in _commandSettings, not _activeBindings
                var settingsToRemove = _commandSettings.Keys
                    .Where(key => !enabledRevitNames.Contains(key))
                    .ToList();

                foreach (var key in settingsToRemove)
                {
                    _commandSettings.TryRemove(key, out _);
                    _logger?.LogDebug($"CommandProtectionBinding: Removed settings for {key}");
                }

                totalSw.Stop();
                _logger?.LogInfo($"CommandProtectionBinding: {_activeBindings.Count} bindings + {_commandSettings.Count - _activeBindings.Count} cached settings now active (TOTAL: {totalSw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                totalSw.Stop();
                _logger?.LogError($"CommandProtectionBinding: Failed to load commands ({totalSw.ElapsedMilliseconds}ms): {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Register a single command binding using BeforeExecuted event.
        /// Commands with individual bindings (Delete, Move, etc.) only get settings cached,
        /// their individual bindings call ProcessCommandBeforeExecution() instead.
        /// </summary>
        private void RegisterCommand(CommandSettingViewModel setting)
        {
            // Lookup RevitCommandId first - try multiple methods
            var commandId = LookupCommandId(setting.CommandCode);
            if (commandId == null)
            {
                _logger?.LogWarning($"CommandProtectionBinding: Command not found: {setting.CommandCode}");
                return;
            }
            RegisterCommandWithCachedId(setting, commandId);
        }

        /// <summary>
        /// Register a single command binding using a pre-cached RevitCommandId.
        /// OPTIMIZATION: Avoids redundant LookupCommandId calls when ID is already known.
        /// Commands with individual bindings (Delete, Move, etc.) only get settings cached,
        /// their individual bindings call ProcessCommandBeforeExecution() instead.
        /// </summary>
        private void RegisterCommandWithCachedId(CommandSettingViewModel setting, RevitCommandId commandId)
        {
            try
            {
                // Strip options that are incompatible with the current mode
                SanitizeNotifyModeSettings(setting);

                var commandCode = setting.CommandCode;

                // Use the actual Revit command ID name as the key
                var revitCommandName = commandId.Name;

                // Check if this command has an individual binding (Delete, Move, Rotate, Mirror, Pin, Unpin)
                // These commands should NOT register a new binding - they call ProcessCommandBeforeExecution() instead
                if (CommandBindingBase.HasIndividualBinding(revitCommandName))
                {
                    // Only update settings cache - individual binding will call ProcessCommandBeforeExecution()
                    _commandSettings[revitCommandName] = setting;
                    _logger?.LogInfo($"CommandProtectionBinding: Cached settings for {commandCode} -> {revitCommandName} (Mode: {GetModeDisplayName(setting.Mode)}) [has individual binding]");
                    return;
                }

                // Check if this command is already bound by CommandInterceptionService (centralized handler)
                // Creating a duplicate binding would override the centralized handler and break interception.
                // Instead, only cache settings — CommandInterceptionService calls ProcessCommandBeforeExecution().
                if (_commandInterceptionService != null && _commandInterceptionService.IsCommandMonitored(commandId))
                {
                    _commandSettings[revitCommandName] = setting;
                    _logger?.LogInfo($"CommandProtectionBinding: Cached settings for {commandCode} -> {revitCommandName} (Mode: {GetModeDisplayName(setting.Mode)}) [centrally monitored]");
                    return;
                }

                // Check if already registered by us (using Revit's command name)
                if (_activeBindings.ContainsKey(revitCommandName))
                {
                    // Update settings cache only
                    _commandSettings[revitCommandName] = setting;
                    _logger?.LogDebug($"CommandProtectionBinding: Updated settings for {commandCode} ({revitCommandName})");
                    return;
                }

                // Check if command can have binding
                if (!commandId.CanHaveBinding)
                {
                    _logger?.LogWarning($"CommandProtectionBinding: Command cannot have binding: {commandCode}");
                    return;
                }

                // Create binding with BeforeExecuted event ONLY
                var binding = _uiApp.CreateAddInCommandBinding(commandId);
                binding.BeforeExecuted += OnBeforeCommandExecuted;
                LogSubscriptionEvent("REGISTER-DYNAMIC", commandId);

                // Store binding info using both keys for proper lookup
                var bindingInfo = new CommandBindingInfo
                {
                    CommandCode = commandCode,      // Our code ("Delete")
                    CommandId = commandId,          // Revit's command ID object
                    Binding = binding
                };

                // Store using Revit's command name as key (this is what we get in OnBeforeCommandExecuted)
                _activeBindings[revitCommandName] = bindingInfo;
                _commandSettings[revitCommandName] = setting;

                _logger?.LogInfo($"CommandProtectionBinding: Registered {commandCode} -> {revitCommandName} (Mode: {GetModeDisplayName(setting.Mode)})");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"CommandProtectionBinding: Failed to register {setting.CommandCode}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Lookup RevitCommandId using multiple methods:
        /// 1. Try direct string lookup (for command IDs like "ID_FILE_SAVE_TO_CENTRAL")
        /// 2. Try parsing as PostableCommand enum (for names like "Delete", "Move", "Copy")
        /// </summary>
        private RevitCommandId? LookupCommandId(string commandCode)
        {
            if (string.IsNullOrEmpty(commandCode))
                return null;

            // Method 1: Direct string lookup for command IDs like "ID_FILE_SAVE_TO_CENTRAL"
            var commandId = RevitCommandId.LookupCommandId(commandCode);
            if (commandId != null)
            {
                _logger?.LogDebug($"CommandProtectionBinding: Found {commandCode} via LookupCommandId");
                return commandId;
            }

            // Method 2: Try parsing as PostableCommand enum name (from command_cache.member_name_24)
            if (Enum.TryParse<PostableCommand>(commandCode, ignoreCase: true, out var postableCommand))
            {
                commandId = RevitCommandId.LookupPostableCommandId(postableCommand);
                if (commandId != null)
                {
                    _logger?.LogDebug($"CommandProtectionBinding: Found {commandCode} via PostableCommand.{postableCommand}");
                    return commandId;
                }
            }

            // Method 3: Try command_cache table (member_name_24 → revit_command_id)
            var revitId = _cacheRepository?.GetRevitCommandIdByMemberName(commandCode);
            if (!string.IsNullOrEmpty(revitId))
            {
                commandId = RevitCommandId.LookupCommandId(revitId);
                if (commandId != null)
                {
                    _logger?.LogDebug($"CommandProtectionBinding: Found {commandCode} via command_cache → {revitId}");
                    return commandId;
                }
            }

            _logger?.LogWarning($"CommandProtectionBinding: Command not resolved: {commandCode}");
            return null;
        }

        /// <summary>
        /// Unregister a command binding.
        /// </summary>
        private void UnregisterCommand(string commandCode)
        {
            try
            {
                if (_activeBindings.TryRemove(commandCode, out var bindingInfo))
                {
                    bindingInfo.Binding.BeforeExecuted -= OnBeforeCommandExecuted;
                    LogSubscriptionEvent("UNREGISTER-DYNAMIC", bindingInfo.CommandId);
                    _commandSettings.TryRemove(commandCode, out _);
                    _logger?.LogInfo($"CommandProtectionBinding: Unregistered {commandCode}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"CommandProtectionBinding: Failed to unregister {commandCode}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Unregister all command bindings.
        /// </summary>
        public void UnregisterAll()
        {
            foreach (var code in _activeBindings.Keys.ToList())
            {
                UnregisterCommand(code);
            }

            _logger?.LogInfo("CommandProtectionBinding: All bindings unregistered");
        }

        /// <summary>
        /// Refresh settings for a specific command (called by SignalR).
        /// </summary>
        public void RefreshCommand(CommandSettingViewModel setting)
        {
            if (setting.IsEnabled)
            {
                RegisterCommand(setting);
            }
            else
            {
                // Find the revitCommandName by looking up the commandId
                var commandId = LookupCommandId(setting.CommandCode);
                if (commandId != null)
                {
                    UnregisterCommand(commandId.Name);
                }
            }
        }

        /// <summary>
        /// Result of pure-logic command evaluation. Used by the bypass detector
        /// (DocumentChanged path) which has no BeforeExecutedEventArgs to cancel.
        /// </summary>
        public sealed class CommandEvaluationResult
        {
            public bool HasProtection { get; init; }
            public bool Allowed { get; init; } = true;
            public string? Reason { get; init; }
            public InterventionMode Mode { get; init; }

            public static CommandEvaluationResult NotProtected { get; } = new() { HasProtection = false, Allowed = true };
        }

        /// <summary>
        /// Pure-logic evaluation: run Notify/Assist/Protect handlers for a command without
        /// requiring a BeforeExecutedEventArgs. Called by:
        ///  - <see cref="ProcessCommandBeforeExecution(BeforeExecutedEventArgs)"/> (individual bindings)
        ///  - <see cref="OnBeforeCommandExecuted"/> (centralized handler)
        ///  - ElementBypassDetector (DocumentChanged-driven drag/nudge/Ctrl+drag path)
        /// </summary>
        /// <param name="revitCommandName">Revit command name (e.g. "ID_OBJECTS_MOVE") used as cache key</param>
        /// <param name="document">Active document</param>
        /// <param name="bypassElements">Optional override element list. When null, uses current selection.
        /// The bypass detector passes modified/added ids from DocumentChanged here.</param>
        /// <param name="bypassSource">Optional gesture-source label (e.g. "Drag bypass", "Nudge bypass").
        /// When non-null, audit Reason entries are prefixed with this label so admin dashboards
        /// can distinguish ribbon vs gesture origin.</param>
        public CommandEvaluationResult EvaluateCommand(
            string revitCommandName,
            Document document,
            IList<Element>? bypassElements = null,
            string? bypassSource = null)
        {
            // Hoisted out of the try so the outer catch can read the configured Mode and
            // make the right fail-open vs fail-closed decision.
            CommandSettingViewModel? setting = null;
            try
            {
                if (string.IsNullOrEmpty(revitCommandName)) return CommandEvaluationResult.NotProtected;

                if (!_commandSettings.TryGetValue(revitCommandName, out setting))
                {
                    _logger?.LogDebug($"CommandProtectionBinding: No settings for {revitCommandName} (not protected)");
                    return CommandEvaluationResult.NotProtected;
                }

                if (document == null)
                {
                    _logger?.LogWarning($"CommandProtectionBinding: No active document for {revitCommandName}");
                    return CommandEvaluationResult.NotProtected;
                }

                var elements = bypassElements != null
                    ? bypassElements.ToList()
                    : GetSelectedElements(document);
                var username = GetRevitUsername();
                var isAdmin = _isAdminCheck();

                _logger?.LogInfo($"CommandProtectionBinding: Evaluating {revitCommandName} (Mode: {GetModeDisplayName(setting.Mode)}, Elements: {elements.Count}, BypassSource: {bypassSource ?? "<ribbon>"})");

                switch (setting.Mode)
                {
                    case InterventionMode.Notify:
                        HandleMonitorMode(setting, elements, username, isAdmin, document, bypassSource);
                        return new CommandEvaluationResult { HasProtection = true, Allowed = true, Mode = setting.Mode };

                    case InterventionMode.Assist:
                        bool userAllows = HandleGuideMode(setting, elements, username, isAdmin, document, bypassSource);
                        return new CommandEvaluationResult
                        {
                            HasProtection = true,
                            Allowed = userAllows,
                            Mode = setting.Mode,
                            Reason = userAllows ? null : "User cancelled (Assist)"
                        };

                    case InterventionMode.Protect:
                        // Fail-CLOSED in Protect mode. See BIManageRevit_20260522_1034.log:6877
                        // — the OTP dialog threw Win32Exception "Invalid window handle" and the
                        // outer catch returned NotProtected, so a protected Delete went through
                        // without OTP. Root cause of the WPF throw is still ambiguous (handle
                        // exhaustion vs stale Owner HWND vs dispatcher re-entrancy — see notes
                        // in the diff for why we can't pin it from the existing log alone),
                        // so the integrity guarantee here cannot depend on getting the WPF
                        // diagnosis right. Instead:
                        //   1. allowed defaults to FALSE — any exit path that doesn't
                        //      explicitly set it stays blocked.
                        //   2. The inner try wraps the dialog call. ANY throw -> stay blocked.
                        //   3. Logging + audit are EACH in their own catch so a logger or DB
                        //      failure cannot bubble out and turn this into fail-open.
                        bool allowed = false;
                        Exception? protectThrow = null;
                        try
                        {
                            allowed = HandlePreventMode(setting, elements, username, isAdmin, document, bypassSource);
                        }
                        catch (Exception protectEx)
                        {
                            allowed = false;            // explicit, even though already false
                            protectThrow = protectEx;
                        }
                        if (protectThrow != null)
                        {
                            // Diagnostic capture — the WPF "Invalid window handle" throw can
                            // come from at least 4 different causes (stale Owner HWND, USER
                            // handle exhaustion, dispatcher re-entrancy, cross-thread Owner).
                            // The fail-closed change above is correct regardless of cause.
                            // These fields make the NEXT occurrence diagnosable from the log
                            // alone instead of guessing again.
                            string diag = "diag=n/a";
                            try
                            {
                                var procHwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                                var apiHwnd = _uiApp?.MainWindowHandle ?? IntPtr.Zero;
                                var tid = System.Threading.Thread.CurrentThread.ManagedThreadId;
                                var apt = System.Threading.Thread.CurrentThread.GetApartmentState();
                                var disp = System.Windows.Threading.Dispatcher.FromThread(System.Threading.Thread.CurrentThread);
                                diag = $"procHwnd=0x{procHwnd.ToInt64():X} apiHwnd=0x{apiHwnd.ToInt64():X} tid={tid} apt={apt} hasDisp={disp != null} dispShutdown={disp?.HasShutdownStarted}";
                            }
                            catch { }
                            try
                            {
                                _logger?.LogError(
                                    $"CommandProtectionBinding: HandlePreventMode threw for {revitCommandName} — blocking command (fail-closed): {protectThrow.Message} | {diag}",
                                    protectThrow);
                            }
                            catch { /* never let logger crash the fail-closed path */ }
                            try
                            {
                                LogAuditEntry(setting, elements, username, isAdmin, "blocked",
                                    $"Protection dialog error (fail-closed): {protectThrow.GetType().Name}: {protectThrow.Message}",
                                    bypassSource: bypassSource);
                            }
                            catch { /* DB / audit-queue failures must not re-open the gate */ }
                        }
                        return new CommandEvaluationResult
                        {
                            HasProtection = true,
                            Allowed = allowed,
                            Mode = setting.Mode,
                            Reason = allowed ? null : (protectThrow != null ? "Blocked (Protect: dialog error)" : "Blocked (Protect)")
                        };
                }

                return CommandEvaluationResult.NotProtected;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"CommandProtectionBinding: Error in EvaluateCommand: {ex.Message}", ex);
                // Outer catch covers everything BEFORE the switch decides the mode (setting
                // lookup, GetSelectedElements, etc.) OR anything that escaped the inner
                // mode-specific catches. If we know the configured mode was Protect OR
                // Assist (both have dialogs that can throw), fail CLOSED — return a blocked
                // result with HasProtection=true so the caller cancels the command.
                // NotProtected here only when we genuinely don't know the mode (e.g. setting
                // lookup itself failed) or the mode is Notify (no dialog, no risk).
                if (setting != null &&
                    (setting.Mode == InterventionMode.Protect || setting.Mode == InterventionMode.Assist))
                {
                    var modeLabel = setting.Mode == InterventionMode.Protect ? "Protect" : "Assist";
                    return new CommandEvaluationResult
                    {
                        HasProtection = true,
                        Allowed = false,
                        Mode = setting.Mode,
                        Reason = $"Blocked ({modeLabel}: pre-handler error, fail-closed)"
                    };
                }
                return CommandEvaluationResult.NotProtected;
            }
        }

        /// <summary>
        /// Process command before execution - called by individual command bindings (Delete, Move, etc.)
        /// that already have their own BeforeExecuted handlers.
        /// Thin wrapper around <see cref="EvaluateCommand"/> that applies the result to e.Cancel.
        /// </summary>
        public void ProcessCommandBeforeExecution(BeforeExecutedEventArgs e)
        {
            var commandId = e.CommandId;
            if (commandId == null) return;

            _logger?.LogInfo($">>> CommandProtectionBinding.ProcessCommandBeforeExecution: {commandId.Name}");

            var result = EvaluateCommand(commandId.Name, e.ActiveDocument);
            if (!result.Allowed && e.Cancellable)
            {
                e.Cancel = true;
                _logger?.LogInfo($"CommandProtectionBinding: {commandId.Name} cancelled ({result.Mode}: {result.Reason})");
            }

            // Suppress the bypass detector only when the user was shown a dialog and made a
            // decision (Assist/Protect modes). Notify mode only logs silently — no dialog is
            // shown — so the bypass detector must still run for the actual operation.
            // Without this guard, a Notify-mode Command Protection for Align calls Suppress()
            // on every Align button click (even with 0 elements selected), which then blocks
            // the bypass detector from evaluating the element that actually gets aligned.
            if (result.HasProtection && !e.Cancel && result.Mode != InterventionMode.Notify)
            {
                BIManage.Revit.Protection.CommandBypassAuditGate.Suppress();
            }
        }

        /// <summary>
        /// BeforeExecuted event handler - the ONLY interception point for centrally-registered commands.
        /// Thin wrapper around <see cref="EvaluateCommand"/> that applies the result to e.Cancel.
        /// </summary>
        private void OnBeforeCommandExecuted(object sender, BeforeExecutedEventArgs e)
        {
            var commandId = e.CommandId;
            _logger?.LogInfo($">>> CommandProtectionBinding.OnBeforeCommandExecuted FIRED: {commandId?.Name ?? "null"}");
            if (commandId == null) return;

            var result = EvaluateCommand(commandId.Name, e.ActiveDocument);
            if (!result.Allowed && e.Cancellable)
            {
                e.Cancel = true;
                _logger?.LogInfo($"CommandProtectionBinding: {commandId.Name} cancelled ({result.Mode}: {result.Reason})");
            }

            // Same guard as ProcessCommandBeforeExecution: don't suppress for Notify mode.
            if (result.HasProtection && !e.Cancel && result.Mode != InterventionMode.Notify)
            {
                BIManage.Revit.Protection.CommandBypassAuditGate.Suppress();
            }
        }

        /// <summary>
        /// Handle Notify mode: Log action, capture screenshot if configured, allow command.
        /// </summary>
        private void HandleMonitorMode(
            CommandSettingViewModel setting,
            List<Element> elements,
            string username,
            bool isAdmin,
            Document document,
            string? bypassSource = null)
        {
            _logger?.LogInfo($"Notify mode: Logging {setting.CommandCode} by {username} ({elements.Count} elements)");

            // Always generate auditLogId upfront — PK for audit entry, FK for all evidence captures
            var auditLogId = Guid.NewGuid().ToString();
            if (setting.CaptureBeforeScreenshot)
                CaptureScreenshot(setting, elements, username, "before", document, auditLogId);

            // Log to audit repository with auditLogId
            LogAuditEntry(setting, elements, username, isAdmin, "allowed", null, auditLogId: auditLogId, bypassSource: bypassSource);

            // Schedule after screenshot using same auditLogId
            if (setting.CaptureAfterScreenshot)
                ScheduleAfterScreenshot(setting, elements, username, document, auditLogId);
        }

        /// <summary>
        /// Handle Assist mode: Show confirmation dialog, require acknowledgment.
        /// Returns true if user allows, false if user cancels.
        /// </summary>
        private bool HandleGuideMode(
            CommandSettingViewModel setting,
            List<Element> elements,
            string username,
            bool isAdmin,
            Document document,
            string? bypassSource = null)
        {
            _logger?.LogInfo($"Assist mode: Showing confirmation for {setting.CommandCode}");

            // Always generate auditLogId upfront — PK for audit entry, FK for all evidence captures
            var auditLogId = Guid.NewGuid().ToString();
            BitmapImage? beforeImage = null;
            if (setting.CaptureBeforeScreenshot)
                beforeImage = CaptureScreenshotForDialog(setting, elements, username, "before", document, auditLogId);

            // Build dialog message
            var message = !string.IsNullOrWhiteSpace(setting.CustomMessage)
                ? setting.CustomMessage
                : $"You are about to execute {setting.CommandName} on {elements.Count} element(s).\n\nPlease confirm you want to proceed with this action.";

            // Create rule evaluation result for the dialog
            var elementIds = elements.Select(e => e.Id).ToList();
            var evaluationResult = new RuleEvaluationResult
            {
                FinalMode = ProtectionMode.Assist,
                CombinedMessage = message,
                RequireComment = setting.RequireComment
            };

            // Add a synthetic matched rule for display
            evaluationResult.MatchedRules.Add(new Rule
            {
                Name = setting.CommandName,
                Description = $"Command protection for {setting.CommandCode}",
                Mode = ProtectionMode.Assist,
                Message = message
            });

            // Create and show the GuideDialog
            var dialog = new GuideDialog(
                evaluationResult,
                elementIds,
                commandName: setting.CommandName,
                commandDescription: $"Affecting {elements.Count} element(s)");

            // Configure as Assist mode with Command protection type
            dialog.SetAssistMode(ProtectionType.Command, setting.CommandName);

            // Set the before image if captured
            if (beforeImage != null)
            {
                dialog.SetBeforeImage(beforeImage);
            }

            // Set appropriate action type based on command
            var actionType = DetermineActionType(setting.CommandCode);
            dialog.SetActionType(actionType);

            // Fail-CLOSED on any dialog throw. See field log BIManageRevit_20260522_1228.log
            // line 28275 — Assist mode's ShowDialog threw "Invalid window handle" and the
            // outer catch returned NotProtected, so the user's Delete proceeded without the
            // confirmation they had configured. userAllows defaults to false so any throw
            // through this method keeps the command blocked. Logger and audit calls are
            // each in their own swallow-catch so a DB/IO failure cannot re-throw and turn
            // this back into fail-open.
            bool userAllows = false;
            string? userComment = null;
            Exception? guideThrow = null;
            try
            {
                SetRevitOwner(dialog);
                var dialogResult = dialog.ShowDialog();
                userAllows = dialogResult == true && dialog.UserAllowed;
                try { userComment = dialog.UserComment; } catch { }
            }
            catch (Exception guideEx)
            {
                userAllows = false;
                guideThrow = guideEx;
            }

            if (guideThrow != null)
            {
                // Diagnostic capture — wrapped in its own try so a probe failure during
                // teardown cannot re-enter the catch. Mirrors HandlePreventMode's capture.
                string diag = "diag=n/a";
                try
                {
                    var procHwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                    var apiHwnd = _uiApp?.MainWindowHandle ?? IntPtr.Zero;
                    var cachedHwnd = BIManage.Revit.Helpers.RevitWindowHelper.GetOwnerHwnd();
                    var tid = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    var apt = System.Threading.Thread.CurrentThread.GetApartmentState();
                    var disp = System.Windows.Threading.Dispatcher.FromThread(System.Threading.Thread.CurrentThread);
                    diag = $"procHwnd=0x{procHwnd.ToInt64():X} apiHwnd=0x{apiHwnd.ToInt64():X} cachedHwnd=0x{cachedHwnd.ToInt64():X} tid={tid} apt={apt} hasDisp={disp != null} dispShutdown={disp?.HasShutdownStarted}";
                }
                catch { }
                try
                {
                    _logger?.LogError(
                        $"CommandProtectionBinding: HandleGuideMode dialog threw for {setting.CommandCode} — blocking command (fail-closed): {guideThrow.Message} | {diag}",
                        guideThrow);
                }
                catch { }
                try
                {
                    LogAuditEntry(setting, elements, username, isAdmin, "blocked",
                        $"Assist dialog error (fail-closed): {guideThrow.GetType().Name}: {guideThrow.Message}",
                        auditLogId: auditLogId,
                        bypassSource: bypassSource);
                }
                catch { }
                // Show the user a brief notice that the action was cancelled because the
                // dialog couldn't open — otherwise fail-closed Assist looks identical to
                // "command did nothing" and we get confused QA reports. Revit TaskDialog
                // doesn't depend on the broken Owner-HWND path so it shows reliably.
                try
                {
                    Autodesk.Revit.UI.TaskDialog.Show(
                        "ZeManage",
                        $"The confirmation dialog for '{setting.CommandName}' could not open. The action has been cancelled — please try again.");
                }
                catch { }
                return false;
            }

            // Log action with user comment
            var reason = userAllows ? "User confirmed" : "User cancelled";
            if (!string.IsNullOrWhiteSpace(userComment))
            {
                _logger?.LogInfo($"User comment: {userComment}");
            }

            LogAuditEntry(setting, elements, username, isAdmin,
                userAllows ? "allowed" : "cancelled",
                reason,
                userComment: userComment,
                auditLogId: auditLogId,
                bypassSource: bypassSource);

            // Capture after screenshot if user allowed and configured
            if (userAllows && setting.CaptureAfterScreenshot)
                ScheduleAfterScreenshot(setting, elements, username, document, auditLogId);

            return userAllows;
        }

        /// <summary>
        /// Determine action type based on command code for button styling.
        /// </summary>
        private ConfirmationActionType DetermineActionType(string commandCode)
        {
            var code = commandCode?.ToLowerInvariant() ?? "";

            // Delete/Remove actions (red button)
            if (code.Contains("delete") || code.Contains("remove") || code.Contains("demolish"))
                return ConfirmationActionType.Delete;

            // Unpin is also a potentially destructive action
            if (code.Contains("unpin") || code.Contains("unlock"))
                return ConfirmationActionType.Delete;

            // Modify actions (orange button)
            if (code.Contains("move") || code.Contains("rotate") || code.Contains("mirror") ||
                code.Contains("edit") || code.Contains("modify") || code.Contains("change"))
                return ConfirmationActionType.Modify;

            // Default to continue (blue button)
            return ConfirmationActionType.Continue;
        }

        /// <summary>
        /// Capture screenshot and return as BitmapImage for dialog preview.
        /// </summary>
        private BitmapImage? CaptureScreenshotForDialog(
            CommandSettingViewModel setting,
            List<Element> elements,
            string username,
            string stage,
            Document document,
            string? auditLogId)
        {
            var screenshotService = _screenshotServiceGetter?.Invoke();
            if (screenshotService == null)
            {
                _logger?.LogDebug("Screenshot service not available for dialog preview");
                return null;
            }

            try
            {
                // Capture as Base64
                var imageBase64 = screenshotService.CaptureRevitWindowAsBase64();

                if (string.IsNullOrEmpty(imageBase64))
                    return null;

                // Convert Base64 to BitmapImage
                var imageBytes = Convert.FromBase64String(imageBase64);
                var bitmap = new BitmapImage();
                using (var stream = new System.IO.MemoryStream(imageBytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                }

                _logger?.LogDebug($"Screenshot captured for dialog preview ({stage})");

                // Also save to database if configured — link evidence to audit via auditLogId
                CaptureScreenshot(setting, elements, username, stage, document, auditLogId);

                return bitmap;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to capture screenshot for dialog: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Handle Protect mode: Block unless OTP override is provided.
        /// Returns true if allowed (via OTP), false if blocked.
        /// </summary>
        private bool HandlePreventMode(
            CommandSettingViewModel setting,
            List<Element> elements,
            string username,
            bool isAdmin,
            Document document,
            string? bypassSource = null)
        {
            _logger?.LogInfo($"Protect mode: Processing {setting.CommandCode} for {username} (isAdmin: {isAdmin}, allowAdminOverride: {setting.AllowAdminOverride})");

            // Always generate auditLogId upfront — PK for audit entry, FK for all evidence captures
            var auditLogId = Guid.NewGuid().ToString();
            if (setting.CaptureBeforeScreenshot)
                CaptureScreenshot(setting, elements, username, "before", document, auditLogId);

            // ADMIN OVERRIDE: If user is admin and AllowAdminOverride is enabled, bypass OTP
            if (isAdmin && setting.AllowAdminOverride)
            {
                _logger?.LogInfo($"Protect mode: Admin override granted for {setting.CommandCode} by {username}");

                // Show GuideDialog for admin override (with comment support)
                var adminMessage = !string.IsNullOrWhiteSpace(setting.CustomMessage)
                    ? $"{setting.CustomMessage}\n\nAs an administrator, you can proceed without OTP."
                    : $"This action requires authorization.\n\nAs an administrator, you can proceed without OTP.";

                var adminElementIds = elements.Select(e => e.Id).ToList();
                var adminEvalResult = new RuleEvaluationResult
                {
                    FinalMode = ProtectionMode.Protect,
                    CombinedMessage = adminMessage,
                    RequireComment = setting.RequireComment
                };
                adminEvalResult.MatchedRules.Add(new Rule
                {
                    Name = setting.CommandName,
                    Description = $"Admin override for {setting.CommandCode}",
                    Mode = ProtectionMode.Protect,
                    Message = adminMessage
                });

                // Capture before screenshot for dialog preview (reuse auditLogId if already captured)
                BitmapImage? adminBeforeImage = null;
                if (setting.CaptureBeforeScreenshot)
                {
                    adminBeforeImage = CaptureScreenshotForDialog(setting, elements, username, "before", document, auditLogId);
                }

                var adminDialog = new GuideDialog(
                    adminEvalResult,
                    adminElementIds,
                    commandName: $"Admin Override - {setting.CommandName}",
                    commandDescription: "This action will be logged for audit purposes.");

                // Configure as Assist mode (admin override uses Confirm, not OTP)
                adminDialog.SetAssistMode(ProtectionType.Command, $"Admin Override - {setting.CommandName}");

                if (adminBeforeImage != null)
                {
                    adminDialog.SetBeforeImage(adminBeforeImage);
                }

                var adminActionType = DetermineActionType(setting.CommandCode);
                adminDialog.SetActionType(adminActionType);

                SetRevitOwner(adminDialog);
                var adminDialogResult = adminDialog.ShowDialog();
                var adminAllows = adminDialogResult == true && adminDialog.UserAllowed;

                if (adminAllows)
                {
                    // Admin confirmed - log and allow
                    _logger?.LogInfo($"Protect mode: Admin {username} confirmed override for {setting.CommandCode}");
                    var adminComment = adminDialog.UserComment;
                    if (!string.IsNullOrWhiteSpace(adminComment))
                    {
                        _logger?.LogInfo($"Admin comment: {adminComment}");
                    }
                    LogAuditEntry(setting, elements, username, isAdmin, "override", "Admin override confirmed",
                        overrideMethod: "AdminPrivilege", userComment: adminComment, auditLogId: auditLogId,
                        bypassSource: bypassSource);

                    // Schedule after screenshot if configured
                    if (setting.CaptureAfterScreenshot)
                        ScheduleAfterScreenshot(setting, elements, username, document, auditLogId);

                    return true;
                }
                else
                {
                    // Admin cancelled
                    _logger?.LogInfo($"Protect mode: Admin {username} cancelled override for {setting.CommandCode}");
                    LogAuditEntry(setting, elements, username, isAdmin, "blocked", "Admin cancelled override",
                        userComment: adminDialog.UserComment, auditLogId: auditLogId,
                        bypassSource: bypassSource);
                    return false;
                }
            }

            // NON-ADMIN PATH: Require OTP authorization via unified GuideDialog in Protect mode
            _logger?.LogInfo($"Protect mode: Requiring OTP for non-admin user {username}");

            // Build dialog message
            var message = !string.IsNullOrWhiteSpace(setting.CustomMessage)
                ? setting.CustomMessage
                : $"This action requires authorization.\n\nAffecting {elements.Count} element(s).";

            // Create rule evaluation result for the dialog
            var elementIds = elements.Select(e => e.Id).ToList();
            var evalResult = new RuleEvaluationResult
            {
                FinalMode = ProtectionMode.Protect,
                CombinedMessage = message,
                RequireComment = setting.RequireComment
            };
            evalResult.MatchedRules.Add(new Rule
            {
                Name = setting.CommandName,
                Description = $"Command protection for {setting.CommandCode}",
                Mode = ProtectionMode.Protect,
                Message = message
            });

            // Capture before screenshot for dialog preview
            BitmapImage? beforeImage = null;
            if (setting.CaptureBeforeScreenshot)
            {
                beforeImage = CaptureScreenshotForDialog(setting, elements, username, "before", document, auditLogId);
            }

            var dialog = new GuideDialog(
                evalResult,
                elementIds,
                commandName: setting.CommandName,
                commandDescription: $"Affecting {elements.Count} element(s)");

            // This path fires only when the user configured a Command Protection — so the
            // dialog label reads "Command Protection" regardless of which Revit command it
            // protects. Event Restriction labels come from the EventInterventionHandler path,
            // which fires when the user configured an Event Protection (e.g. Ze_CADImportProtection).
            // Each path shows the name that matches how the user set the protection up.
            const string contextLabel = "Command Restriction";

            // Configure as Protect mode (shows OTP button, hides Confirm)
            dialog.SetProtectMode(ProtectionType.Command, setting.CommandName);

            if (beforeImage != null)
            {
                dialog.SetBeforeImage(beforeImage);
            }

            SetRevitOwner(dialog);
            bool? dialogResult = dialog.ShowDialog();

            if (dialogResult != true || !dialog.OverrideAllowed)
            {
                _logger?.LogInfo($"Protect mode: User {username} cancelled OTP entry for {setting.CommandCode}");
                LogAuditEntry(setting, elements, username, isAdmin, "blocked", "User cancelled",
                    userComment: dialog.UserComment, auditLogId: auditLogId,
                    bypassSource: bypassSource);
                return false;
            }

            // Validate OTP
            string? otpCode = dialog.OtpCode;
            bool otpValid = ValidateOtp(otpCode, username, setting.CommandCode);

            if (!otpValid)
            {
                // Show error dialog — title matches the protection-type label so an Import-CAD
                // OTP error reads "Event Restriction" while a Move/Delete OTP error reads
                // "Command Protection".
                var errorDialog = new BIManageRevit.BIManage.Views.Bindings.ErrorDialog(
                    windowTitle: contextLabel,
                    headerText: "Invalid OTP",
                    message: "The OTP code you entered is invalid or has expired.",
                    details: "Please contact your BIM administrator for a new code.");
                SetRevitOwner(errorDialog);
                errorDialog.ShowDialog();

                _logger?.LogWarning($"Protect mode: Invalid OTP '{otpCode}' for {setting.CommandCode} by {username}");
                LogAuditEntry(setting, elements, username, isAdmin, "blocked", $"Invalid OTP: {otpCode}",
                    userComment: dialog.UserComment, auditLogId: auditLogId,
                    bypassSource: bypassSource);
                return false;
            }

            // OTP valid - allow action
            var otpUserComment = dialog.UserComment;
            if (!string.IsNullOrWhiteSpace(otpUserComment))
            {
                _logger?.LogInfo($"User comment: {otpUserComment}");
            }
            _logger?.LogInfo($"Protect mode: User {username} authorized via OTP for {setting.CommandCode}");
            LogAuditEntry(setting, elements, username, isAdmin, "allowed", $"OTP authorized: {otpCode}",
                overrideMethod: "OTP", userComment: otpUserComment, auditLogId: auditLogId,
                bypassSource: bypassSource);

            // Show success dialog — title matches the protection-type label.
            var successDialog = new BIManageRevit.BIManage.Views.Bindings.UnpinSuccessDialog(
                elements.Count,
                "OTP verified successfully.",
                windowTitle: contextLabel,
                headerText: "Authorization Successful");
            SetRevitOwner(successDialog);
            successDialog.ShowDialog();

            // Schedule after screenshot if configured
            if (setting.CaptureAfterScreenshot)
                ScheduleAfterScreenshot(setting, elements, username, document, auditLogId);

            return true;
        }

        /// <summary>
        /// Validate OTP code against the database.
        /// </summary>
        private bool ValidateOtp(string? otpCode, string username, string commandCode)
        {
            if (string.IsNullOrEmpty(otpCode))
                return false;

            if (_otpRepository == null)
            {
                _logger?.LogWarning("OTP repository not available - OTP validation disabled");
                return false;
            }

            try
            {
                var otp = Task.Run(() => _otpRepository.ValidateAndConsumeOtpAsync(
                    otpCode,
                    username,
                    ruleId: null,
                    commandId: commandCode)).GetAwaiter().GetResult();

                if (otp != null)
                {
                    _logger?.LogInfo($"OTP validated: Code={otpCode}, GeneratedBy={otp.GeneratedBy}, Command={commandCode}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"OTP validation error: {ex.Message}", ex);
            }

            return false;
        }

        /// <summary>
        /// Capture screenshot and record to database.
        /// The auditLogId links this evidence to its corresponding audit log entry.
        /// </summary>
        private void CaptureScreenshot(
            CommandSettingViewModel setting,
            List<Element> elements,
            string username,
            string stage,
            Document document,
            string? auditLogId)
        {
            var screenshotService = _screenshotServiceGetter?.Invoke();
            if (screenshotService == null)
            {
                _logger?.LogDebug("Screenshot service not available");
                return;
            }

            try
            {
                var evidenceId = Guid.NewGuid().ToString();
                var revitContext = _revitContextGetter?.Invoke();
                var sessionId = revitContext?.SessionId.ToString() ?? Guid.NewGuid().ToString();

                // Capture as Base64
                var imageBase64 = screenshotService.CaptureRevitWindowAsBase64();

                if (!string.IsNullOrEmpty(imageBase64))
                {
                    _logger?.LogInfo($"Screenshot captured for {setting.CommandCode} ({stage}): {imageBase64.Length} chars");

                    // Save to temp file and compute hash
                    var savedPath = screenshotService.SaveBase64ToTempFile(imageBase64, sessionId, $"{evidenceId}_{stage}");
                    string? fileHash = null;
                    if (!string.IsNullOrEmpty(savedPath))
                    {
                        try
                        {
                            using (var sha256 = System.Security.Cryptography.SHA256.Create())
                            {
                                var fileBytes = System.IO.File.ReadAllBytes(savedPath);
                                var hashBytes = sha256.ComputeHash(fileBytes);
                                fileHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                            }
                        }
                        catch { /* hash failure is non-fatal */ }
                    }

                    // Record to database if repository available
                    if (_evidenceRepository != null)
                    {
                        var evidence = new EvidenceRepository.EvidenceCapture
                        {
                            EvidenceId = evidenceId,
                            AuditLogId = auditLogId,
                            SessionId = sessionId,
                            ProtectionType = "command",
                            CaptureType = "screenshot",
                            CaptureStage = stage,
                            CapturedAt = DateTime.UtcNow,
                            CommandId = setting.CommandCode,
                            CommandName = setting.CommandName,
                            ElementIds = string.Join(",", elements.Select(e => e.Id.GetIdValue())),
                            ElementCount = elements.Count,
                            FilePath = savedPath,
                            FileSizeBytes = imageBase64.Length * 3 / 4, // Approximate decoded size
                            FileFormat = "png",
                            UploadStatus = "pending",
                            FileHashSha256 = fileHash,
                            Metadata = $"{{\"username\":\"{username}\",\"mode\":\"{setting.Mode}\"}}"
                        };

                        _ = _evidenceRepository.RecordEvidenceCaptureAsync(evidence);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to capture screenshot: {ex.Message}");
            }
        }

        /// <summary>
        /// Schedule after screenshot capture (to be captured after command executes).
        /// Uses IdlingEvent to capture after the command completes.
        /// </summary>
        private void ScheduleAfterScreenshot(
            CommandSettingViewModel setting,
            List<Element> elements,
            string username,
            Document document,
            string? auditLogId = null)
        {
            EventHandler<Autodesk.Revit.UI.Events.IdlingEventArgs>? idlingHandler = null;
            idlingHandler = (s, args) =>
            {
                try
                {
                    _uiApp.Idling -= idlingHandler;
                    CaptureScreenshot(setting, elements, username, "after", document, auditLogId: auditLogId);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to capture after screenshot: {ex.Message}");
                }
            };

            _uiApp.Idling += idlingHandler;
        }

        /// <summary>
        /// Log audit entry to repository.
        /// </summary>
        private void LogAuditEntry(
            CommandSettingViewModel setting,
            List<Element> elements,
            string username,
            bool isAdmin,
            string action,
            string? comment,
            string? userComment = null,
            string? overrideMethod = null,
            string? auditLogId = null,
            string? bypassSource = null)
        {
            try
            {
                // Bypass-detector path prefixes the Reason so the admin dashboard can
                // distinguish ribbon clicks from drag/nudge/Ctrl+drag gestures.
                if (!string.IsNullOrEmpty(bypassSource))
                {
                    comment = string.IsNullOrEmpty(comment)
                        ? bypassSource
                        : $"{bypassSource}: {comment}";
                }
                if (_auditRepository == null)
                {
                    _logger?.LogDebug("Audit repository not available, skipping audit log");
                    return;
                }

                var elementIds = string.Join(",", elements.Select(e => e.Id.GetIdValue()));

                // Get session ID from RevitContext
                var sessionId = _revitContextGetter?.Invoke()?.SessionId.ToString();

                // Get model GUID from active document
                string modelGuid = null;
                try
                {
                    var doc = _uiApp?.ActiveUIDocument?.Document;
                    if (doc != null) modelGuid = ModelGuidHelper.GetModelGuid(doc, _logger);
                }
                catch { /* Non-critical */ }

                var auditEntry = new ProtectionAuditEntry
                {
                    Timestamp = DateTime.UtcNow,
                    UserName = username,
                    WasCompanyAdmin = isAdmin,
                    WasProjectAdmin = isAdmin,
                    ModelGuid = modelGuid,
                    ProtectionId = setting.Id,
                    CommandName = setting.CommandName,
                    Mode = setting.Mode switch
                    {
                        InterventionMode.Notify => ProtectionMode.Notify,
                        InterventionMode.Assist => ProtectionMode.Assist,
                        InterventionMode.Protect => ProtectionMode.Protect,
                        _ => ProtectionMode.Notify
                    },
                    Action = action switch
                    {
                        "allowed" => ProtectionAction.Allowed,
                        "blocked" => ProtectionAction.Blocked,
                        "cancelled" => ProtectionAction.Cancelled,
                        "override" => ProtectionAction.Override,
                        _ => ProtectionAction.Allowed
                    },
                    ElementIds = elementIds,
                    ElementCount = elements.Count,
                    Reason = comment,
                    UserComment = userComment,
                    OverrideMethod = overrideMethod,
                    EventSource = "Command Restriction",
                    SessionId = sessionId,
                    // Per user request: every audit entry starts at sent_mail=1 (true).
                    SentMail = true,
                    AuditLogId = auditLogId ?? Guid.NewGuid().ToString()
                };

                _auditRepository.SaveAuditEntry(auditEntry);
                _logger?.LogDebug($"Audit entry logged: {setting.CommandCode} - {action} (EventSource: CommandProtection)");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to log audit entry: {ex.Message}");
            }
        }

        /// <summary>
        /// Get selected elements from document.
        /// </summary>
        private List<Element> GetSelectedElements(Document document)
        {
            try
            {
                var uiDocument = new UIDocument(document);
                var selection = uiDocument.Selection;
                var elementIds = selection.GetElementIds();

                if (elementIds.Count == 0)
                    return new List<Element>();

                return elementIds
                    .Select(id => document.GetElement(id))
                    .Where(e => e != null)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error getting selected elements: {ex.Message}", ex);
                return new List<Element>();
            }
        }

        /// <summary>
        /// Get current Revit username.
        /// </summary>
        private string GetRevitUsername()
        {
            try
            {
                return _uiApp?.Application?.Username ?? Environment.UserName;
            }
            catch
            {
                return Environment.UserName;
            }
        }

        /// <summary>
        /// Get display name for an element.
        /// </summary>
        private string GetElementDisplayName(Element element)
        {
            try
            {
                var name = element.Name;
                var category = element.Category?.Name ?? "Unknown";
                return $"{category}: {name}";
            }
            catch
            {
                return element.Id.ToString();
            }
        }

        /// <summary>
        /// Get count of active bindings.
        /// </summary>
        public int ActiveBindingCount => _activeBindings.Count;

        /// <summary>
        /// Get list of active command codes.
        /// </summary>
        public IEnumerable<string> ActiveCommandCodes => _activeBindings.Keys;

        // [Fix-1a diagnostic] Logs dynamic binding lifecycle with ALC identity, assembly
        // hash, and Application.Instance assembly hash. Compared against
        // [AsmIdentity:OnStartup] in Application.cs and [SubDiag] from
        // CommandInterceptionService to identify any subscription registered from a
        // non-live ALC (the suspected cause of AddInManager's "Cannot find the host
        // add-in" warning). Pure logging — no behavior change.
        private void LogSubscriptionEvent(string op, RevitCommandId commandId)
        {
            try
            {
                string alcName = "n/a";
                int thisAsmHash = 0;
                int liveAsmHash = 0;
#if NET8_0_OR_GREATER
                var thisAsm = typeof(CommandProtectionBinding).Assembly;
                var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(thisAsm);
                alcName = alc?.Name ?? "Default";
                thisAsmHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(thisAsm);
                var liveApp = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                if (liveApp != null)
                    liveAsmHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(liveApp.GetType().Assembly);
#endif
                string addInId = "unknown";
                try { addInId = _uiApp?.ActiveAddInId?.GetGUID().ToString() ?? "unknown"; }
                catch { /* ActiveAddInId may throw outside of command context */ }

                bool fromLiveAlc = (thisAsmHash != 0 && thisAsmHash == liveAsmHash);
                // Per-command diagnostic from the cross-ALC debugging campaign — kept as DEBUG.
                _logger?.LogDebug(
                    $"[SubDiag-CPB] {op} cmd={commandId.Name}(id={commandId.Id}) ALC={alcName} thisAsm={thisAsmHash} liveAsm={liveAsmHash} fromLiveAlc={fromLiveAlc} AddInId={addInId}");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[SubDiag-CPB] log failed for op={op}: {ex.Message}");
            }
        }

        /// <summary>
        /// Internal class to store binding information.
        /// </summary>
        private class CommandBindingInfo
        {
            public string CommandCode { get; set; } = "";
            public RevitCommandId CommandId { get; set; } = null!;
            public AddInCommandBinding Binding { get; set; } = null!;
        }
    }
}
