using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Common.Helpers;
using BIManage.Core.Features;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Productivity;

namespace BIManage.Revit.UITracking
{
    /// <summary>
    ///     Tracks view activation changes in Revit
    ///     Monitors when users switch between views (plans, elevations, 3D views, etc.)
    /// </summary>
    public class ViewTrackingService : IViewTrackingService
    {
        private readonly IFeatureToggleService _featureToggleService;
        private readonly IProductivityTracker _productivityTracker;
        private readonly ILogger? _logger;
        private UIApplication? _uiApplication;
        private View? _currentView;
        private bool _isRegistered;

        public ViewTrackingService(
            IFeatureToggleService featureToggleService,
            IProductivityTracker productivityTracker,
            ILogger? logger)
        {
            _featureToggleService = featureToggleService ?? throw new ArgumentNullException(nameof(featureToggleService));
            _productivityTracker = productivityTracker ?? throw new ArgumentNullException(nameof(productivityTracker));
            _logger = logger;
        }

        /// <summary>
        ///     Currently active view
        /// </summary>
        public View? CurrentView => _currentView;

        /// <summary>
        ///     Event fired when view changes
        /// </summary>
        public event EventHandler<ViewActivatedEventArgs>? ViewChanged;

        /// <summary>
        ///     Register view tracking with UIApplication
        /// </summary>
        public void Register(UIApplication uiApplication)
        {
            if (uiApplication == null) throw new ArgumentNullException(nameof(uiApplication));
            if (_isRegistered)
            {
                _logger?.LogWarning("ViewTrackingService already registered");
                return;
            }

            _uiApplication = uiApplication;
            _uiApplication.ViewActivated += OnViewActivated;
            _isRegistered = true;

            // Initialize current view
            try
            {
                if (_uiApplication.ActiveUIDocument?.Document != null)
                {
                    _currentView = _uiApplication.ActiveUIDocument.Document.ActiveView;
                    _logger?.LogInfo($"View tracking initialized with view: {GetViewDescription(_currentView)}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to initialize current view: {ex.Message}");
            }

            _logger?.LogInfo("View tracking service registered");
        }

        /// <summary>
        ///     Unregister view tracking
        /// </summary>
        public void Unregister()
        {
            if (!_isRegistered || _uiApplication == null) return;

            try
            {
                _uiApplication.ViewActivated -= OnViewActivated;
                _isRegistered = false;
                _logger?.LogInfo("View tracking service unregistered");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Error unregistering view tracking: {ex.Message}");
            }
        }

        /// <summary>
        ///     Handler for ViewActivated event
        /// </summary>
        private void OnViewActivated(object sender, Autodesk.Revit.UI.Events.ViewActivatedEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused) return;

                // Validate that the view and document are still valid
                if (e.CurrentActiveView == null || !e.CurrentActiveView.IsValidObject)
                {
                    _logger?.LogDebug("ViewActivated event received with invalid view object - likely document closing");
                    return;
                }

                if (e.Document == null || !e.Document.IsValidObject)
                {
                    _logger?.LogDebug("ViewActivated event received with invalid document - likely document closing");
                    return;
                }

                var previousView = _currentView;
                _currentView = e.CurrentActiveView;

                // Record activity for productivity tracking
                _productivityTracker.RecordActivity();

                var viewInfo = new ViewActivatedEventArgs(
                    previousView,
                    _currentView,
                    e.Document);

                _logger?.LogInfo($"View changed: {viewInfo}");

                // Raise event for subscribers
                ViewChanged?.Invoke(this, viewInfo);

                // TODO Phase 2: Persist view change to SQLite for analytics
                // TODO Phase 3: Track view dwell time for productivity metrics
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in OnViewActivated handler: {ex.Message}", ex);
            }
        }

        /// <summary>
        ///     Get descriptive string for a view
        /// </summary>
        private string GetViewDescription(View? view)
        {
            if (view == null) return "None";

            try
            {
                var viewType = view.ViewType.ToString();
                var viewName = view.Name;
                return $"{viewType}: {viewName} (ID: {view.Id.GetIdValue()})";
            }
            catch
            {
                return $"View ID: {view.Id.GetIdValue()}";
            }
        }

        public void Dispose()
        {
            Unregister();
        }
    }

    /// <summary>
    ///     Event args for view activation changes
    /// </summary>
    public class ViewActivatedEventArgs : EventArgs
    {
        public ViewActivatedEventArgs(View? previousView, View currentView, Document document)
        {
            PreviousView = previousView;
            CurrentView = currentView ?? throw new ArgumentNullException(nameof(currentView));
            Document = document ?? throw new ArgumentNullException(nameof(document));
            Timestamp = DateTime.UtcNow;
        }

        /// <summary>
        ///     Previous active view (null if first view activation)
        /// </summary>
        public View? PreviousView { get; }

        /// <summary>
        ///     Newly activated view
        /// </summary>
        public View CurrentView { get; }

        /// <summary>
        ///     Document containing the view
        /// </summary>
        public Document Document { get; }

        /// <summary>
        ///     UTC timestamp of view change
        /// </summary>
        public DateTime Timestamp { get; }

        public override string ToString()
        {
            try
            {
                var previousViewType = "None";
                var previousViewName = "None";

                if (PreviousView != null && PreviousView.IsValidObject)
                {
                    try
                    {
                        previousViewType = PreviousView.ViewType.ToString();
                        previousViewName = PreviousView.Name;
                    }
                    catch
                    {
                        previousViewType = $"View ID: {PreviousView.Id.GetIdValue()}";
                        previousViewName = "(Invalid)";
                    }
                }

                var currentViewType = "Unknown";
                var currentViewName = "Unknown";

                if (CurrentView != null && CurrentView.IsValidObject)
                {
                    try
                    {
                        currentViewType = CurrentView.ViewType.ToString();
                        currentViewName = CurrentView.Name;
                    }
                    catch
                    {
                        currentViewType = $"View ID: {CurrentView.Id.GetIdValue()}";
                        currentViewName = "(Invalid)";
                    }
                }

                var documentTitle = "(Unknown)";
                if (Document != null && Document.IsValidObject)
                {
                    try
                    {
                        documentTitle = Document.Title;
                    }
                    catch
                    {
                        documentTitle = "(Invalid Document)";
                    }
                }

                return $"View changed from [{previousViewType}: {previousViewName}] to [{currentViewType}: {currentViewName}] in document '{documentTitle}'";
            }
            catch
            {
                return "View changed (error getting details - objects may have been deleted)";
            }
        }
    }
}
