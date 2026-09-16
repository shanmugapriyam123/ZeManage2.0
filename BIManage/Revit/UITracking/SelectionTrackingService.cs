using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIManage.Common.Helpers;
using BIManage.Core.Features;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Productivity;

namespace BIManage.Revit.UITracking
{
    /// <summary>
    ///     Tracks selection changes in Revit
    ///     Monitors when users select/deselect elements in the active view
    /// </summary>
    public class SelectionTrackingService : ISelectionTrackingService
    {
        private readonly IFeatureToggleService _featureToggleService;
        private readonly IProductivityTracker _productivityTracker;
        private readonly ILogger? _logger;
        private UIApplication? _uiApplication;
        private ICollection<ElementId> _currentSelection;
        private System.Timers.Timer? _selectionPollTimer;
        private bool _isRegistered;

        public SelectionTrackingService(
            IFeatureToggleService featureToggleService,
            IProductivityTracker productivityTracker,
            ILogger? logger)
        {
            _featureToggleService = featureToggleService ?? throw new ArgumentNullException(nameof(featureToggleService));
            _productivityTracker = productivityTracker ?? throw new ArgumentNullException(nameof(productivityTracker));
            _logger = logger;
            _currentSelection = new List<ElementId>();
        }

        /// <summary>
        ///     Currently selected element IDs
        /// </summary>
        public ICollection<ElementId> CurrentSelection => new List<ElementId>(_currentSelection);

        /// <summary>
        ///     Event fired when selection changes
        /// </summary>
        public event EventHandler<SelectionChangedEventArgs>? SelectionChanged;

        /// <summary>
        ///     Register selection tracking with UIApplication
        ///     Note: Revit doesn't expose SelectionChanged event, so we poll periodically
        /// </summary>
        public void Register(UIApplication uiApplication)
        {
            if (uiApplication == null) throw new ArgumentNullException(nameof(uiApplication));
            if (_isRegistered)
            {
                _logger?.LogWarning("SelectionTrackingService already registered");
                return;
            }

            _uiApplication = uiApplication;

            // Initialize current selection
            try
            {
                if (_uiApplication.ActiveUIDocument?.Selection != null)
                {
                    _currentSelection = _uiApplication.ActiveUIDocument.Selection.GetElementIds();
                    _logger?.LogInfo($"Selection tracking initialized with {_currentSelection.Count} elements selected");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to initialize current selection: {ex.Message}");
            }

            // Start polling timer (check every 500ms)
            // Note: This is necessary because Revit API doesn't expose SelectionChanged event
            _selectionPollTimer = new System.Timers.Timer(500);
            _selectionPollTimer.Elapsed += OnSelectionPollTimerElapsed;
            _selectionPollTimer.AutoReset = true;
            _selectionPollTimer.Start();

            _isRegistered = true;
            _logger?.LogInfo("Selection tracking service registered (polling every 500ms)");
        }

        /// <summary>
        ///     Unregister selection tracking
        /// </summary>
        public void Unregister()
        {
            if (!_isRegistered) return;

            try
            {
                _selectionPollTimer?.Stop();
                _selectionPollTimer?.Dispose();
                _selectionPollTimer = null;

                _isRegistered = false;
                _logger?.LogInfo("Selection tracking service unregistered");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Error unregistering selection tracking: {ex.Message}");
            }
        }

        /// <summary>
        ///     Poll for selection changes (Revit doesn't expose SelectionChanged event)
        /// </summary>
        private void OnSelectionPollTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                if (_featureToggleService.IsGlobalPaused || _featureToggleService.IsEmployeeCaptureDisabled) return;
                if (_uiApplication?.ActiveUIDocument?.Selection == null) return;

                // Get current selection
                var newSelection = _uiApplication.ActiveUIDocument.Selection.GetElementIds();

                // Check if selection changed
                if (SelectionsAreEqual(_currentSelection, newSelection))
                    return;

                // Selection changed - raise event
                var previousSelection = _currentSelection;
                _currentSelection = newSelection;

                var selectionInfo = new SelectionChangedEventArgs(
                    previousSelection,
                    _currentSelection,
                    _uiApplication.ActiveUIDocument.Document);

                // Record activity for productivity tracking
                _productivityTracker.RecordActivity();

                _logger?.LogDebug($"Selection changed: {selectionInfo}");

                // Raise event for subscribers
                SelectionChanged?.Invoke(this, selectionInfo);

                // TODO Phase 2: Persist selection change to SQLite for analytics
                // TODO Phase 3: Track selection patterns for productivity insights
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error polling selection: {ex.Message}", ex);
            }
        }

        /// <summary>
        ///     Compare two selections for equality
        /// </summary>
        private bool SelectionsAreEqual(ICollection<ElementId> selection1, ICollection<ElementId> selection2)
        {
            if (selection1.Count != selection2.Count)
                return false;

            var set1 = new HashSet<ElementId>(selection1);
            return selection2.All(id => set1.Contains(id));
        }

        /// <summary>
        ///     Get selected element descriptions for logging
        /// </summary>
        private string GetSelectionDescription(ICollection<ElementId> elementIds, Document document)
        {
            if (elementIds.Count == 0)
                return "0 elements";

            if (elementIds.Count > 5)
                return $"{elementIds.Count} elements";

            try
            {
                var descriptions = elementIds
                    .Take(5)
                    .Select(id =>
                    {
                        var element = document.GetElement(id);
                        var category = element?.Category?.Name ?? "Unknown";
                        return $"{category} (ID: {id.GetIdValue()})";
                    });

                return string.Join(", ", descriptions);
            }
            catch
            {
                return $"{elementIds.Count} elements";
            }
        }

        public void Dispose()
        {
            Unregister();
        }
    }

    /// <summary>
    ///     Event args for selection changes
    /// </summary>
    public class SelectionChangedEventArgs : EventArgs
    {
        public SelectionChangedEventArgs(
            ICollection<ElementId> previousSelection,
            ICollection<ElementId> currentSelection,
            Document document)
        {
            PreviousSelection = new List<ElementId>(previousSelection);
            CurrentSelection = new List<ElementId>(currentSelection);
            Document = document ?? throw new ArgumentNullException(nameof(document));
            Timestamp = DateTime.UtcNow;

            // Calculate deltas
            var previousSet = new HashSet<ElementId>(previousSelection);
            var currentSet = new HashSet<ElementId>(currentSelection);

            AddedToSelection = currentSet.Except(previousSet).ToList();
            RemovedFromSelection = previousSet.Except(currentSet).ToList();
        }

        /// <summary>
        ///     Previous selection
        /// </summary>
        public ICollection<ElementId> PreviousSelection { get; }

        /// <summary>
        ///     Current selection
        /// </summary>
        public ICollection<ElementId> CurrentSelection { get; }

        /// <summary>
        ///     Elements added to selection
        /// </summary>
        public ICollection<ElementId> AddedToSelection { get; }

        /// <summary>
        ///     Elements removed from selection
        /// </summary>
        public ICollection<ElementId> RemovedFromSelection { get; }

        /// <summary>
        ///     Document containing the selection
        /// </summary>
        public Document Document { get; }

        /// <summary>
        ///     UTC timestamp of selection change
        /// </summary>
        public DateTime Timestamp { get; }

        public override string ToString()
        {
            var added = AddedToSelection.Count;
            var removed = RemovedFromSelection.Count;
            var total = CurrentSelection.Count;

            return $"Selection changed: +{added} -{removed} (Total: {total}) in document '{Document.Title}'";
        }
    }
}
