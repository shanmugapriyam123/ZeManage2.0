using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BIManage.Core.Metrics;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Auth;

// Resolve ambiguous types with Revit API
using WpfVisibility = System.Windows.Visibility;


namespace BIManageRevit.BIManage.Views.Metrics
{
    public partial class AnalyzeModelDialog : Window
    {
        private bool _isModal;
        private bool _isLoaded;
        private bool _showProgressPending;
        private Storyboard? _pulseAnimation;
        private Storyboard? _spinAnimation;
        private Storyboard? _middleRingAnimation;
        private DispatcherTimer? _autoCloseTimer;
        private int _autoCloseSecondsRemaining;

        public string ModelName { get; set; } = "";
        public bool UserConfirmed { get; private set; }
        public bool GenerateDetailedReport { get; private set; }
        /// <summary>
        /// Set to true when the user cancels mid-analysis — either by clicking the
        /// Cancel button on the loading screen OR by closing the dialog window (X).
        /// AnalyzeModelMetricsCommand.RunAnalysis polls this between phases (especially
        /// before the Phase 5 detailed-report block) and bails out so the HTML/PDF
        /// is not written after the user gave up. Without this flag, closing the
        /// dialog only hid the UI — the synchronous report generation kept running.
        /// </summary>
        public bool IsCancelled { get; private set; }
        private bool _startInLoadingMode;

        // Results
        public int FamiliesOver5MbCount { get; set; }
        public int PurgeableElementsCount { get; set; }
        public string CaptureId { get; set; } = "";

        public AnalyzeModelDialog()
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);
            Loaded += OnLoaded;
            PreviewKeyDown += OnPreviewKeyDown;
            // XAML defaults: LoadingSection visible, ConfirmationSection collapsed
            // Each derived constructor will set the appropriate visibility
        }

        /// <summary>
        /// Constructor that optionally starts the dialog directly in loading mode
        /// </summary>
        public AnalyzeModelDialog(string modelName, bool startInLoadingMode) : this()
        {
            ModelName = modelName;
            _startInLoadingMode = startInLoadingMode;

            if (startInLoadingMode)
            {
                // Loading mode - LoadingSection is visible by default in XAML
                // Just ensure other sections are hidden
                ConfirmationSection.Visibility = WpfVisibility.Collapsed;
                ResultsSection.Visibility = WpfVisibility.Collapsed;
                ErrorSection.Visibility = WpfVisibility.Collapsed;
                LoadingSection.Visibility = WpfVisibility.Visible;

                // Initialize loading content
                LoadingText.Text = "Checking Your Model";
                LoadingDetailText.Text = "Getting ready...";
                ProgressFill.Width = 0;
                ProgressPercentText.Text = "0%";
            }
            else
            {
                // Confirmation mode
                ConfirmationSection.Visibility = WpfVisibility.Visible;
                LoadingSection.Visibility = WpfVisibility.Collapsed;
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;

                // Handle Escape based on current visible section
                if (ConfirmationSection.Visibility == WpfVisibility.Visible)
                {
                    // On confirmation screen, Escape = No
                    NoButton_Click(sender, e);
                }
                else if (ResultsSection.Visibility == WpfVisibility.Visible ||
                         ErrorSection.Visibility == WpfVisibility.Visible ||
                         LoadingSection.Visibility == WpfVisibility.Visible)
                {
                    // On results/error/loading screen, Escape = Close
                    Close();
                }
            }
        }

        public AnalyzeModelDialog(string modelName) : this()
        {
            ModelName = modelName;
            // Show confirmation mode (not loading mode)
            ConfirmationSection.Visibility = WpfVisibility.Visible;
            LoadingSection.Visibility = WpfVisibility.Collapsed;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;

            // Treat any close that happens while we're still in the loading screen as a
            // cancellation — the synchronous analysis loop in AnalyzeModelMetricsCommand
            // polls IsCancelled between phases and bails before the detailed-report block.
            Closing += (s, ce) =>
            {
                if (LoadingSection.Visibility == WpfVisibility.Visible)
                    IsCancelled = true;
            };

            if (!string.IsNullOrEmpty(ModelName))
            {
                ModelNameText.Text = ModelName;
            }
            InitializeAnimations();

            // Force layout update to ensure UI is ready
            UpdateLayout();

            // If started in loading mode, show progress immediately
            if (_startInLoadingMode)
            {
                ShowProgressInternal();
                // Force UI to render
                Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            }
            // If ShowProgress was called before window was loaded, show it now
            else if (_showProgressPending)
            {
                _showProgressPending = false;
                ShowProgressInternal();
            }
        }

        private void InitializeAnimations()
        {
            // Create pulse animation for center dot
            _pulseAnimation = new Storyboard();
            var pulseAnim = new DoubleAnimation
            {
                From = 1.0,
                To = 0.4,
                Duration = TimeSpan.FromSeconds(0.5),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(pulseAnim, LoadingLogo);
            Storyboard.SetTargetProperty(pulseAnim, new PropertyPath(OpacityProperty));
            _pulseAnimation.Children.Add(pulseAnim);

            // Create spin animation for the spinner
            _spinAnimation = new Storyboard();
            var spinAnim = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(1.2),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(spinAnim, LoadingSpinner);
            Storyboard.SetTargetProperty(spinAnim, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
            _spinAnimation.Children.Add(spinAnim);

            // Middle ring animation (not visible but needed for compatibility)
            _middleRingAnimation = new Storyboard();
        }

        public new bool? ShowDialog()
        {
            _isModal = true;
            return base.ShowDialog();
        }

        public new void Show()
        {
            _isModal = false;
            base.Show();
        }

        #region Window Events

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
            {
                DragMove();
            }
        }

        private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
        {
            UserConfirmed = false;
            if (_isModal)
            {
                DialogResult = false;
            }
            Close();
        }

        #endregion

        #region Button Click Handlers

        private void YesButton_Click(object sender, RoutedEventArgs e)
        {
            UserConfirmed = true;
            GenerateDetailedReport = DetailedReportCheck.IsChecked == true;
            if (_isModal)
            {
                DialogResult = true;
            }
            Close();
        }

        private void NoButton_Click(object sender, RoutedEventArgs e)
        {
            UserConfirmed = false;
            if (_isModal)
            {
                DialogResult = false;
            }
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            UserConfirmed = true;
            if (_isModal)
            {
                DialogResult = true;
            }
            Close();
        }

        private void ErrorCancelButton_Click(object sender, RoutedEventArgs e)
        {
            UserConfirmed = false;
            if (_isModal)
            {
                DialogResult = false;
            }
            Close();
        }

        /// <summary>
        /// Handles the persistent Cancel button on the loading screen used by
        /// AnalyzeModelMetricsCommand's synchronous flow. The synchronous analysis
        /// polls IsCancelled between phases and inside the heavy collector loops,
        /// so we just set the flag, disable the button, and update the status text.
        /// Closing the window here would race with the synchronous loop still on the
        /// UI thread — let the command's phase boundary close the dialog properly.
        /// </summary>
        private void LoadingCancelButton_Click(object sender, RoutedEventArgs e)
        {
            IsCancelled = true;
            LoadingCancelButton.IsEnabled = false;
            LoadingDetailText.Text = "Cancelling...";

            // If a ChunkedMetricsAnalyzer is wired up (the Idling-driven flow), forward
            // the cancellation to it too so its tick loop bails out next chunk.
            _analyzer?.Cancel();
        }

        /// <summary>
        /// Hide the persistent Cancel button. Called by the ChunkedMetricsAnalyzer flow
        /// which manages its own contextual buttons inside FamilyScanButtonsContainer.
        /// </summary>
        public void HideLoadingCancelButton()
        {
            Dispatcher.Invoke(() =>
            {
                if (LoadingCancelButton != null)
                    LoadingCancelButton.Visibility = WpfVisibility.Collapsed;
            });
        }

        /// <summary>
        /// Wire the chunked analyzer to the dialog up front so the persistent Cancel
        /// button can stop the analyzer even before its first family-scan-specific UI
        /// transition (which sets _analyzer internally). Called by
        /// ChunkedMetricsAnalyzer.Start() so cancellation in the early phases (fast /
        /// medium / purgeable / save) routes through analyzer.Cancel() correctly.
        /// </summary>
        public void AttachAnalyzer(ChunkedMetricsAnalyzer analyzer)
        {
            _analyzer = analyzer;
        }

        private void ModelHealthLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Stop auto-close timer since user is taking action
                _autoCloseTimer?.Stop();
                _autoCloseTimer = null;

                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.Services;
                var logger = app?.Logger;

                if (services == null) return;

                var metricsRepository = services.GetService<ModelFileMetricsRepository>();
                var httpClient = services.GetService<AuthenticatedHttpClient>();
                var metricsSyncService = services.GetService<MetricsSyncService>();

                if (metricsRepository == null) return;

                // Get model context from the current Revit document
                string? modelGuid = null;
                string modelName = "All Models";

                var uiAppProvider = services.GetService<global::BIManage.Revit.Context.IUIApplicationProvider>();
                var uiApp = uiAppProvider?.UIApplication;
                var doc = uiApp?.ActiveUIDocument?.Document;
                string? revitProjectName = null;
                bool isRegistered = true;
                if (doc != null && !doc.IsFamilyDocument)
                {
                    modelGuid = global::BIManage.Revit.Helpers.ModelGuidHelper.GetModelGuid(doc);
                    modelName = doc.Title;
                    try { revitProjectName = doc.ProjectInformation?.Name; }
                    catch { /* ProjectInformation may be null */ }

                    try
                    {
                        var modelRepo = services.GetService<RegisteredModelsRepository>();
                        if (modelRepo != null && !string.IsNullOrEmpty(modelGuid))
                        {
                            var regModel = System.Threading.Tasks.Task.Run(() => modelRepo.GetModelAsync(modelGuid)).GetAwaiter().GetResult();
                            if (regModel == null)
                                isRegistered = false;
                            else if (!string.IsNullOrEmpty(regModel.ProjectName))
                                modelName = regModel.ProjectName + " | " + (regModel.ModelName ?? doc.Title);
                        }
                    }
                    catch { /* Use doc.Title as fallback */ }
                }

                Close();

                var dashboard = new ModelHealthDashboard(metricsRepository, logger, modelGuid, modelName, httpClient, metricsSyncService, revitProjectName, isRegistered);
                var mainWindowHandle = uiApp?.MainWindowHandle ?? System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                new System.Windows.Interop.WindowInteropHelper(dashboard) { Owner = mainWindowHandle };
                dashboard.ShowDialog();
            }
            catch (Exception ex)
            {
                var appLogger = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance?.Logger;
                appLogger?.LogWarning($"Failed to open Model Health from deep analysis: {ex.Message}");
            }
        }

        #endregion

        #region Public Methods for External Control

        /// <summary>
        /// Shows the loading section with BIManage logo animation
        /// </summary>
        public void ShowProgress()
        {
            if (!_isLoaded)
            {
                // Window not loaded yet, defer showing progress
                _showProgressPending = true;
                return;
            }

            Dispatcher.Invoke(() => ShowProgressInternal());
        }

        private void ShowProgressInternal()
        {
            // Initialize animations if not already done
            if (_pulseAnimation == null || _spinAnimation == null || _middleRingAnimation == null)
            {
                InitializeAnimations();
            }

            // Hide other sections
            ConfirmationSection.Visibility = WpfVisibility.Collapsed;
            ResultsSection.Visibility = WpfVisibility.Collapsed;
            ErrorSection.Visibility = WpfVisibility.Collapsed;

            // Show loading section
            LoadingSection.Visibility = WpfVisibility.Visible;

            // Reset progress
            ProgressFill.Width = 0;
            ProgressPercentText.Text = "0%";
            LoadingText.Text = "Checking Your Model";
            LoadingDetailText.Text = "Getting ready...";

            // Start all animations
            _pulseAnimation?.Begin();
            _spinAnimation?.Begin();
            _middleRingAnimation?.Begin();
        }

        /// <summary>
        /// Updates the progress display with smooth animation
        /// </summary>
        public void UpdateProgress(string message, int percentage)
        {
            Dispatcher.Invoke(() =>
            {
                // Animate progress bar width smoothly (track is 320px wide)
                double targetWidth = (percentage / 100.0) * 320;
                var widthAnim = new DoubleAnimation
                {
                    To = targetWidth,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                ProgressFill.BeginAnimation(WidthProperty, widthAnim);

                ProgressPercentText.Text = $"{percentage}%";
                LoadingDetailText.Text = message;
            });
        }

        /// <summary>
        /// Shows the results section with analysis data
        /// </summary>
        public void ShowResults(int familiesOver5Mb, int purgeableElements, string captureId)
        {
            // Guard against the late "UI flash" — the synchronous analyzer can take
            // minutes after the user cancels before this method is reached. If they
            // cancelled, just close the dialog instead of briefly re-rendering the
            // results section on a window that's about to disappear.
            if (IsCancelled)
            {
                Dispatcher.Invoke(() => { try { Close(); } catch { } });
                return;
            }

            Dispatcher.Invoke(() =>
            {
                // Stop animations
                _pulseAnimation?.Stop();
                _spinAnimation?.Stop();
                _middleRingAnimation?.Stop();

                // Store results
                FamiliesOver5MbCount = familiesOver5Mb;
                PurgeableElementsCount = purgeableElements;
                CaptureId = captureId;

                // Populate results section
                ResultModelName.Text = ModelName;
                ResultCapturedTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                ResultCaptureId.Text = captureId;
                ResultFamiliesOver5Mb.Text = familiesOver5Mb.ToString();
                ResultPurgeableElements.Text = purgeableElements.ToString();

                // Hide other sections
                ConfirmationSection.Visibility = WpfVisibility.Collapsed;
                LoadingSection.Visibility = WpfVisibility.Collapsed;
                ErrorSection.Visibility = WpfVisibility.Collapsed;

                // Show results section
                ResultsSection.Visibility = WpfVisibility.Visible;

                // Start auto-close timer (30 seconds)
                StartAutoCloseTimer(30);
            });
        }

        /// <summary>
        /// Shows results for all 3 metric tiers (fast + medium + expensive).
        /// Called by ChunkedMetricsAnalyzer after all tiers are collected.
        /// </summary>
        public void ShowAllResults(
            string modelName,
            FastMetrics? fast,
            MediumMetrics? medium,
            ExpensiveMetrics? expensive,
            string captureId)
        {
            Dispatcher.Invoke(() =>
            {
                _pulseAnimation?.Stop();
                _spinAnimation?.Stop();
                _middleRingAnimation?.Stop();

                ModelName = modelName;
                CaptureId = captureId;
                FamiliesOver5MbCount = expensive?.FamiliesOver5MbCount ?? 0;
                PurgeableElementsCount = expensive?.PurgeableElementsCount ?? 0;

                // Populate hidden fields for backward compat
                ResultModelName.Text = modelName;
                ResultCapturedTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                ResultCaptureId.Text = captureId;
                ResultFamiliesOver5Mb.Text = FamiliesOver5MbCount.ToString();
                ResultPurgeableElements.Text = PurgeableElementsCount.ToString();

                // Build summary text for the results section
                var summary = new System.Text.StringBuilder();

                if (fast != null)
                {
                    summary.AppendLine($"File Size: {FormatBytes(fast.FileSizeBytes)}");
                    summary.AppendLine($"Warnings: {fast.WarningsCount ?? 0}  |  Levels: {fast.LevelsCount ?? 0}  |  Grids: {fast.GridsCount ?? 0}");
                    summary.AppendLine($"Views: {fast.TotalViewsCount ?? 0}  |  Sheets: {fast.SheetsCount ?? 0}  |  Families: {fast.TotalFamiliesCount ?? 0}");
                    summary.AppendLine($"Linked DWG: {fast.LinkedDwgCount ?? 0}  |  Linked Revit: {fast.LinkedRevitCount ?? 0}  |  Imported DWG: {fast.ImportedDwgCount ?? 0}");
                }

                if (medium != null)
                {
                    summary.AppendLine($"Total Elements: {medium.TotalElementsCount ?? 0}  (Model: {medium.ModelElementsCount ?? 0}  |  Annotative: {medium.AnnotativeElementsCount ?? 0})");
                    summary.AppendLine($"In-place Families: {medium.InplaceFamiliesCount ?? 0}  |  Unplaced Rooms: {medium.UnplacedRoomsCount ?? 0}  |  Views Not on Sheets: {medium.ViewsNotOnSheetsCount ?? 0}");
                }

                if (expensive != null)
                {
                    summary.AppendLine($"Oversized Families (>5 MB): {expensive.FamiliesOver5MbCount ?? 0}  |  Purgeable Elements: {expensive.PurgeableElementsCount ?? 0}");
                }

                // Hide other sections, show results
                ConfirmationSection.Visibility = WpfVisibility.Collapsed;
                LoadingSection.Visibility = WpfVisibility.Collapsed;
                ErrorSection.Visibility = WpfVisibility.Collapsed;
                ResultsSection.Visibility = WpfVisibility.Visible;

                // Resize for summary content
                Width = 440;
                SizeToContent = SizeToContent.Height;

                // Replace the simple "Analysis Complete" with detailed summary
                ResultsSection.Children.Clear();

                var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(24, 16, 24, 16) };

                // Success icon
                var iconBorder = new System.Windows.Controls.Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 252, 231)),
                    CornerRadius = new CornerRadius(25),
                    Width = 50, Height = 50,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                var iconText = new System.Windows.Controls.TextBlock
                {
                    Text = "\u2713", FontSize = 24, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 163, 74)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                iconBorder.Child = iconText;
                stack.Children.Add(iconBorder);

                // Title
                stack.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "Analysis Complete",
                    FontSize = 14, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 12, 0, 4)
                });

                stack.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = modelName,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 12)
                });

                // Summary card
                var card = new System.Windows.Controls.Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14, 10, 14, 10),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
                    BorderThickness = new Thickness(1)
                };
                var summaryBlock = new System.Windows.Controls.TextBlock
                {
                    Text = summary.ToString().TrimEnd(),
                    FontSize = 10,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18
                };
                card.Child = summaryBlock;
                stack.Children.Add(card);

                ResultsSection.Children.Add(stack);

                StartAutoCloseTimer(60);
            });
        }

        private static string FormatBytes(long? bytes)
        {
            if (bytes == null) return "N/A";
            double b = bytes.Value;
            if (b < 1024) return $"{b:F0} B";
            b /= 1024;
            if (b < 1024) return $"{b:F1} KB";
            b /= 1024;
            if (b < 1024) return $"{b:F1} MB";
            b /= 1024;
            return $"{b:F2} GB";
        }

        private void StartAutoCloseTimer(int seconds)
        {
            _autoCloseSecondsRemaining = seconds;
            _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _autoCloseTimer.Tick += (s, e) =>
            {
                _autoCloseSecondsRemaining--;
                if (_autoCloseSecondsRemaining <= 0)
                {
                    _autoCloseTimer?.Stop();
                    _autoCloseTimer = null;
                    Close();
                }
            };
            _autoCloseTimer.Start();
        }

        /// <summary>
        /// Shows the error section with the error message
        /// </summary>
        public void ShowError(string errorMessage)
        {
            Dispatcher.Invoke(() =>
            {
                // Stop animations
                _pulseAnimation?.Stop();
                _spinAnimation?.Stop();
                _middleRingAnimation?.Stop();

                // Set error message
                ErrorMessageText.Text = errorMessage;

                // Hide other sections
                ConfirmationSection.Visibility = WpfVisibility.Collapsed;
                LoadingSection.Visibility = WpfVisibility.Collapsed;
                ResultsSection.Visibility = WpfVisibility.Collapsed;

                // Show error section
                ErrorSection.Visibility = WpfVisibility.Visible;
            });
        }

        #endregion

        #region Family Size Scan Confirmation & Progress

        private ChunkedMetricsAnalyzer? _analyzer;

        /// <summary>
        /// Shows an inline confirmation prompt asking the user whether to proceed with
        /// the family size scan. Called by ChunkedMetricsAnalyzer after initial metrics are saved.
        /// </summary>
        public void ShowFamilyScanConfirmation(int familyCount, ChunkedMetricsAnalyzer analyzer)
        {
            _analyzer = analyzer;

            Dispatcher.Invoke(() =>
            {
                _pulseAnimation?.Stop();
                _spinAnimation?.Stop();

                // Hide other sections
                ConfirmationSection.Visibility = WpfVisibility.Collapsed;
                ResultsSection.Visibility = WpfVisibility.Collapsed;
                ErrorSection.Visibility = WpfVisibility.Collapsed;
                LoadingSection.Visibility = WpfVisibility.Visible;

                // Chunked analyzer uses its own contextual buttons; hide the persistent one.
                if (LoadingCancelButton != null)
                    LoadingCancelButton.Visibility = WpfVisibility.Collapsed;

                // Repurpose loading section as a confirmation prompt
                LoadingText.Text = "Family Size Scan";
                LoadingDetailText.Text = $"{familyCount} families found. This may take a while.\nProceed with family size analysis?";
                ProgressPercentText.Text = "";

                // Reset progress bar
                ProgressFill.Width = 0;

                // Add Yes/No buttons dynamically below the loading section
                var buttonPanel = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 12, 0, 0),
                    Name = "FamilyScanButtonPanel"
                };

                var yesBtn = new System.Windows.Controls.Button
                {
                    Content = "Yes, Scan",
                    Width = 100, Height = 30,
                    Margin = new Thickness(0, 0, 8, 0),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 38, 38)),
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    Cursor = Cursors.Hand
                };
                yesBtn.Click += (s, e) =>
                {
                    RemoveFamilyScanButtons();
                    _analyzer?.ResumeFamilyScan(true);
                };

                var noBtn = new System.Windows.Controls.Button
                {
                    Content = "Skip",
                    Width = 100, Height = 30,
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 245, 249)),
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                    FontWeight = FontWeights.SemiBold,
                    Cursor = Cursors.Hand
                };
                noBtn.Click += (s, e) =>
                {
                    RemoveFamilyScanButtons();
                    _analyzer?.ResumeFamilyScan(false);
                };

                buttonPanel.Children.Add(yesBtn);
                buttonPanel.Children.Add(noBtn);
                buttonPanel.Tag = "FamilyScanButtons";

                // Place inside the StackPanel-based host so the buttons appear BELOW the progress
                // bar instead of overlapping it (the LoadingSection itself is a Grid).
                FamilyScanButtonsContainer.Children.Clear();
                FamilyScanButtonsContainer.Children.Add(buttonPanel);
            });
        }

        /// <summary>
        /// Shows the family scan progress with a Cancel button.
        /// </summary>
        public void ShowFamilyScanProgress(int totalFamilies, ChunkedMetricsAnalyzer analyzer)
        {
            _analyzer = analyzer;

            Dispatcher.Invoke(() =>
            {
                RemoveFamilyScanButtons();

                // Chunked analyzer manages its own Cancel inside FamilyScanButtonsContainer.
                if (LoadingCancelButton != null)
                    LoadingCancelButton.Visibility = WpfVisibility.Collapsed;

                LoadingText.Text = "Scanning Family Sizes";
                LoadingDetailText.Text = $"Analyzing 0/{totalFamilies} families...";
                ProgressPercentText.Text = "0%";
                ProgressFill.Width = 0;

                _pulseAnimation?.Begin();
                _spinAnimation?.Begin();

                // Add Cancel button (gets parented to FamilyScanButtonsContainer below the 0%
                // text — its outer container already provides the top margin, so don't double up).
                var cancelBtn = new System.Windows.Controls.Button
                {
                    Content = "Cancel",
                    Width = 100, Height = 30,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 245, 249)),
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                    FontWeight = FontWeights.SemiBold,
                    Cursor = Cursors.Hand
                };
                cancelBtn.Tag = "FamilyScanButtons";
                cancelBtn.Click += (s, e) =>
                {
                    // Set the flag FIRST so any phase polling sees the cancel even
                    // before the analyzer's own cancellation propagates. This is
                    // what stops the detailed-report block from running when the
                    // user cancels mid-analysis with "Generate detailed report" ticked.
                    IsCancelled = true;
                    _analyzer?.Cancel();
                    RemoveFamilyScanButtons();
                    LoadingDetailText.Text = "Cancelling...";
                };

                FamilyScanButtonsContainer.Children.Clear();
                FamilyScanButtonsContainer.Children.Add(cancelBtn);
            });
        }

        /// <summary>
        /// Updates the family scan progress display.
        /// </summary>
        public void UpdateFamilyScanProgress(int current, int total, int percentage)
        {
            Dispatcher.Invoke(() =>
            {
                LoadingDetailText.Text = $"Analyzing {current}/{total} families...";
                ProgressPercentText.Text = $"{percentage}%";

                double targetWidth = (percentage / 100.0) * 320;
                var widthAnim = new DoubleAnimation
                {
                    To = targetWidth,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                ProgressFill.BeginAnimation(WidthProperty, widthAnim);
            });
        }

        private void RemoveFamilyScanButtons()
        {
            // Clear the dedicated buttons container.
            if (FamilyScanButtonsContainer != null)
                FamilyScanButtonsContainer.Children.Clear();

            // Defensive: also clean up any legacy buttons that may still be parented to the
            // LoadingSection grid from earlier code paths.
            if (LoadingSection is System.Windows.Controls.Panel panel)
            {
                for (int i = panel.Children.Count - 1; i >= 0; i--)
                {
                    if (panel.Children[i] is FrameworkElement fe && fe.Tag as string == "FamilyScanButtons")
                        panel.Children.RemoveAt(i);
                }
            }
        }

        #endregion

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }
    }
}
