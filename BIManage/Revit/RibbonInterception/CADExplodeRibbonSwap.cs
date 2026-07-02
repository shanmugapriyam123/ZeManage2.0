using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Windows;
using BIManage.Data.SQLite;
using BIManage.Revit.Helpers;
using BIManage.Revit.Protection;

namespace BIManage.Revit.RibbonInterception
{
    /// <summary>
    /// Replaces Revit's native CAD Explode ribbon split-button (Modify | Imports tab) with a
    /// shadow button so we can intercept the click and route it through the standard ZeManage
    /// event protection flow (Notify / Assist / Protect modes, audit, screenshot, OTP override).
    ///
    /// Why this exists: Revit's contextual ribbon Explode buttons cannot be intercepted via
    /// AddInCommandBinding. Without the swap, ribbon Full/Partial Explode bypasses the protection
    /// entirely (only the right-click context-menu IDs ID_IMPORT_INSTANCE_EXPLODE and
    /// ID_IMPORT_INST_PARTIAL_EXPLODE are catchable through normal command interception).
    ///
    /// Pattern: hide the native split-button, surface a visually identical shadow with our own IDs,
    /// hook ComponentManager.UIElementActivated to catch the click, show the protection dialog, and
    /// on allow flip visibility back and PostCommand the real Revit command.
    ///
    /// SAFETY: Every interaction with Autodesk.Windows is wrapped in try/catch. If the AdWindows
    /// API contract changes between Revit versions or the control name moves, the swap silently
    /// degrades to no-op and the existing post-action handler (CheckCADExplodePostAction) still
    /// audits the operation. Never throws back to Application init.
    /// </summary>
    public sealed class CADExplodeRibbonSwap : IDisposable
    {
        // Shadow button ID — must NOT collide with any Revit command id. Click handling is
        // entirely observational via UIElementActivated; no IExternalCommand dispatch.
        private const string ShadowButtonId = "ze_ExplodeButton";

        // AdWindows control IDs for the native Explode split-button on the contextual
        // Modify | Import Instance tab. The composite "Dialog_Essentials_…" id is recursive-find
        // material. Values verified against Revit 2022-2026; if Autodesk renames them in a future
        // release the FindItem lookups will return null and the swap simply doesn't install — the
        // post-action handler keeps audit working.
        private const string NativeSplitButtonControlId =
            "Dialog_Essentials_ImportInstanceExplode:Control_Essentials_ImportPartialExplode_RibbonListButton";

        // Transaction names dispatched by the underlying Revit commands. Used by the post-action
        // handler to recognise our PostCommand redispatch and skip duplicate audit.
        public const string TxFullExplode = "Full Explode";
        public const string TxPartialExplode = "Partial Explode";

        private readonly ILogger? _logger;
        private readonly Func<IEventProtectionService?>? _eventProtectionGetter;
        private readonly Func<EventInterventionHandler>? _interventionHandlerFactory;
        private readonly Func<AuditRepository?>? _auditRepoGetter;
        private readonly Func<RegisteredModelsRepository?>? _registeredModelsRepoGetter;

        private UIApplication? _uiApp;
        private bool _initialized;
        private bool _uiElementActivatedSubscribed;
        private bool _tabsCollectionSubscribed;
        private bool _disposed;
        // Track tabs we've hooked to avoid stacking duplicate handlers on stable static tabs.
        private readonly System.Collections.Generic.HashSet<Autodesk.Windows.RibbonTab> _hookedTabs = new();

        // ExternalEvent that pumps the rebind onto Revit's main UI thread. AdWindows ribbon
        // mutations are not thread-safe — RefreshSwapState() can be called from a SignalR
        // listener Task or other background context, so every mutation must funnel through
        // this event. Pattern reference: RibbonVisibilityManager.cs:25-50.
        private ExternalEvent? _rebindEvent;
        private RebindHandler? _rebindHandler;

        // ExternalEvent that posts the native Revit explode command from a clean Idling
        // context. PostCommand only works when the native button is visible AND the call
        // is made outside of UIElementActivated (which is mid-dispatch). The handler also
        // restores the selection that was lost while the protection dialog was open.
        private ExternalEvent? _postExplodeEvent;
        private PostExplodeHandler? _postExplodeHandler;
        private string? _pendingExplodeCommandId;
        private ICollection<ElementId>? _savedSelection;


        // Suppression flag moved to CADExplodeAuditGate so both this swap AND
        // CommandInterceptionService Priority-3 can mark the next post-action as already
        // handled. The static helper below keeps the existing call site in
        // EventRegistryService.CheckCADExplodePostAction working without a wider rename.
        public static bool ConsumeSuppressedPostAction() => CADExplodeAuditGate.ConsumeSuppressed();

        public CADExplodeRibbonSwap(
            ILogger? logger,
            Func<IEventProtectionService?>? eventProtectionGetter,
            Func<EventInterventionHandler>? interventionHandlerFactory,
            Func<AuditRepository?>? auditRepoGetter,
            Func<RegisteredModelsRepository?>? registeredModelsRepoGetter = null)
        {
            _logger = logger;
            _eventProtectionGetter = eventProtectionGetter;
            _interventionHandlerFactory = interventionHandlerFactory;
            _auditRepoGetter = auditRepoGetter;
            _registeredModelsRepoGetter = registeredModelsRepoGetter;
        }

        /// <summary>
        /// Wire up the swap. Idempotent — safe to call repeatedly. Returns false (and logs a
        /// debug line) if AdWindows isn't reachable; the rest of the protection chain stays
        /// intact in that case.
        /// </summary>
        public bool Initialize(UIApplication uiApp)
        {
            if (_initialized) return true;
            if (uiApp == null)
            {
                _logger?.LogDebug("[ExplodeSwap] UIApplication is null — skipping init.");
                return false;
            }

            try
            {
                _uiApp = uiApp;

                // Build the UI-thread marshaller BEFORE subscribing to events. ExternalEvent.Create
                // must run on the UI thread; Initialize is called from OnApplicationInitialized
                // which already runs there.
                _rebindHandler = new RebindHandler(this);
                _rebindEvent = ExternalEvent.Create(_rebindHandler);

                _postExplodeHandler = new PostExplodeHandler(this);
                _postExplodeEvent = ExternalEvent.Create(_postExplodeHandler);

                // Subscribe to ribbon panel collection changes so the swap re-applies when the
                // contextual Modify | Import Instance tab is created/destroyed (which happens
                // every time the user selects/deselects an ImportInstance).
                if (!TrySubscribeToRibbonChanges())
                    return false;

                // First-pass attempt at swap (panel may already be live if a CAD instance is
                // selected at startup). It's fine if there's nothing to find yet. We're on the
                // UI thread here, so direct call is safe — no need to round-trip through the event.
                TryRebindAllOpenPanels();

                _initialized = true;
                _logger?.LogInfo("[ExplodeSwap] Initialized — listening for Modify | Imports panel.");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[ExplodeSwap] Initialize failed (degrading to post-action audit only): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Re-evaluate the swap state in response to a protection toggle change (SignalR fires
        /// this when an admin enables/disables Ze_CADExplodeProtection). When disabled, we
        /// restore the native button visibility so users see the unmodified Revit UI.
        ///
        /// Thread-safe: marshals the actual ribbon mutation onto the Revit main thread via
        /// ExternalEvent. Safe to call from SignalR background tasks, UI commands, or anywhere.
        /// </summary>
        public void RefreshSwapState()
        {
            if (!_initialized) return;
            try
            {
                if (_rebindEvent == null)
                {
                    _logger?.LogDebug("[ExplodeSwap] RefreshSwapState skipped: rebind event not yet created.");
                    return;
                }
                if (_rebindEvent.IsPending)
                {
                    // Already queued — Revit will service it on the next idling tick. No need
                    // to raise again; the handler will pick up the latest protection state when
                    // it runs (it reads IsExplodeProtectionEnabled() at execution time, not now).
                    _logger?.LogDebug("[ExplodeSwap] RefreshSwapState noop: rebind already pending.");
                    return;
                }
                _rebindEvent.Raise();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[ExplodeSwap] RefreshSwapState failed to schedule rebind: {ex.Message}");
            }
        }

        /// <summary>
        /// IExternalEventHandler that runs <see cref="TryRebindAllOpenPanels"/> on Revit's main
        /// UI thread. Created and registered in <see cref="Initialize"/>; raised by
        /// <see cref="RefreshSwapState"/>.
        /// </summary>
        private sealed class RebindHandler : IExternalEventHandler
        {
            private readonly CADExplodeRibbonSwap _owner;
            public RebindHandler(CADExplodeRibbonSwap owner) { _owner = owner; }
            public void Execute(UIApplication app)
            {
                try { _owner.TryRebindAllOpenPanels(); }
                catch (Exception ex) { _owner._logger?.LogWarning($"[ExplodeSwap] RebindHandler.Execute failed: {ex.Message}"); }
            }
            public string GetName() => "BIManage.CADExplodeRibbonSwap.Rebind";
        }

        /// <summary>
        /// Fires the native Revit explode command from a clean Idling context by calling
        /// the native RibbonButton's CommandHandler.Execute directly.
        ///
        /// UIElementActivated is a POST-dispatch notification — it fires after AdWindows has
        /// already called ICommand.Execute on the button. Re-invoking UIElementActivated via
        /// reflection does NOT trigger command execution again. The real trigger is calling
        /// CommandHandler.Execute on the native RibbonButton whose CommandHandler Revit set.
        ///
        /// PostCommand is also off the table: ID_IMPORT_INSTANCE_EXPLODE always returns
        /// CanPostCommand=false because it is not in the PostableCommand enum.
        /// </summary>
        private sealed class PostExplodeHandler : IExternalEventHandler
        {
            private readonly CADExplodeRibbonSwap _owner;
            public PostExplodeHandler(CADExplodeRibbonSwap owner) { _owner = owner; }

            public void Execute(UIApplication app)
            {
                try
                {
                    var commandIdStr = _owner._pendingExplodeCommandId;
                    if (string.IsNullOrEmpty(commandIdStr)) return;
                    _owner._pendingExplodeCommandId = null;

                    bool isFull = commandIdStr == "ID_IMPORT_INSTANCE_EXPLODE";

                    // Restore the ImportInstance selection captured at click time.
                    // The protection dialog (modal WPF) clears selection while open.
                    var savedSel = _owner._savedSelection;
                    _owner._savedSelection = null;
                    if (savedSel != null && savedSel.Count > 0)
                    {
                        try { app.ActiveUIDocument?.Selection.SetElementIds(savedSel); }
                        catch (Exception selEx)
                        {
                            _owner._logger?.LogWarning($"[ExplodeSwap] PostExplode: selection restore failed: {selEx.Message}");
                        }
                    }

                    // Approval granted — reveal native button so user can click it directly.
                    // Shadow hides so the native takes its place in the ribbon.
                    // Audit gate is armed now so post-action won't double-audit when user clicks native.
                    CADExplodeAuditGate.Suppress();
                    _owner.RevealNativeForExplode();
                    _owner.ShowOverrideNotification(app);
                    _owner._logger?.LogInfo("[ExplodeSwap] PostExplode: native Explode button revealed — waiting for user click.");
                }
                catch (Exception ex)
                {
                    _owner._logger?.LogWarning($"[ExplodeSwap] PostExplodeHandler.Execute failed: {ex.Message}");
                }
            }

            public string GetName() => "BIManage.CADExplodeRibbonSwap.PostExplode";
        }

        /// <summary>
        /// No-op <see cref="System.Windows.Input.ICommand"/> implementation we attach to every
        /// shadow ribbon button. AdWindows greys a button out when it has no CommandHandler;
        /// providing this dummy command keeps the button enabled while the actual click is
        /// handled in <see cref="OnUIElementActivated"/>. Execute is intentionally empty —
        /// allowing the click to flow back to ZeManage's protection logic, not Revit's
        /// command dispatch (the shadow ids don't correspond to any registered Revit command).
        /// </summary>
        private sealed class AlwaysEnabledRibbonCommand : System.Windows.Input.ICommand
        {
            public static readonly AlwaysEnabledRibbonCommand Instance = new();
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) { /* handled in UIElementActivated */ }
            // ICommand requires CanExecuteChanged. Static instance never changes.
            public event EventHandler? CanExecuteChanged
            {
                add { /* nop — CanExecute is always true */ }
                remove { /* nop */ }
            }
        }

        // ── AdWindows interaction (all methods Try* — exceptions never escape) ──────────────

        private bool TrySubscribeToRibbonChanges()
        {
            try
            {
                if (!_uiElementActivatedSubscribed)
                {
                    ComponentManager.UIElementActivated += OnUIElementActivated;
                    _uiElementActivatedSubscribed = true;
                }

                var ribbon = ComponentManager.Ribbon;
                if (ribbon == null)
                {
                    _logger?.LogDebug("[ExplodeSwap] ComponentManager.Ribbon is null at init — will retry on first UI activation.");
                    return true; // still consider initialized; UIElementActivated will drive future rebinds
                }


                // Watch the Tabs collection itself — Revit adds contextual tabs (e.g. the
                // "Modify | …" tabs that appear when an element is selected) AT RUNTIME, so
                // tabs that didn't exist at init time would otherwise never get a Panels
                // watcher. This is the missing piece that left the Imports panel uncovered.
                if (!_tabsCollectionSubscribed)
                {
                    if (ribbon.Tabs is System.Collections.Specialized.INotifyCollectionChanged observableTabs)
                    {
                        observableTabs.CollectionChanged += OnTabsChanged;
                        _tabsCollectionSubscribed = true;
                    }
                }

                foreach (var tab in ribbon.Tabs)
                {
                    HookTabPanelsChanges(tab);
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[ExplodeSwap] Failed to subscribe to ribbon events (AdWindows API unavailable?): {ex.Message}");
                return false;
            }
        }

        private void OnPanelsChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            try
            {
                TryRebindAllOpenPanels();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[ExplodeSwap] OnPanelsChanged handler failed: {ex.Message}");
            }
        }

        private void OnTabActivated(object? sender, EventArgs e)
        {
            try { TryRebindAllOpenPanels(); }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[ExplodeSwap] OnTabActivated failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Subscribe to a tab's Panels.CollectionChanged so we get notified when its contextual
        /// panels (like the Imports panel on Modify) appear or disappear. Idempotent — uses an
        /// internal set to avoid double-hooking the same tab.
        /// Also subscribes to RibbonTab.Activated so the shadow button's CommandHandler is
        /// re-attached when Revit returns focus to the tab after a PostCommand completes —
        /// without this, AdWindows drops the handler during the post-command ribbon rebuild
        /// and leaves the shadow button grey.
        /// </summary>
        private void HookTabPanelsChanges(Autodesk.Windows.RibbonTab? tab)
        {
            if (tab?.Panels == null) return;
            if (!_hookedTabs.Add(tab)) return; // already hooked
            try
            {
                tab.Panels.CollectionChanged -= OnPanelsChanged;
                tab.Panels.CollectionChanged += OnPanelsChanged;
                tab.Activated -= OnTabActivated;
                tab.Activated += OnTabActivated;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[ExplodeSwap] HookTabPanelsChanges skip on tab '{tab?.Title ?? "?"}': {ex.Message}");
            }
        }

        /// <summary>
        /// Fired when ComponentManager.Ribbon.Tabs collection is mutated — e.g. Revit adds the
        /// contextual "Modify | <element>" tab when an ImportInstance is selected. We hook the
        /// new tab's Panels collection and immediately try to install the swap (in case the
        /// Imports panel is already present).
        /// </summary>
        private void OnTabsChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            try
            {
                if (e.NewItems != null)
                {
                    foreach (var item in e.NewItems)
                    {
                        if (item is Autodesk.Windows.RibbonTab newTab)
                            HookTabPanelsChanges(newTab);
                    }
                }
                TryRebindAllOpenPanels();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[ExplodeSwap] OnTabsChanged handler failed: {ex.Message}");
            }
        }

        private void TryRebindAllOpenPanels()
        {
            try
            {
                var ribbon = ComponentManager.Ribbon;
                if (ribbon == null)
                {
                    _logger?.LogDebug("[ExplodeSwap] Rebind skipped: ComponentManager.Ribbon is null.");
                    return;
                }

                bool protectionActive = IsExplodeProtectionEnabled();
                int tabCount = 0, panelCount = 0, hits = 0;

                foreach (var tab in ribbon.Tabs)
                {
                    if (tab?.Panels == null) continue;
                    tabCount++;
                    // Defensive: re-hook in case a stable tab's Panels collection was replaced.
                    HookTabPanelsChanges(tab);
                    foreach (var panel in tab.Panels)
                    {
                        panelCount++;
                        if (TryRebindOnePanel(panel, protectionActive)) hits++;
                    }
                }

                if (hits > 0)
                {
                    _logger?.LogInfo($"[ExplodeSwap] Rebind scan: {tabCount} tabs, {panelCount} panels, found {hits} Explode panel(s) (protectionActive={protectionActive}).");
                }
                else
                {
                    _logger?.LogDebug($"[ExplodeSwap] Rebind scan: {tabCount} tabs, {panelCount} panels, no Explode panel found yet (protectionActive={protectionActive}).");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[ExplodeSwap] TryRebindAllOpenPanels failed: {ex.Message}");
            }
        }


        internal void ShowOverrideNotification(UIApplication app)
        {
            try
            {
                // Resolve popup position: horizontally centred under the ribbon.
                // Try to anchor to the Revit window; fall back to primary screen centre.
                double left = System.Windows.SystemParameters.PrimaryScreenWidth / 2.0 - 160;
                double top  = 110;
                try
                {
                    var src = System.Windows.Interop.HwndSource.FromHwnd(app.MainWindowHandle);
                    if (src?.RootVisual is System.Windows.FrameworkElement root)
                    {
                        var pt = root.PointToScreen(
                            new System.Windows.Point(root.ActualWidth / 2.0, 90));
                        left = pt.X - 160;
                        top  = pt.Y;
                    }
                }
                catch { /* use fallback position */ }

                var border = new System.Windows.Controls.Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(255, 243, 205)),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(230, 160, 20)),
                    BorderThickness = new System.Windows.Thickness(1.5),
                    CornerRadius    = new System.Windows.CornerRadius(5),
                    Padding         = new System.Windows.Thickness(14, 10, 14, 10)
                };

                var text = new System.Windows.Controls.TextBlock
                {
                    Text            = "Explode protection overridden.\nClick the Explode button to proceed.",
                    FontSize        = 12,
                    Foreground      = new System.Windows.Media.SolidColorBrush(
                                        System.Windows.Media.Color.FromRgb(90, 60, 0)),
                    TextWrapping    = System.Windows.TextWrapping.Wrap,
                    MaxWidth        = 260,
                    TextAlignment   = System.Windows.TextAlignment.Center
                };
                border.Child = text;

                var popup = new System.Windows.Window
                {
                    WindowStyle     = System.Windows.WindowStyle.None,
                    AllowsTransparency = true,
                    Background      = System.Windows.Media.Brushes.Transparent,
                    ShowInTaskbar   = false,
                    Topmost         = true,
                    ResizeMode      = System.Windows.ResizeMode.NoResize,
                    SizeToContent   = System.Windows.SizeToContent.WidthAndHeight,
                    Left            = left,
                    Top             = top,
                    Content         = border,
                    Cursor          = System.Windows.Input.Cursors.Hand
                };

                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(4)
                };
                timer.Tick += (s, _) => { timer.Stop(); popup.Close(); };
                popup.MouseLeftButtonDown += (s, _) => popup.Close();
                timer.Start();
                popup.Show();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[ExplodeSwap] ShowOverrideNotification failed: {ex.Message}");
            }
        }

        internal void RevealNativeForExplode()
        {
            try
            {
                var ribbon = ComponentManager.Ribbon;
                if (ribbon == null) return;

                foreach (var tab in ribbon.Tabs)
                {
                    if (tab?.Panels == null) continue;
                    foreach (var panel in tab.Panels)
                    {
                        var nativeSplit = panel?.FindItem(NativeSplitButtonControlId, true)
                                         as Autodesk.Windows.RibbonSplitButton;
                        if (nativeSplit == null) continue;

                        nativeSplit.IsVisible = true;
                        nativeSplit.IsSplit = true;

                        var shadowBtn = panel.FindItem(ShadowButtonId, true);
                        if (shadowBtn != null)
                            shadowBtn.IsVisible = false;

                        _logger?.LogInfo("[ExplodeSwap] RevealNativeForExplode: native visible, shadow hidden.");
                        return;
                    }
                }

                _logger?.LogWarning("[ExplodeSwap] RevealNativeForExplode: native split button not found in ribbon.");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[ExplodeSwap] RevealNativeForExplode failed: {ex.Message}");
            }
        }

        private bool TryRebindOnePanel(Autodesk.Windows.RibbonPanel panel, bool protectionActive)
        {
            try
            {
                var nativeSplit = panel?.FindItem(NativeSplitButtonControlId, true);
                if (nativeSplit == null) return false; // not an Explode panel

                if (!protectionActive)
                {
                    // Make sure native is visible and any shadow is hidden — admins disabled
                    // the protection, the user should see vanilla Revit UI.
                    if (nativeSplit.IsVisible == false) nativeSplit.IsVisible = true;
                    var existingShadow = panel.FindItem(ShadowButtonId, true);
                    if (existingShadow != null && existingShadow.IsVisible == true)
                        existingShadow.IsVisible = false;
                    return true;
                }

                // Protection ON — install shadow button if not already, then hide native + show shadow.
                EnsureShadowButtonInstalled(panel, nativeSplit);

                // Hide native (shadow takes the visual slot) but ALWAYS keep IsEnabled=true.
                if (nativeSplit.IsVisible == true) nativeSplit.IsVisible = false;
                nativeSplit.IsEnabled = true;

                var shadow = panel.FindItem(ShadowButtonId, true);
                if (shadow != null)
                {
                    shadow.IsVisible = true;
                    shadow.IsEnabled = true;
                    // Re-attach CommandHandler if AdWindows dropped it during a ribbon rebuild.
                    if (shadow is Autodesk.Windows.RibbonButton btn && btn.CommandHandler == null)
                        btn.CommandHandler = AlwaysEnabledRibbonCommand.Instance;
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[ExplodeSwap] TryRebindOnePanel skip ({panel?.Source?.Title ?? "?"}): {ex.Message}");
                return false;
            }
        }

        private void EnsureShadowButtonInstalled(Autodesk.Windows.RibbonPanel panel, Autodesk.Windows.RibbonItem nativeSplit)
        {
            try
            {
                if (panel?.Source == null) return;

                var existing = panel.FindItem(ShadowButtonId, true);
                if (existing != null) return;

                // Shadow is a plain RibbonButton — no dropdown, no children.
                // Always Large so the icon appears on top and the label below.
                //
                // Icon resolution cascade:
                //   1. Native parent's LargeImage (32×32) — ideal.
                //   2. Native children's LargeImage.
                //   3. Native children's Image (16×16).
                //   4. Native parent's Image (16×16).
                // If we end up with only a 16×16 source, scale it to 32×32 via
                // DrawingImage so AdWindows renders it in the Large slot (it will not
                // render a 16×16 ImageSource there even if set as LargeImage).
                var nativeSplitTyped = nativeSplit as Autodesk.Windows.RibbonSplitButton;

                System.Windows.Media.ImageSource? largeImg = nativeSplit.LargeImage;
                System.Windows.Media.ImageSource? smallImg = nativeSplit.Image;

                if ((largeImg == null || smallImg == null) && nativeSplitTyped?.Items != null)
                {
                    foreach (var child in nativeSplitTyped.Items)
                    {
                        if (child == null) continue;
                        largeImg ??= child.LargeImage;
                        smallImg ??= child.Image;
                        if (largeImg != null && smallImg != null) break;
                    }
                }

                smallImg ??= largeImg;

                // If still no 32×32, scale whatever we have up to 32×32 via DrawingImage.
                if (largeImg == null && smallImg != null)
                {
                    try
                    {
                        var drawing = new System.Windows.Media.ImageDrawing(
                            smallImg, new System.Windows.Rect(0, 0, 32, 32));
                        var di = new System.Windows.Media.DrawingImage(drawing);
                        di.Freeze();
                        largeImg = di;
                    }
                    catch { largeImg = smallImg; }
                }
                largeImg ??= smallImg;

                _logger?.LogInfo($"[ExplodeSwap] Icon — parent LargeImage={(nativeSplit.LargeImage != null ? "set" : "null")}, " +
                    $"parent Image={(nativeSplit.Image != null ? "set" : "null")}, " +
                    $"resolved={(largeImg != null ? "set" : "NULL — button will have no icon")}.");

                var shadowBtn = new Autodesk.Windows.RibbonButton
                {
                    Id          = ShadowButtonId,
                    Text        = "Explode",
                    Description = nativeSplit.Description,
                    Image       = smallImg,
                    LargeImage  = largeImg,
                    Size        = Autodesk.Windows.RibbonItemSize.Large,
                    ShowImage   = true,
                    ShowText    = true,
                    ResizeStyle = nativeSplit.ResizeStyle,
                    IsEnabled   = true,
                    IsVisible   = false,
                    CommandHandler   = AlwaysEnabledRibbonCommand.Instance,
                    ToolTip          = nativeSplit.ToolTip,
                    KeyTip           = nativeSplit.KeyTip,
                    IsToolTipEnabled = nativeSplit.IsToolTipEnabled,
                    AllowInStatusBar = nativeSplit.AllowInStatusBar,
                    AllowInToolBar   = nativeSplit.AllowInToolBar
                };

                int insertAt = panel.Source.Items.IndexOf(nativeSplit);
                if (insertAt < 0) panel.Source.Items.Add(shadowBtn);
                else panel.Source.Items.Insert(insertAt + 1, shadowBtn);

                _logger?.LogInfo($"[ExplodeSwap] Shadow Explode button installed on panel '{panel.Source.Title}' " +
                    $"(Size={shadowBtn.Size}, Image={(shadowBtn.Image != null ? "set" : "null")}, " +
                    $"LargeImage={(shadowBtn.LargeImage != null ? "set" : "null")}).");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[ExplodeSwap] EnsureShadowButtonInstalled failed: {ex.Message}");
            }
        }

        // ── Click handling ──────────────────────────────────────────────────────────────────

        private void OnUIElementActivated(object sender, UIElementActivatedEventArgs e)
        {
            try
            {
                if (e?.Item?.Id != ShadowButtonId) return;

                _logger?.LogInfo("[ExplodeSwap] Shadow Explode button clicked — showing protection.");

                ICollection<ElementId>? selectionSnapshot = null;
                try { selectionSnapshot = _uiApp?.ActiveUIDocument?.Selection.GetElementIds(); }
                catch { /* non-critical */ }

                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    new Action(() => HandleShadowExplodeClick(true, selectionSnapshot)),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[ExplodeSwap] OnUIElementActivated handler failed: {ex.Message}");
            }
        }

        private void HandleShadowExplodeClick(bool isFull, ICollection<ElementId>? selectionSnapshot)
        {
            try
            {
                if (_uiApp == null) return;

                var doc = _uiApp.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    _logger?.LogDebug("[ExplodeSwap] No active document on shadow click — ignoring.");
                    return;
                }

                var registeredModelsRepo = _registeredModelsRepoGetter?.Invoke();
                if (registeredModelsRepo != null)
                {
                    var swapModelGuid = ModelGuidHelper.GetModelGuid(doc, _logger);
                    var isSwapRegistered = !string.IsNullOrEmpty(swapModelGuid) &&
                        registeredModelsRepo.IsModelRegisteredAsync(swapModelGuid).GetAwaiter().GetResult();
                    if (!isSwapRegistered)
                    {
                        _logger?.LogInfo("[ExplodeSwap] Skipping protection — not a registered model — dispatching directly.");
                        DispatchNativeExplode(isFull, selectionSnapshot);
                        return;
                    }
                }

                var protectionService = _eventProtectionGetter?.Invoke();
                if (protectionService == null)
                {
                    _logger?.LogDebug("[ExplodeSwap] EventProtectionService unavailable — falling through to native command.");
                    DispatchNativeExplode(isFull, selectionSnapshot);
                    return;
                }

                var settings = protectionService.GetProtectionByDummyCommandId("Ze_CADExplodeProtection");
                if (settings == null || !settings.Enabled)
                {
                    _logger?.LogDebug("[ExplodeSwap] Ze_CADExplodeProtection not enabled — letting explode proceed.");
                    DispatchNativeExplode(isFull, selectionSnapshot);
                    return;
                }

                var handler = _interventionHandlerFactory?.Invoke();
                if (handler == null)
                {
                    _logger?.LogWarning("[ExplodeSwap] EventInterventionHandler factory returned null — allowing explode.");
                    DispatchNativeExplode(isFull, selectionSnapshot);
                    return;
                }

                var context = new EventContext
                {
                    Document = doc,
                    FilePath = doc.PathName,
                    CurrentRevitVersion = doc.Application?.VersionNumber
                };

                var result = handler.ProcessIntervention(settings, context,
                    isFull ? "Full Explode requested from ribbon button" : "Partial Explode requested from ribbon button");

                LogAudit(settings, doc, result, isFull);

                if (!result.Allowed)
                {
                    _logger?.LogInfo($"[ExplodeSwap] {(isFull ? "Full" : "Partial")} Explode blocked by protection (mode={settings.Mode}).");
                    return; // shadow stays visible, native stays hidden — user can't bypass
                }

                _logger?.LogInfo($"[ExplodeSwap] {(isFull ? "Full" : "Partial")} Explode allowed (override={result.UserOverrode}) — dispatching.");
                DispatchNativeExplode(isFull, selectionSnapshot);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ExplodeSwap] HandleShadowExplodeClick failed: {ex.Message}", ex);
            }
        }

        private void DispatchNativeExplode(bool isFull, ICollection<ElementId>? selectionSnapshot)
        {
            try
            {
                // Native button must be HIDDEN (IsVisible=false) but NOT DISABLED (IsEnabled=true)
                // when PostCommand fires. This is already the state installed by the shadow swap:
                // native is hidden behind the shadow, and IsEnabled is always left true.
                // Do NOT make native visible here — that causes the second-click fallback pattern
                // where the user sees the native button and has to click it manually.
                // Only touch IsEnabled if something set it to false (it never should be).

                _pendingExplodeCommandId = isFull
                    ? "ID_IMPORT_INSTANCE_EXPLODE"
                    : "ID_IMPORT_INST_PARTIAL_EXPLODE";
                _savedSelection = selectionSnapshot;

                if (_postExplodeEvent != null && !_postExplodeEvent.IsPending)
                    _postExplodeEvent.Raise();

                _logger?.LogInfo($"[ExplodeSwap] {(isFull ? "Full" : "Partial")} Explode — PostExplodeEvent raised (ICommand.Execute will fire on next Idling tick).");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[ExplodeSwap] DispatchNativeExplode failed: {ex.Message}");
            }
        }

        private bool IsExplodeProtectionEnabled()
        {
            try
            {
                var svc = _eventProtectionGetter?.Invoke();
                if (svc == null || !svc.IsProtectionEnabled) return false;
                var settings = svc.GetProtectionByDummyCommandId("Ze_CADExplodeProtection");
                if (settings == null || !settings.Enabled) return false;
                // Shadow button is only needed for Assist/Protect modes.
                // Notify mode: native button works normally; post-action audit handles logging.
                return settings.Mode != InterventionMode.Notify;
            }
            catch
            {
                return false;
            }
        }

        private void LogAudit(EventProtectionSettings settings, Document doc, EventInterventionResult result, bool isFull)
        {
            try
            {
                var repo = _auditRepoGetter?.Invoke();
                if (repo == null) return;

                var entry = new ProtectionAuditEntry
                {
                    AuditLogId = Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow,
                    UserName = Environment.UserName,
                    ModelGuid = TryGetModelGuid(doc),
                    CommandName = isFull ? "FullExplode" : "PartialExplode",
                    Mode = MapMode(settings.Mode),
                    Action = result.Allowed
                        ? (result.UserOverrode ? ProtectionAction.Override : ProtectionAction.Allowed)
                        : ProtectionAction.Blocked,
                    Reason = result.Reason ?? (isFull ? "Full Explode" : "Partial Explode"),
                    EventSource = "Event Restriction",
                    UserComment = result.UserComment,
                    SessionId = string.Empty
                };
                _ = repo.SaveAuditEntryAsync(entry);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"[ExplodeSwap] LogAudit failed: {ex.Message}");
            }
        }

        private static string? TryGetModelGuid(Document doc)
        {
            try { return Helpers.ModelGuidHelper.GetModelGuid(doc, null); }
            catch { return null; }
        }

        private static BIManage.Core.Rules.Models.ProtectionMode MapMode(InterventionMode mode)
        {
            return mode switch
            {
                InterventionMode.Notify => BIManage.Core.Rules.Models.ProtectionMode.Notify,
                InterventionMode.Assist => BIManage.Core.Rules.Models.ProtectionMode.Assist,
                InterventionMode.Protect => BIManage.Core.Rules.Models.ProtectionMode.Protect,
                _ => BIManage.Core.Rules.Models.ProtectionMode.Notify
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_uiElementActivatedSubscribed)
                    ComponentManager.UIElementActivated -= OnUIElementActivated;

                var ribbon = ComponentManager.Ribbon;
                if (ribbon != null)
                {
                    if (_tabsCollectionSubscribed && ribbon.Tabs is System.Collections.Specialized.INotifyCollectionChanged observableTabs)
                    {
                        try { observableTabs.CollectionChanged -= OnTabsChanged; } catch { }
                    }
                    foreach (var tab in ribbon.Tabs)
                    {
                        if (tab?.Panels == null) continue;
                        try { tab.Panels.CollectionChanged -= OnPanelsChanged; } catch { }
                        try { tab.Activated -= OnTabActivated; } catch { }
                    }
                }
                _hookedTabs.Clear();

                // ExternalEvent has no Dispose API in the Revit SDK — drop the references and
                // let GC reclaim. The handler stops being raised once the event is unrooted.
                _rebindEvent = null;
                _rebindHandler = null;
                _postExplodeEvent = null;
                _postExplodeHandler = null;
            }
            catch { /* swallow on shutdown */ }
        }
    }
}
