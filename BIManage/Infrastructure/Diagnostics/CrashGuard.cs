#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;

namespace BIManage.Infrastructure.Diagnostics
{
    /// <summary>
    /// Last-resort safety net for unhandled exceptions originating from the BIManage
    /// add-in. Goal: an unhandled .NET exception in plugin code MUST NOT crash Revit.
    /// Plugin functionality may be temporarily compromised, but Revit continues.
    ///
    /// Three failure surfaces in a Revit add-in that, left unhandled, take down the
    /// entire host process (and the user's unsaved work):
    ///   1. <see cref="AppDomain.UnhandledException"/> — exceptions on background
    ///      threads (e.g. from <c>Task.Run(...)</c>, timers, SignalR receive loop,
    ///      sync workers). On .NET 8+ these terminate the process by default.
    ///   2. <see cref="TaskScheduler.UnobservedTaskException"/> — exceptions inside
    ///      <c>Task</c>s whose result was never awaited / GetResult'd. .NET 8+ may
    ///      terminate the process; calling <see cref="UnobservedTaskExceptionEventArgs.SetObserved"/>
    ///      tells the runtime "we've seen this, don't crash" — that's our hook.
    ///   3. WPF's <c>Application.Current.DispatcherUnhandledException</c> — exceptions
    ///      from event handlers, command bindings, click handlers, etc. running on
    ///      the WPF UI thread. Marking <c>Handled=true</c> prevents Revit's
    ///      "serious error" dialog and the subsequent process termination.
    ///
    /// Each handler does the same thing: log full diagnostic info, throttle the
    /// user-visible recovery dialog (one per minute max so a tight-loop exception
    /// doesn't queue 1000 dialogs), and offer the user a "Submit Help Ticket"
    /// option that pre-fills <see cref="ReportIssueDialog"/> with the exception
    /// stack so the issue can be sent without retyping anything. The user is
    /// always given the choice to keep working — no forced exits, no hangs.
    ///
    /// What this DOESN'T catch:
    ///   - Native (C++) Revit crashes triggered by managed code calling Revit APIs
    ///     in unsupported states (e.g. <c>getModelGUID</c> during STC). Those
    ///     surface as Revit's own "serious error" dialog and we cannot intercept
    ///     them from managed code — they have to be PREVENTED at the call site.
    ///     The cross-ALC fix and STC guards address those separately.
    ///   - Stack overflow / OutOfMemory — the runtime tears down the process
    ///     before any handler can run. Mitigated by careful allocation patterns
    ///     in our codebase rather than by this guard.
    ///
    /// Idempotent: safe to call <see cref="Initialize"/> multiple times.
    /// </summary>
    public static class CrashGuard
    {
        private static readonly object _initLock = new object();
        private static bool _initialized;
        private static ILogger? _logger;
        private static IntPtr _ownerHandle = IntPtr.Zero;

        // Throttle the user-visible recovery dialog. Logging is NOT throttled —
        // every exception is logged so the engineer can see the full pattern.
        private static DateTime _lastDialogShownAt = DateTime.MinValue;
        private static readonly TimeSpan DialogCooldown = TimeSpan.FromSeconds(60);

        // Counter for diagnostic visibility ("[CrashGuard] caught #N exception")
        private static long _crashCount;

        // Snapshot of plugin/user/session context for the help ticket pre-fill.
        // Captured at Initialize so even a crash deep inside a service doesn't
        // need to re-resolve these from DI (which itself might be broken).
        private static string? _revitVersion;
        private static string? _revitBuild;
        private static string? _revitUsername;
        private static Func<string?>? _sessionIdProvider;
        private static Func<string?>? _modelNameProvider;
        private static Func<string?>? _modelPathProvider;
        private static Func<global::BIManage.Infrastructure.Auth.AuthenticatedHttpClient?>? _httpClientProvider;

        /// <summary>
        /// Wires up the three unhandled-exception handlers and stores context
        /// providers for the help-ticket pre-fill. Call once from
        /// <c>Application.OnStartup</c> as the FIRST action — before service
        /// registration, before the bootstrap pipeline — so that any subsequent
        /// startup failure is caught instead of crashing Revit.
        /// </summary>
        public static void Initialize(
            ILogger? logger,
            IntPtr revitMainWindowHandle,
            string? revitVersion = null,
            string? revitBuild = null,
            string? revitUsername = null,
            Func<string?>? sessionIdProvider = null,
            Func<string?>? modelNameProvider = null,
            Func<string?>? modelPathProvider = null,
            Func<global::BIManage.Infrastructure.Auth.AuthenticatedHttpClient?>? httpClientProvider = null)
        {
            lock (_initLock)
            {
                _logger = logger ?? _logger;
                if (revitMainWindowHandle != IntPtr.Zero) _ownerHandle = revitMainWindowHandle;
                _revitVersion = revitVersion ?? _revitVersion;
                _revitBuild = revitBuild ?? _revitBuild;
                _revitUsername = revitUsername ?? _revitUsername;
                _sessionIdProvider = sessionIdProvider ?? _sessionIdProvider;
                _modelNameProvider = modelNameProvider ?? _modelNameProvider;
                _modelPathProvider = modelPathProvider ?? _modelPathProvider;
                _httpClientProvider = httpClientProvider ?? _httpClientProvider;

                if (_initialized) return;

                AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

                // WPF dispatcher hook — only on the UI thread, only when WPF is alive.
                // Wrapped in try/catch because WPF may not yet be initialised at the
                // moment this runs (Revit creates its main WPF dispatcher lazily).
                try
                {
                    var app = System.Windows.Application.Current;
                    if (app != null)
                    {
                        app.DispatcherUnhandledException += OnDispatcherUnhandledException;
                    }
                    else
                    {
                        // Defer the hook until WPF is up. Schedule on the dispatcher of
                        // the calling thread (Revit's UI thread) so we don't miss late
                        // bring-up.
                        var disp = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                        disp?.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                var lateApp = System.Windows.Application.Current;
                                if (lateApp != null)
                                    lateApp.DispatcherUnhandledException += OnDispatcherUnhandledException;
                            }
                            catch { }
                        }));
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"[CrashGuard] Could not hook DispatcherUnhandledException: {ex.Message}");
                }

                _initialized = true;
                _logger?.LogInfo("[CrashGuard] Initialized — AppDomain + TaskScheduler + WPF Dispatcher handlers active.");
            }
        }

        /// <summary>
        /// Wraps an action so any exception is logged and (optionally) surfaced via
        /// the recovery dialog without propagating. Use this for code paths where a
        /// failure should NOT cancel the surrounding flow — e.g. background metrics
        /// snapshots, optional API calls, telemetry. Returns true if the action ran
        /// to completion without throwing.
        /// </summary>
        public static bool RunGuarded(Action action, string contextDescription, bool showDialogOnError = false)
        {
            if (action == null) return false;
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                Capture("RunGuarded:" + contextDescription, ex, terminating: false, showDialog: showDialogOnError);
                return false;
            }
        }

        /// <summary>
        /// Async variant of <see cref="RunGuarded(Action, string, bool)"/>.
        /// </summary>
        public static async Task<bool> RunGuardedAsync(Func<Task> asyncAction, string contextDescription, bool showDialogOnError = false)
        {
            if (asyncAction == null) return false;
            try
            {
                await asyncAction();
                return true;
            }
            catch (Exception ex)
            {
                Capture("RunGuardedAsync:" + contextDescription, ex, terminating: false, showDialog: showDialogOnError);
                return false;
            }
        }

        // ----------------------------------------------------------------------
        // Handlers
        // ----------------------------------------------------------------------

        private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // e.IsTerminating: true on .NET 8+ for almost any unhandled exception on a
            // non-finalizer thread — the runtime is about to tear down the process.
            // We have a brief window to log + show the dialog before that happens.
            // We can't actually PREVENT termination here (.NET removed the legacy
            // <legacyUnhandledExceptionPolicy> in modern runtimes), so this branch is
            // primarily a diagnostic capture. The other two handlers are the ones
            // that actually save Revit.
            var ex = e.ExceptionObject as Exception;
            Capture("AppDomain.UnhandledException", ex, terminating: e.IsTerminating, showDialog: true);
        }

        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            // Mark observed FIRST so the runtime doesn't terminate the process,
            // even if the rest of our handler throws. This is the critical line —
            // without it, .NET 8+ default policy crashes Revit on the next GC.
            try { e.SetObserved(); }
            catch { }

            Capture("TaskScheduler.UnobservedTaskException", e.Exception, terminating: false, showDialog: true);
        }

        private static void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            // Mark Handled FIRST so WPF doesn't bubble the exception to AppDomain
            // (which on WPF would lead to Revit's own crash dialog + termination).
            try { e.Handled = true; }
            catch { }

            Capture("Dispatcher.UnhandledException", e.Exception, terminating: false, showDialog: true);
        }

        // ----------------------------------------------------------------------
        // Capture + dialog (throttled)
        // ----------------------------------------------------------------------

        private static void Capture(string source, Exception? ex, bool terminating, bool showDialog)
        {
            // Defensive: never let the guard itself throw.
            try
            {
                var n = Interlocked.Increment(ref _crashCount);

                // Unwrap AggregateExceptions (always the case for UnobservedTaskException) so we
                // surface the REAL inner fault — its type, message and, critically, its stack
                // trace. An AggregateException's own StackTrace is null, which is why the actual
                // culprit was invisible before.
                var (root, all) = NormalizeFault(ex);

                var summary = root != null
                    ? $"{root.GetType().FullName}: {root.Message}"
                    : "<no exception object>";
                if (all.Count > 1)
                    summary += $" (+{all.Count - 1} sibling fault(s))";

                // Always log the FULL flattened detail ourselves. Pass null as the exception arg
                // so FileLogger doesn't re-append its own partial (single-chain) walk on top.
                _logger?.LogError(
                    $"[CrashGuard] #{n} {source} (terminating={terminating}) — {summary}\n" +
                    BuildFlattenedLog(all),
                    null);

                if (!showDialog) return;

                // Origin attribution: the TaskScheduler / AppDomain hooks are PROCESS-WIDE and
                // catch faults from every add-in loaded in Revit. Without this gate we'd blame
                // ZeManage for other vendors' bugs. We still caught + SetObserved + logged the
                // fault above (protecting Revit is the point) — we just don't pop a misleading
                // "ZeManage encountered an error" dialog or offer a ZeManage ticket for a fault
                // that didn't originate in our code. Terminating faults are the exception: the
                // process is dying, so the user always gets the capture/ticket path.
                bool isZeManage = LooksLikeZeManageFault(all);
                if (!isZeManage && !terminating)
                {
                    _logger?.LogWarning(
                        $"[CrashGuard] Suppressed recovery dialog for non-ZeManage fault originating in: {DescribeOriginFrame(root, all)}");
                    return;
                }

                bool shouldShow;
                lock (_initLock)
                {
                    shouldShow = (DateTime.UtcNow - _lastDialogShownAt) >= DialogCooldown;
                    if (shouldShow) _lastDialogShownAt = DateTime.UtcNow;
                }
                if (!shouldShow)
                {
                    _logger?.LogInfo($"[CrashGuard] Recovery dialog throttled (cooldown {DialogCooldown.TotalSeconds:F0}s).");
                    return;
                }

                ShowRecoveryDialogSafe(root, source, all, isZeManage, terminating);
            }
            catch (Exception guardEx)
            {
                // Last-resort: write to debug output. Don't ever rethrow from here.
                try { System.Diagnostics.Debug.WriteLine($"[CrashGuard] Capture itself failed: {guardEx}"); }
                catch { }
            }
        }

        // ----------------------------------------------------------------------
        // Fault normalization + origin attribution
        // ----------------------------------------------------------------------

        // ZeManage code appears in stack frames / type names as either "BIManage.*" or
        // "BIManageRevit.BIManage.*"; the substring "BIManage" matches both.
        private const string ZeManageMarker = "BIManage";

        /// <summary>
        /// Resolves a raw exception into (a) the most useful "root cause" for display and
        /// (b) the full flattened list of leaf faults for logging/attribution. For an
        /// <see cref="AggregateException"/> (what <c>UnobservedTaskException</c> always wraps)
        /// this calls <see cref="AggregateException.Flatten"/> so nested aggregates collapse and
        /// every sibling fault is preserved. Never throws.
        /// </summary>
        private static (Exception? root, IReadOnlyList<Exception> all) NormalizeFault(Exception? ex)
        {
            try
            {
                if (ex == null)
                    return (null, Array.Empty<Exception>());

                var all = new List<Exception>();

                IEnumerable<Exception> seed;
                if (ex is AggregateException agg)
                {
                    var flat = agg.Flatten().InnerExceptions;
                    seed = flat.Count > 0 ? (IEnumerable<Exception>)flat : new[] { ex };
                }
                else
                {
                    seed = new[] { ex };
                }

                // Expand each seed's single InnerException chain (bounded) so wrapped non-aggregate
                // inners are visible too. Exception has no Equals override, so List.Contains uses
                // reference equality — fine for dedup on the (short) crash path.
                foreach (var e in seed)
                {
                    var cur = e;
                    int depth = 0;
                    while (cur != null && depth < 10 && !all.Contains(cur))
                    {
                        all.Add(cur);
                        cur = cur.InnerException;
                        depth++;
                    }
                }

                if (all.Count == 0) all.Add(ex);

                // Root = first non-aggregate leaf WITH a stack trace; fall back to first
                // non-aggregate; finally the original exception.
                Exception? root = all.FirstOrDefault(e => !(e is AggregateException) && !string.IsNullOrEmpty(e.StackTrace))
                                   ?? all.FirstOrDefault(e => !(e is AggregateException))
                                   ?? ex;

                return (root, all);
            }
            catch
            {
                return (ex, ex != null ? new[] { ex } : Array.Empty<Exception>());
            }
        }

        /// <summary>
        /// True if any of the flattened faults appears to originate in ZeManage code. Scans
        /// stack traces, <see cref="Exception.TargetSite"/>, <see cref="Exception.Source"/> and
        /// the exception type name for <see cref="ZeManageMarker"/>. TargetSite/Source survive
        /// even when StackTrace is null. Uncertain (no marker found anywhere) ⇒ false (treated as
        /// third-party): the fault is still fully logged, just kept out of the user-facing dialog.
        /// Never throws.
        /// </summary>
        private static bool LooksLikeZeManageFault(IReadOnlyList<Exception> flattened)
        {
            try
            {
                if (flattened == null) return false;
                foreach (var e in flattened)
                {
                    if (e == null) continue;
                    if (HasMarker(e.StackTrace)) return true;
                    try
                    {
                        var ts = e.TargetSite;
                        if (ts != null)
                        {
                            if (HasMarker(ts.DeclaringType?.FullName)) return true;
                            if (HasMarker(ts.DeclaringType?.Assembly?.FullName)) return true;
                        }
                    }
                    catch { /* TargetSite can throw in partial-trust / remoting edge cases */ }
                    if (HasMarker(e.Source)) return true;
                    if (HasMarker(e.GetType().FullName)) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasMarker(string? s) =>
            !string.IsNullOrEmpty(s) && s!.IndexOf(ZeManageMarker, StringComparison.Ordinal) >= 0;

        /// <summary>
        /// Best-effort short description of where a fault originated, for the suppression log
        /// line and the dialog body. Prefers the first non-framework stack frame; falls back to
        /// TargetSite, then Source, then "&lt;unknown origin&gt;". Never throws.
        /// </summary>
        private static string DescribeOriginFrame(Exception? root, IReadOnlyList<Exception> flattened)
        {
            try
            {
                IEnumerable<Exception> order = root != null
                    ? new[] { root }.Concat(flattened ?? Array.Empty<Exception>())
                    : (flattened ?? (IEnumerable<Exception>)Array.Empty<Exception>());

                foreach (var e in order)
                {
                    var st = e?.StackTrace;
                    if (string.IsNullOrEmpty(st)) continue;
                    foreach (var rawLine in st!.Split('\n'))
                    {
                        var line = rawLine.Trim();
                        if (line.Length == 0) continue;
                        if (line.StartsWith("at ", StringComparison.Ordinal))
                            line = line.Substring(3);
                        // Strip trailing " in <file>:line N"
                        var inIdx = line.IndexOf(" in ", StringComparison.Ordinal);
                        if (inIdx > 0) line = line.Substring(0, inIdx);
                        // Skip pure-framework frames — we want the first meaningful caller.
                        if (line.StartsWith("System.", StringComparison.Ordinal)
                            || line.StartsWith("Microsoft.", StringComparison.Ordinal)
                            || line.StartsWith("MS.", StringComparison.Ordinal))
                            continue;
                        return line;
                    }
                }

                var fb = root ?? (flattened != null && flattened.Count > 0 ? flattened[0] : null);
                if (fb != null)
                {
                    try
                    {
                        var ts = fb.TargetSite;
                        if (ts?.DeclaringType != null)
                            return ts.DeclaringType.FullName + "." + ts.Name;
                    }
                    catch { }
                    if (!string.IsNullOrEmpty(fb.Source)) return fb.Source!;
                }
                return "<unknown origin>";
            }
            catch
            {
                return "<unknown origin>";
            }
        }

        /// <summary>
        /// Full-fidelity dump of every flattened fault: index, type, message and stack trace.
        /// This is what guarantees the real culprit is recorded — unlike the bare
        /// AggregateException whose own StackTrace is null. Never throws.
        /// </summary>
        private static string BuildFlattenedLog(IReadOnlyList<Exception> flattened)
        {
            try
            {
                if (flattened == null || flattened.Count == 0)
                    return "[CrashGuard] (no exception detail)";

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < flattened.Count; i++)
                {
                    var e = flattened[i];
                    sb.Append($"[CrashGuard] [{i}] {e?.GetType().FullName}: {e?.Message}\n");
                    sb.Append($"[CrashGuard]     StackTrace: {(string.IsNullOrEmpty(e?.StackTrace) ? "<no stack trace>" : e!.StackTrace)}\n");
                }
                return sb.ToString().TrimEnd('\n');
            }
            catch
            {
                return "[CrashGuard] (failed to format exception detail)";
            }
        }

        private static void ShowRecoveryDialogSafe(Exception? root, string source, IReadOnlyList<Exception> all, bool isZeManage, bool terminating)
        {
            try
            {
                // Marshal to UI thread. WPF dialogs MUST run on a STA dispatcher.
                var dispatcher = System.Windows.Application.Current?.Dispatcher
                                  ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
                if (dispatcher == null)
                {
                    _logger?.LogWarning("[CrashGuard] No WPF dispatcher available — recovery dialog skipped.");
                    return;
                }

                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var origin = DescribeOriginFrame(root, all);
                        var errorLine = $"Error: {root?.GetType().Name ?? "<unknown>"} — {root?.Message ?? "<no message>"}";

                        string title, headline, body;
                        if (isZeManage)
                        {
                            title = "ZeManage Error";
                            headline = "ZeManage encountered an error.";
                            body =
                                "Revit will keep running, but some ZeManage features may be limited until the issue is resolved.\n\n" +
                                $"Source: {source}\n" +
                                $"{errorLine}\n" +
                                $"Originating in: {origin}\n\n" +
                                "You can submit a help ticket so the team can investigate. Your last action will not be lost.";
                        }
                        else
                        {
                            // We only reach here for a TERMINATING fault that did NOT originate in
                            // ZeManage — don't claim it's a ZeManage error, but still offer the path.
                            title = "Revit Error";
                            headline = "Revit encountered an error.";
                            body =
                                "A serious error occurred in a Revit add-in (not ZeManage), and Revit may be unstable.\n\n" +
                                $"Source: {source}\n" +
                                $"{errorLine}\n" +
                                $"Originating in: {origin}\n\n" +
                                "You can submit a help ticket so the team can investigate.";
                        }

                        var ownerHandle = _ownerHandle != IntPtr.Zero
                            ? _ownerHandle
                            : System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;

                        var choice = global::BIManage.Views.Common.ZeConfirmDialog.ConfirmShow(
                            title: title,
                            headline: headline,
                            message: body,
                            primaryText: "Submit Help Ticket",
                            secondaryText: "Continue without reporting",
                            ownerHandle: ownerHandle);

                        if (choice == global::BIManage.Views.Common.ZeConfirmResult.Primary)
                        {
                            OpenReportIssueDialog(root, source, ownerHandle, all);
                        }
                        else
                        {
                            _logger?.LogInfo("[CrashGuard] User dismissed recovery dialog without submitting ticket.");
                        }
                    }
                    catch (Exception dialogEx)
                    {
                        _logger?.LogWarning($"[CrashGuard] Recovery dialog failed to display: {dialogEx.Message}");
                    }
                }));
            }
            catch (Exception ex2)
            {
                _logger?.LogWarning($"[CrashGuard] Failed to dispatch recovery dialog: {ex2.Message}");
            }
        }

        private static void OpenReportIssueDialog(Exception? root, string source, IntPtr ownerHandle, IReadOnlyList<Exception> all)
        {
            try
            {
                var prefill = BuildIssueDescription(root, source, all);
                var httpClient = _httpClientProvider?.Invoke();
                var sessionId = _sessionIdProvider?.Invoke();
                var modelName = _modelNameProvider?.Invoke();
                var modelPath = _modelPathProvider?.Invoke();

                // ReportIssueDialog has no prefill-description parameter today; pre-filling the
                // user's description text post-construction via reflection is too brittle. Instead
                // we use the existing preSelectModule slot to hint "Crash" and copy the diagnostic
                // text to the clipboard so the user can paste with a single Ctrl+V into the
                // description box. Logging the text guarantees the engineer also has it.
                try
                {
                    System.Windows.Clipboard.SetText(prefill);
                    _logger?.LogInfo("[CrashGuard] Diagnostic text copied to clipboard for help ticket.");
                }
                catch (Exception clipEx)
                {
                    _logger?.LogDebug($"[CrashGuard] Clipboard copy failed: {clipEx.Message}");
                }

                var dialog = new global::BIManageRevit.BIManage.Views.Support.ReportIssueDialog(
                    httpClient: httpClient,
                    revitVersion: _revitVersion,
                    revitBuild: _revitBuild,
                    revitUsername: _revitUsername,
                    sessionId: sessionId,
                    modelName: modelName,
                    modelPath: modelPath,
                    preSelectModule: "Crash");

                if (ownerHandle != IntPtr.Zero)
                {
                    new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = ownerHandle };
                }

                dialog.ShowDialog();
            }
            catch (Exception openEx)
            {
                _logger?.LogWarning($"[CrashGuard] Could not open Report Issue dialog: {openEx.Message}");
                // Even the fallback dialog failure must not crash — just log.
            }
        }

        private static string BuildIssueDescription(Exception? root, string source, IReadOnlyList<Exception> all)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[Auto-filled by BIManage CrashGuard]");
            sb.AppendLine();
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"When: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
            sb.AppendLine($"Crash count this session: {Interlocked.Read(ref _crashCount)}");
            sb.AppendLine($"Originating in: {DescribeOriginFrame(root, all)}");

            // Enumerate every flattened fault (AggregateExceptions can carry several siblings).
            if (all != null && all.Count > 0)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    var e = all[i];
                    sb.AppendLine();
                    sb.AppendLine($"---- Fault [{i}] ----");
                    sb.AppendLine($"Exception: {e?.GetType().FullName}");
                    sb.AppendLine($"Message: {e?.Message}");
                    sb.AppendLine("Stack trace:");
                    sb.AppendLine(string.IsNullOrEmpty(e?.StackTrace) ? "<no stack trace>" : e!.StackTrace);
                }
            }
            else if (root != null)
            {
                sb.AppendLine();
                sb.AppendLine($"Exception: {root.GetType().FullName}");
                sb.AppendLine($"Message: {root.Message}");
                sb.AppendLine("Stack trace:");
                sb.AppendLine(root.StackTrace ?? "<no stack trace>");
            }

            sb.AppendLine();
            sb.AppendLine("[End auto-filled section. Please describe what you were doing when this occurred:]");
            sb.AppendLine();
            return sb.ToString();
        }
    }
}
