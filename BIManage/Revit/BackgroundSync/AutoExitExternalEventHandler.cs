using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Helpers;

namespace BIManage.Revit.BackgroundSync
{
    /// <summary>
    /// ExternalEvent handler for auto-exit on idle.
    /// Runs on Revit's main thread:
    ///   1. Shows a 60-second countdown dialog (user can cancel)
    ///   2. If not cancelled: syncs all modified workshared documents
    ///   3. Calls CloseMainWindow for a clean shutdown
    /// Exit confirmation flow: countdown → PostMessage(WM_CLOSE).
    /// </summary>
    public class AutoExitExternalEventHandler : IExternalEventHandler
    {
        private readonly ILogger? _logger;

        /// <summary>Fired if user cancels the countdown — engine resets _exitRequested.</summary>
        public event Action? ExitCancelled;

        private const int CountdownSeconds = 60;

        public AutoExitExternalEventHandler(ILogger? logger)
        {
            _logger = logger;
        }

        public void Execute(UIApplication app)
        {
            _logger?.LogInfo("[AutoExit] Idle threshold reached — showing exit countdown dialog");

            // Show countdown dialog on the main thread (safe inside ExternalEvent.Execute)
            bool cancelled = ShowCountdownDialog(app.MainWindowHandle);

            if (cancelled)
            {
                _logger?.LogInfo("[AutoExit] User cancelled auto-exit");
                ExitCancelled?.Invoke();
                RevitInteropHelper.ClearStatusText(app.MainWindowHandle);
                return;
            }

            // User did not cancel — sync all open modified documents before closing
            _logger?.LogInfo("[AutoExit] Countdown complete — syncing open documents before exit");

            var failuresHandler = new BackgroundFailuresHandler(_logger);

            try
            {
                foreach (Document doc in app.Application.Documents)
                {
                    if (doc.IsLinked) continue; // Linked docs are never saved independently

                    // For WORKSHARED docs we must attempt SynchronizeWithCentral even when
                    // doc.IsModified is false. After a plain Ctrl+S (local save) the in-memory
                    // dirty flag clears (IsModified=false) but the changes only landed in the
                    // user's local file — central still doesn't have them. The previous
                    // unconditional "if (!doc.IsModified) continue;" therefore skipped the
                    // sync entirely and Revit closed without ever pushing the user's work to
                    // central — exact symptom reported as "Auto Close Revit – Automatic
                    // Sync Not Triggered Before Closing". For non-workshared documents
                    // IsModified is sufficient: nothing to push beyond a local save, so a
                    // clean doc is genuinely a no-op.
                    var isWorksharedDoc = DocumentTypeHelper.IsWorkshared(doc) && !doc.IsDetached;
                    if (!isWorksharedDoc && !doc.IsModified) continue;

                    try
                    {
                        var hasPath = !string.IsNullOrEmpty(doc.PathName);

                        if (!hasPath)
                        {
                            // New/unsaved document — SaveAs to default auto-save location
                            var autoSavePath = BuildAutoSavePath(doc);
                            if (autoSavePath != null)
                            {
                                _logger?.LogInfo($"[AutoExit] SaveAs new document '{doc.Title}' → {autoSavePath}");
                                RevitInteropHelper.SetStatusText(app.MainWindowHandle, $"ZeManage: Saving new file {doc.Title}...");
                                doc.SaveAs(autoSavePath, new SaveAsOptions { OverwriteExistingFile = true });
                                _logger?.LogInfo($"[AutoExit] SaveAs complete: {autoSavePath}");
                            }
                            else
                            {
                                _logger?.LogWarning($"[AutoExit] Could not determine auto-save path for '{doc.Title}' — skipping");
                            }
                        }
                        else if (!doc.IsReadOnly)
                        {
                            if (DocumentTypeHelper.IsWorkshared(doc) && !doc.IsDetached)
                            {
                                // Workshared: sync to central
                                _logger?.LogInfo($"[AutoExit] Syncing {doc.Title} before exit...");
                                RevitInteropHelper.SetStatusText(app.MainWindowHandle, $"ZeManage: Syncing {doc.Title}...");

                                failuresHandler.Attach(app.Application);
                                try
                                {
                                    var syncOpts = new SynchronizeWithCentralOptions();
                                    // RELINQUISH EVERYTHING the current user owns. If CheckedOutElements
                                    // is left false, Revit's close handler sees the user still owns
                                    // elements and shows the native "Editable Elements" dialog, blocking
                                    // the auto-exit. For unattended auto-close we want a fully clean
                                    // state — sync + give up everything — so CloseMainWindow proceeds
                                    // without prompting. FamilyWorksets stays false because the family
                                    // workset concept doesn't apply to most workshared models, and
                                    // forcing relinquish on it can fail.
                                    syncOpts.SetRelinquishOptions(new RelinquishOptions(true)
                                    {
                                        FamilyWorksets = false,
                                        CheckedOutElements = true
                                    });
                                    syncOpts.Comment = "ZeManage auto-exit sync";
                                    // SyncProtectionGate marks this as an automated sync so the
                                    // Command Protection enforcer skips it (shutdown is not the
                                    // place to surface a Notify/Assist/Protect dialog).
                                    using (BIManage.Revit.SyncTrafficControl.SyncProtectionGate.EnterInternalSync())
                                    {
                                        doc.SynchronizeWithCentral(new TransactWithCentralOptions(), syncOpts);
                                    }
                                    _logger?.LogInfo($"[AutoExit] Sync completed for {doc.Title}");
                                }
                                finally
                                {
                                    failuresHandler.Detach(app.Application);
                                }

                                // Fallback local save if document is still marked modified after sync
                                if (doc.IsModified)
                                {
                                    _logger?.LogInfo($"[AutoExit] Local save fallback after sync for {doc.Title}");
                                    doc.Save();
                                }
                            }
                            else
                            {
                                // Non-workshared, detached, or family document — local save
                                _logger?.LogInfo($"[AutoExit] Saving {doc.Title} before exit...");
                                RevitInteropHelper.SetStatusText(app.MainWindowHandle, $"ZeManage: Saving {doc.Title}...");
                                doc.Save();
                                _logger?.LogInfo($"[AutoExit] Save completed for {doc.Title}");
                            }
                        }

                        RevitInteropHelper.SetStatusText(app.MainWindowHandle, $"ZeManage: {doc.Title} saved.");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"[AutoExit] Save/sync failed for {doc.Title}: {ex.Message}", ex);
                        // Continue — attempt to close even if one document fails
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[AutoExit] Error during pre-exit save: {ex.Message}", ex);
            }

            _logger?.LogInfo("[AutoExit] Pre-exit sync complete — closing Revit");
            try
            {
                Process.GetCurrentProcess().CloseMainWindow();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[AutoExit] Failed to close Revit: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Builds a safe auto-save path for a new/unsaved document.
        /// %LocalAppData%\BIManageRevit\AutoSave\{title}_{timestamp}.rvt/.rfa
        /// </summary>
        private static string? BuildAutoSavePath(Document doc)
        {
            try
            {
                var autoSaveDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BIManageRevit", "AutoSave");
                Directory.CreateDirectory(autoSaveDir);

                var ext = doc.IsFamilyDocument ? ".rfa" : ".rvt";
                var safeName = string.Concat((doc.Title ?? "Untitled").Split(Path.GetInvalidFileNameChars()));
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                return Path.Combine(autoSaveDir, $"{safeName}_{timestamp}{ext}");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Shows a modal countdown dialog. Returns true if the user clicked Cancel.
        /// Blocks for up to CountdownSeconds seconds if no interaction.
        /// </summary>
        private bool ShowCountdownDialog(IntPtr ownerHandle)
        {
            bool cancelled = false;

            try
            {
                var dialog = new AutoExitCountdownDialog(CountdownSeconds);

                if (ownerHandle != IntPtr.Zero)
                    new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = ownerHandle };

                dialog.ShowDialog();
                cancelled = dialog.WasCancelled;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[AutoExit] Countdown dialog failed ({ex.Message}) — proceeding with exit");
                // If dialog fails to show, proceed with exit
            }

            return cancelled;
        }

        public string GetName() => "ZeManage Auto Exit";
    }

    /// <summary>
    /// 60-second countdown dialog: "Revit will close in X seconds."
    /// User can click Cancel to abort, or Close Now to proceed immediately.
    /// </summary>
    internal class AutoExitCountdownDialog : Window
    {
        private readonly int _totalSeconds;
        private int _remaining;
        private readonly DispatcherTimer _timer;
        private readonly TextBlock _countdownText;
        private readonly TextBlock _secondsText;
        private readonly System.Windows.Shapes.Rectangle _progressBar;
        private readonly double _progressMaxWidth = 280;

        public bool WasCancelled { get; private set; } = false;

        public AutoExitCountdownDialog(int seconds)
        {
            _totalSeconds = seconds;
            _remaining = seconds;

            Title = "ZeManage";
            Width = 400;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
            Topmost = false;

            // ESC to cancel
            PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) { WasCancelled = true; _timer.Stop(); Close(); } };

            // Main border with shadow
            var mainBorder = new Border
            {
                Background = System.Windows.Media.Brushes.White,
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(16),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = System.Windows.Media.Colors.Black,
                    BlurRadius = 20,
                    ShadowDepth = 4,
                    Opacity = 0.15
                }
            };

            var outerStack = new StackPanel();

            // Header bar
            var headerBorder = new Border
            {
                CornerRadius = new CornerRadius(12, 12, 0, 0),
                Padding = new Thickness(20, 14, 20, 14),
                Background = new System.Windows.Media.LinearGradientBrush(
                    System.Windows.Media.Color.FromRgb(230, 234, 240),
                    System.Windows.Media.Color.FromRgb(230, 234, 240),
                    0)
            };
            var headerStack = new StackPanel { Orientation = Orientation.Horizontal };
            headerStack.Children.Add(new TextBlock
            {
                Text = "\u23F1",
                FontSize = 18,
                Foreground = System.Windows.Media.Brushes.Black,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            var headerTextStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            headerTextStack.Children.Add(new TextBlock
            {
                Text = "Revit Will Close Soon",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.Black
            });
            headerTextStack.Children.Add(new TextBlock
            {
                Text = "No activity detected",
                FontSize = 10,
                Foreground = System.Windows.Media.Brushes.Black
            });
            headerStack.Children.Add(headerTextStack);
            headerBorder.Child = headerStack;
            outerStack.Children.Add(headerBorder);

            // Content
            var contentStack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

            // Countdown display
            var countdownPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
            _secondsText = new TextBlock
            {
                Text = _remaining.ToString(),
                FontSize = 36,
                FontWeight = FontWeights.Bold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(252, 66, 79)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            countdownPanel.Children.Add(_secondsText);
            countdownPanel.Children.Add(new TextBlock
            {
                Text = "seconds remaining",
                FontSize = 10,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            contentStack.Children.Add(countdownPanel);

            // Progress bar
            var progressBg = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 245, 249)),
                CornerRadius = new CornerRadius(4),
                Height = 6,
                Margin = new Thickness(0, 0, 0, 14),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var progressGrid = new System.Windows.Controls.Grid();
            _progressBar = new System.Windows.Shapes.Rectangle
            {
                Fill = new System.Windows.Media.LinearGradientBrush(
                    System.Windows.Media.Color.FromRgb(252, 66, 79),
                    System.Windows.Media.Color.FromRgb(239, 68, 68), 0),
                RadiusX = 4,
                RadiusY = 4,
                Width = _progressMaxWidth,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            progressGrid.Children.Add(_progressBar);
            progressBg.Child = progressGrid;
            contentStack.Children.Add(progressBg);

            // Info text
            _countdownText = new TextBlock
            {
                Text = "Your work will be saved and synced before closing.",
                FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 18)
            };
            contentStack.Children.Add(_countdownText);

            // Buttons
            var buttonGrid = new System.Windows.Controls.Grid();
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 90,
                Height = 36,
                Background = System.Windows.Media.Brushes.White,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 12
            };
            cancelBtn.Click += (s, e) => { WasCancelled = true; _timer.Stop(); Close(); };
            System.Windows.Controls.Grid.SetColumn(cancelBtn, 1);
            buttonGrid.Children.Add(cancelBtn);

            var closeNowBtn = new Button
            {
                Width = 110,
                Height = 36,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            // Navy button with proper template (no default grey)
            var closeNowTemplate = new System.Windows.Controls.ControlTemplate(typeof(Button));
            var closeNowFactory = new System.Windows.FrameworkElementFactory(typeof(Border));
            closeNowFactory.SetValue(Border.BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 234, 240)));
            closeNowFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            var closeNowTextFactory = new System.Windows.FrameworkElementFactory(typeof(TextBlock));
            closeNowTextFactory.SetValue(TextBlock.TextProperty, "Close Now");
            closeNowTextFactory.SetValue(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.Black);
            closeNowTextFactory.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            closeNowTextFactory.SetValue(TextBlock.FontSizeProperty, 12.0);
            closeNowTextFactory.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            closeNowTextFactory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            closeNowFactory.AppendChild(closeNowTextFactory);
            closeNowTemplate.VisualTree = closeNowFactory;
            closeNowBtn.Template = closeNowTemplate;
            closeNowBtn.Click += (s, e) => { WasCancelled = false; _timer.Stop(); Close(); };
            System.Windows.Controls.Grid.SetColumn(closeNowBtn, 2);
            buttonGrid.Children.Add(closeNowBtn);

            contentStack.Children.Add(buttonGrid);
            outerStack.Children.Add(contentStack);
            mainBorder.Child = outerStack;
            Content = mainBorder;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;
            Loaded += (s, e) => _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            _remaining--;
            if (_remaining <= 0)
            {
                _timer.Stop();
                WasCancelled = false;
                Close();
            }
            else
            {
                _secondsText.Text = _remaining.ToString();
                _progressBar.Width = _progressMaxWidth * ((double)_remaining / _totalSeconds);
            }
        }
    }
}
