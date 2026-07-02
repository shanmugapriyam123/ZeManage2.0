using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using BIManageRevit.Commands.RibbonCommands;

using WpfVisibility = System.Windows.Visibility;


namespace BIManageRevit.BIManage.Views.Bindings
{
    public partial class PinProtectionStatusDialog : Window
    {
        private List<ProtectedElementDetails> _protectedElements;
        private bool _detailsExpanded = false;

        // Optional re-fetch delegate. When provided, the dialog refreshes its list every time
        // the window is activated (e.g. after the user unpins an element and switches back).
        private readonly Func<Task<List<ProtectedElementDetails>>>? _reloader;

        public PinProtectionStatusDialog(List<ProtectedElementDetails> protectedElements)
            : this(protectedElements, null)
        {
        }

        public PinProtectionStatusDialog(
            List<ProtectedElementDetails> protectedElements,
            Func<Task<List<ProtectedElementDetails>>>? reloader)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);
            _protectedElements = protectedElements ?? new List<ProtectedElementDetails>();
            _reloader = reloader;

            LoadData();

            // Auto-refresh when the user re-focuses the dialog after performing an unpin.
            if (_reloader != null)
                Activated += OnDialogActivated;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private async void OnDialogActivated(object? sender, EventArgs e)
        {
            // The unpin path writes to SQLite via async-void (fire-and-forget) — the DB
            // row's is_active flag may not yet be committed when this Activated event
            // fires, so we wait briefly to let the write settle before re-querying.
            // Otherwise the reloader returns the stale active-row count and the dialog
            // keeps showing the pre-unpin number.
            await Task.Delay(350);
            await ReloadFromSourceAsync();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            // Manual fallback when the auto-refresh on Activated misses an unpin (e.g.
            // user unpinned without ever moving focus away from the dialog).
            BtnRefresh.IsEnabled = false;
            try { await ReloadFromSourceAsync(); }
            finally { BtnRefresh.IsEnabled = true; }
        }

        private async Task ReloadFromSourceAsync()
        {
            if (_reloader == null) return;
            try
            {
                var fresh = await _reloader();
                if (fresh == null) return;

                // Always re-render — the count text is cheap to update and the previous
                // key-comparison short-circuit hid stale-count cases when the underlying
                // DB write was momentarily behind the UI snapshot.
                _protectedElements = fresh;
                LoadData();
            }
            catch
            {
                // Silently ignore refresh errors — keep the existing snapshot displayed.
            }
        }

        private void LoadData()
        {
            // Set protected count
            TxtProtectedCount.Text = _protectedElements.Count.ToString();

            // Hide View Details if no elements
            if (_protectedElements.Count == 0)
            {
                BtnViewDetails.Visibility = WpfVisibility.Collapsed;
            }

            // Prepare category grouped data
            var groupedData = _protectedElements
                .GroupBy(e => e.ElementCategory ?? "Unknown")
                .OrderBy(g => g.Key)
                .Select(g => new CategoryGroup
                {
                    CategoryName = g.Key,
                    ElementCount = g.Count(),
                    Elements = g.OrderBy(e => e.ElementName).Select(e => new ElementDisplayItem
                    {
                        ElementId = e.ElementId.ToString(),
                        ElementName = e.ElementName ?? "N/A",
                        ProtectedBy = e.ProtectedBy,
                        ProtectionMode = GetProtectionModeName(e.ProtectionMode)
                    }).ToList()
                }).ToList();

            CategoryList.ItemsSource = groupedData;
        }

        private string GetProtectionModeName(int mode)
        {
            return mode switch
            {
                0 => "notify",
                1 => "assist",
                2 => "protect",
                _ => "Unknown"
            };
        }

        private void ViewDetails_Click(object sender, RoutedEventArgs e)
        {
            _detailsExpanded = !_detailsExpanded;

            if (_detailsExpanded)
            {
                TxtArrow.Text = "↓"; // Down arrow
                TxtViewDetailsTitle.Text = "Hide Details";

                if (_protectedElements.Count > 0)
                {
                    DetailsPanel.Visibility = WpfVisibility.Visible;
                    EmptyDetailsPanel.Visibility = WpfVisibility.Collapsed;
                }
                else
                {
                    DetailsPanel.Visibility = WpfVisibility.Collapsed;
                    EmptyDetailsPanel.Visibility = WpfVisibility.Visible;
                }
            }
            else
            {
                TxtArrow.Text = "→"; // Right arrow
                TxtViewDetailsTitle.Text = "View Details";
                DetailsPanel.Visibility = WpfVisibility.Collapsed;
                EmptyDetailsPanel.Visibility = WpfVisibility.Collapsed;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        }
    }

    public class CategoryGroup
    {
        public string CategoryName { get; set; } = string.Empty;
        public int ElementCount { get; set; }
        public List<ElementDisplayItem> Elements { get; set; } = new List<ElementDisplayItem>();
    }

    public class ElementDisplayItem
    {
        public string ElementId { get; set; } = string.Empty;
        public string ElementName { get; set; } = string.Empty;
        public string ProtectedBy { get; set; } = string.Empty;
        public string ProtectionMode { get; set; } = string.Empty;
    }
}
