using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;
using WpfVisibility = System.Windows.Visibility;


namespace BIManageRevit.BIManage.Views
{
    public partial class ModelRegistrationDialog : Window
    {
        public ModelRegistrationDialog()
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);
        }

        /// <summary>
        /// Show "Model Already Registered" state
        /// </summary>
        public static void ShowAlreadyRegistered(string modelName, string modelId, string modelType)
        {
            var dialog = new ModelRegistrationDialog();
            dialog.HeaderIcon.Text = "\u2139\uFE0F";
            dialog.TxtTitle.Text = "Already Registered";
            dialog.TxtSubtitle.Text = "Model is already tracked";

            dialog.TxtModelName.Text = modelName;
            dialog.TxtModelId.Text = modelId;
            dialog.TxtModelType.Text = modelType ?? "N/A";
            dialog.TxtProject.Text = "";
            dialog.TxtProject.Visibility = WpfVisibility.Collapsed;
            dialog.ProjectLabel.Visibility = WpfVisibility.Collapsed;
            dialog.ProjectSeparator.Visibility = WpfVisibility.Collapsed;

            dialog.StatusBorder.Background = new SolidColorBrush(WpfColor.FromRgb(219, 234, 254));
            dialog.TxtStatus.Foreground = new SolidColorBrush(WpfColor.FromRgb(30, 64, 175));
            dialog.TxtStatus.Text = "This model is already registered in ZeManage.\nAll tracking and protection features are active.";

            dialog.FeaturesSection.Visibility = WpfVisibility.Collapsed;
            new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle };
            dialog.ShowDialog();
        }

        /// <summary>
        /// Show "Model Registered Successfully" state
        /// </summary>
        public static void ShowRegistered(string modelName, string modelId, string modelType, string projectName)
        {
            var dialog = new ModelRegistrationDialog();
            dialog.HeaderIcon.Text = "\u2705";
            dialog.TxtTitle.Text = "Model Registered";
            dialog.TxtSubtitle.Text = "Registration successful";

            dialog.TxtModelName.Text = modelName;
            dialog.TxtModelId.Text = modelId;
            dialog.TxtModelType.Text = modelType ?? "N/A";
            dialog.TxtProject.Text = projectName ?? "N/A";

            dialog.StatusBorder.Background = new SolidColorBrush(WpfColor.FromRgb(220, 252, 231));
            dialog.TxtStatus.Foreground = new SolidColorBrush(WpfColor.FromRgb(21, 128, 61));
            dialog.TxtStatus.Text = "This model is now registered in ZeManage.";

            // Show features
            dialog.FeaturesSection.Visibility = WpfVisibility.Visible;
            var features = new List<string>
            {
                "Session and metrics tracking",
                "Element protection rules",
                "Evidence capture",
                "Command monitoring"
            };

            foreach (var feature in features)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
                item.Children.Add(new TextBlock
                {
                    Text = "\u2022",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(21, 128, 61)),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                item.Children.Add(new TextBlock
                {
                    Text = feature,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(30, 41, 59))
                });
                dialog.FeaturesList.Children.Add(item);
            }

            new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle };
            dialog.ShowDialog();
        }

        /// <summary>
        /// Show warning when no active document is open
        /// </summary>
        public static void ShowNoDocument()
        {
            ShowWarning(
                "\u26A0\uFE0F",
                "No Document",
                "Cannot register model",
                "No active document found.\n\nPlease open a model first.");
        }

        /// <summary>
        /// Show warning when model has not been saved to disk
        /// </summary>
        public static void ShowNotSavedWarning()
        {
            ShowWarning(
                "\u26A0\uFE0F",
                "Model Not Saved",
                "Cannot register model",
                "This model has not been saved to disk yet.\n\nPlease save the file (File \u2192 Save As) before registering it with ZeManage.");
        }

        /// <summary>
        /// Show warning when model is a detached copy
        /// </summary>
        public static void ShowDetachedWarning()
        {
            ShowWarning(
                "\u26A0\uFE0F",
                "Detached Copy",
                "Cannot register model",
                "This model was opened as a detached copy and has not been saved as a standalone file.\n\nPlease use File \u2192 Save As to save it to a new location, then register it.");
        }

        /// <summary>
        /// Show error when registration service is unavailable
        /// </summary>
        public static void ShowServiceUnavailable()
        {
            ShowError(
                "Service Unavailable",
                "Model registration service not available.\n\nPlease check the plugin installation.");
        }

        /// <summary>
        /// Show error when model identifier cannot be determined
        /// </summary>
        public static void ShowNoIdentifier()
        {
            ShowWarning(
                "\u26A0\uFE0F",
                "Unknown Identifier",
                "Cannot register model",
                "Unable to determine model identifier.\n\nThis model may not be workshared or may not have a unique identifier.");
        }

        /// <summary>
        /// Show error when registration fails
        /// </summary>
        public static void ShowRegistrationFailed()
        {
            ShowError(
                "Registration Failed",
                "Failed to register the model.\n\nThis may be due to a database error or the model may already be registered.\n\nCheck the log file for details.");
        }

        /// <summary>
        /// Shown when the local registration succeeded but the server rejected the POST
        /// because the company has no default project configured. The model will not
        /// appear in the tenant's backend dashboards until a BIManage admin provisions
        /// the company, and model/session/metrics sync will all cascade-fail until then.
        /// </summary>
        public static void ShowTenantNotProvisioned()
        {
            ShowWarning(
                "\u26A0", // WARNING SIGN
                "Company Not Provisioned",
                "Server rejected model registration",
                "Your model was saved locally, but the BIManage server rejected registration because your company has no default project configured.\n\nContact your BIManage administrator to provision the company. Until then:\n  • the model won't appear on your company's dashboard\n  • model sessions, metrics, and sync history cannot be uploaded\n\nThe local record is kept, so registration will be retried automatically on the next model open.");
        }

        /// <summary>
        /// Shown when local registration succeeded but the server rejected the POST
        /// for a reason other than unprovisioned tenant.
        /// </summary>
        public static void ShowServerRejected(int? statusCode, string? responseBody)
        {
            ShowWarning(
                "\u26A0",
                "Server Rejected Registration",
                "Registered locally only",
                $"Your model was saved locally, but the BIManage server rejected the registration request ({statusCode} response).\n\nThe local record is kept, and registration will be retried automatically on the next model open.\n\nServer response:\n{Truncate(responseBody, 400)}");
        }

        /// <summary>
        /// Shown when local registration succeeded but the server sync is queued for retry
        /// (network error, 5xx). No admin action required — will retry automatically.
        /// </summary>
        public static void ShowRegisteredPendingServer(string modelName)
        {
            ShowWarning(
                "\u23F3", // HOURGLASS
                "Registration Queued",
                "Saved locally — server sync pending",
                $"'{modelName}' was registered locally. Server sync is queued and will retry automatically when connectivity is restored.");
        }

        private static string Truncate(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "(empty)";
            return s!.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>
        /// Show error with exception details
        /// </summary>
        public static void ShowRegistrationError(string errorMessage)
        {
            ShowError(
                "Registration Error",
                $"An error occurred while registering the model:\n\n{errorMessage}\n\nCheck the log file for details.");
        }

        internal static void ShowWarning(string icon, string title, string subtitle, string message)
        {
            var dialog = new ModelRegistrationDialog();
            dialog.HeaderIcon.Text = icon;
            dialog.TxtTitle.Text = title;
            dialog.TxtSubtitle.Text = subtitle;

            // Hide model details card
            dialog.ModelDetailsCard.Visibility = WpfVisibility.Collapsed;
            dialog.FeaturesSection.Visibility = WpfVisibility.Collapsed;

            // Warning-style status
            dialog.StatusBorder.Background = new SolidColorBrush(WpfColor.FromRgb(254, 243, 199)); // #FEF3C7
            dialog.TxtStatus.Foreground = new SolidColorBrush(WpfColor.FromRgb(146, 64, 14)); // #92400E
            dialog.TxtStatus.Text = message;

            new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle };
            dialog.ShowDialog();
        }

        private static void ShowError(string title, string message)
        {
            var dialog = new ModelRegistrationDialog();
            dialog.HeaderIcon.Text = "\u274C";
            dialog.TxtTitle.Text = title;
            dialog.TxtSubtitle.Text = "Something went wrong";

            // Hide model details card
            dialog.ModelDetailsCard.Visibility = WpfVisibility.Collapsed;
            dialog.FeaturesSection.Visibility = WpfVisibility.Collapsed;

            // Error-style status
            dialog.StatusBorder.Background = new SolidColorBrush(WpfColor.FromRgb(254, 226, 226)); // #FEE2E2
            dialog.TxtStatus.Foreground = new SolidColorBrush(WpfColor.FromRgb(185, 28, 28)); // #B91C1C
            dialog.TxtStatus.Text = message;

            new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle };
            dialog.ShowDialog();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape || e.Key == Key.Enter)
            {
                Close();
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
    }
}
