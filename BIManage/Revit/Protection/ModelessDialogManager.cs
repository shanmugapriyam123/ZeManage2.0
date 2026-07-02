using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Rules.Models;
using BIManage.Infrastructure.Logging;
using BIManage.Views.Protection;

namespace BIManage.Revit.Protection
{
    /// <summary>
    /// Manages modeless guidance dialogs
    /// Provides non-blocking notifications that don't interrupt workflow
    /// Thread-safe, supports multiple concurrent dialogs
    /// </summary>
    public class ModelessDialogManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, ModelessGuideWindow> _activeDialogs;
        private IntPtr _revitWindowHandle;
        private bool _disposed;

        public ModelessDialogManager(ILogger logger)
        {
            _logger = logger;
            _activeDialogs = new ConcurrentDictionary<string, ModelessGuideWindow>();
        }

        /// <summary>
        /// Set the Revit main window handle for proper parenting
        /// </summary>
        public void SetRevitWindowHandle(UIApplication uiApp)
        {
            try
            {
                _revitWindowHandle = uiApp?.MainWindowHandle ?? IntPtr.Zero;
                _logger?.LogDebug($"Revit window handle set: {_revitWindowHandle}");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to get Revit window handle: {ex.Message}");
            }
        }

        /// <summary>
        /// Show a modeless guidance dialog
        /// Returns dialog ID for later updates or dismissal
        /// </summary>
        public string ShowModelessGuidance(
            RuleEvaluationResult result,
            List<ElementId> elementIds,
            string dialogId = null)
        {
            if (_disposed)
            {
                _logger?.LogWarning("Attempted to show modeless dialog on disposed manager");
                return null;
            }

            dialogId ??= Guid.NewGuid().ToString();

            try
            {
                // Check if dialog already exists
                if (_activeDialogs.ContainsKey(dialogId))
                {
                    _logger?.LogDebug($"Dialog {dialogId} already exists, updating instead");
                    UpdateDialog(dialogId, result, elementIds);
                    return dialogId;
                }

                // Create viewmodel
                var viewModel = new ModelessGuideViewModel(
                    result,
                    elementIds,
                    onClose: () => DismissDialog(dialogId));

                // Create and show window on UI thread
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    var window = new ModelessGuideWindow
                    {
                        DataContext = viewModel,
                        Title = "Guidance - BIManage",
                        Topmost = true,
                        ShowInTaskbar = false
                    };

                    // Parent to Revit window if handle available
                    if (_revitWindowHandle != IntPtr.Zero)
                    {
                        var helper = new WindowInteropHelper(window)
                        {
                            Owner = _revitWindowHandle
                        };
                    }

                    // Handle window closing
                    window.Closed += (s, e) =>
                    {
                        _activeDialogs.TryRemove(dialogId, out _);
                        _logger?.LogDebug($"Modeless dialog closed: {dialogId}");
                    };

                    // Show modeless
                    window.Show();

                    // Store reference
                    _activeDialogs.TryAdd(dialogId, window);

                    _logger?.LogInfo($"Modeless guidance shown: {dialogId} ({result.MatchedRules?.Count ?? 0} rules, {elementIds?.Count ?? 0} elements)");
                });

                return dialogId;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to show modeless guidance: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Update an existing modeless dialog with new information
        /// </summary>
        public bool UpdateDialog(string dialogId, RuleEvaluationResult result, List<ElementId> elementIds)
        {
            if (!_activeDialogs.TryGetValue(dialogId, out var window))
            {
                _logger?.LogDebug($"Dialog {dialogId} not found for update");
                return false;
            }

            try
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (window.DataContext is ModelessGuideViewModel viewModel)
                    {
                        viewModel.UpdateMessage(result.CombinedMessage);
                        viewModel.UpdateElementCount(elementIds?.Count ?? 0);
                        _logger?.LogDebug($"Modeless dialog updated: {dialogId}");
                    }
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to update modeless dialog: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Dismiss a modeless dialog
        /// </summary>
        public bool DismissDialog(string dialogId)
        {
            if (!_activeDialogs.TryRemove(dialogId, out var window))
            {
                _logger?.LogDebug($"Dialog {dialogId} not found for dismissal");
                return false;
            }

            try
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    window?.Close();
                    _logger?.LogInfo($"Modeless dialog dismissed: {dialogId}");
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to dismiss modeless dialog: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Dismiss all active modeless dialogs
        /// </summary>
        public void DismissAll()
        {
            _logger?.LogInfo($"Dismissing all modeless dialogs ({_activeDialogs.Count} active)");

            foreach (var dialogId in _activeDialogs.Keys)
            {
                DismissDialog(dialogId);
            }
        }

        /// <summary>
        /// Get count of active dialogs
        /// </summary>
        public int GetActiveDialogCount()
        {
            return _activeDialogs.Count;
        }

        /// <summary>
        /// Check if a specific dialog is active
        /// </summary>
        public bool IsDialogActive(string dialogId)
        {
            return _activeDialogs.ContainsKey(dialogId);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _logger?.LogInfo("Disposing ModelessDialogManager...");

            try
            {
                DismissAll();
                _activeDialogs.Clear();
                _disposed = true;

                _logger?.LogInfo("ModelessDialogManager disposed");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error disposing ModelessDialogManager: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Placeholder for ModelessGuideWindow
    /// Full WPF implementation would go in Views/Protection/ModelessGuideWindow.xaml
    /// </summary>
    public class ModelessGuideWindow : Window
    {
        public ModelessGuideWindow()
        {
            Width = 400;
            Height = 250;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }
}
