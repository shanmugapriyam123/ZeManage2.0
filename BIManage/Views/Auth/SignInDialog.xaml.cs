using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BIManage.Core.Identity;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;
using BIManage.ViewModels.Auth;
using WpfVisibility = System.Windows.Visibility;


namespace BIManageRevit.BIManage.Views.Auth
{
    public partial class SignInDialog : Window
    {
        private readonly SignInViewModel _viewModel;
        private readonly ILogger? _logger;
        private bool _passwordVisible;

        private static readonly System.Windows.Media.SolidColorBrush FocusBorderBrush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E6EAF0"));
        private static readonly System.Windows.Media.SolidColorBrush DefaultBorderBrush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E2E8F0"));
        private static readonly System.Windows.Media.SolidColorBrush FocusBackground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFF"));
        private static readonly System.Windows.Media.SolidColorBrush DefaultBackground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F8FAFC"));

        // Role badge background tints (orange theme — matches Ze'Manage logo)
        private static readonly System.Windows.Media.SolidColorBrush CompanyAdminBrush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FEF3EC"));
        private static readonly System.Windows.Media.SolidColorBrush ProjectAdminBrush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FEF3EC"));
        private static readonly System.Windows.Media.SolidColorBrush UserBrush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F1F5F9"));

        static SignInDialog()
        {
            CompanyAdminBrush.Freeze();
            ProjectAdminBrush.Freeze();
            UserBrush.Freeze();
        }

        public bool Success => _viewModel?.Success ?? false;

        public SignInDialog(AuthApiService authApi, AuthTokenManager tokenManager, ILogger? logger = null, IUserService? userService = null, SecureTokenStorage? secureStorage = null, SessionSyncService? sessionSyncService = null, string? sessionId = null)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            _logger = logger;
            _viewModel = new SignInViewModel(authApi, tokenManager, logger, userService, secureStorage, sessionSyncService, sessionId);
            DataContext = _viewModel;

            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(SignInViewModel.StatusMessage))
                    UpdateStatusPanel();
                if (e.PropertyName == nameof(SignInViewModel.IsAuthenticated))
                    SwitchView();
                if (e.PropertyName == nameof(SignInViewModel.RoleDisplay))
                    UpdateRoleBadge();
            };

            Loaded += (s, e) =>
            {
                SwitchView();
                if (!_viewModel.IsAuthenticated)
                    EmailBox.Focus();
            };
        }

        private async void SignIn_Click(object sender, RoutedEventArgs e)
        {
            SignInButton.IsEnabled = false;

            try
            {
                var password = _passwordVisible ? PasswordVisibleBox.Text : PasswordBox.Password;
                var success = await _viewModel.SignInAsync(password);
                if (success)
                {
                    SwitchView();
                }
                else
                {
                    // Wipe the password on failed login so the user starts fresh —
                    // previously the rejected value lingered in the box and they had
                    // to manually select-all + delete before retyping. Also flips the
                    // visible/masked state back to MASKED (with the matching RedEye
                    // glyph) so an interrupted login doesn't leave the password
                    // readable on screen.
                    PasswordBox.Clear();
                    PasswordVisibleBox.Clear();
                    if (_passwordVisible)
                    {
                        _passwordVisible = false;
                        PasswordVisibleBox.Visibility = WpfVisibility.Collapsed;
                        PasswordBox.Visibility = WpfVisibility.Visible;
                        EyeIcon.Text = ""; // Segoe MDL2 RedEye — matches masked state
                    }
                    PasswordPlaceholder.Visibility = WpfVisibility.Visible;
                    PasswordBox.Focus();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignIn_Click error: {ex.Message}", ex);
            }
            finally
            {
                SignInButton.IsEnabled = true;
            }
        }

        private void SignOut_Click(object sender, RoutedEventArgs e)
        {
            // Per user request (29 May 2026): Sign Out must ALWAYS take the user back
            // to the email/password form (image 2). Previously the VM's SignOut() had
            // two paths — for admin sessions it would only DOWNGRADE the role (keeping
            // IsAuthenticated=true and the device session active), and the dialog kept
            // showing the signed-in panel with a different role badge (image 3 in the
            // user's report). To enforce the "always log me out" behaviour, force the
            // VM into the full destructive branch by clearing IsAdminSession first
            // (calls ForceFullSignOut), then run the normal SignOut() — which now
            // falls into its non-admin branch and wipes tokens + identity + user state.
            _viewModel.ForceFullSignOut();

            PasswordBox.Clear();
            PasswordVisibleBox.Clear();
            _passwordVisible = false;
            PasswordVisibleBox.Visibility = WpfVisibility.Collapsed;
            PasswordBox.Visibility = WpfVisibility.Visible;

            // Belt-and-suspenders visibility flip in case any handler chain leaves
            // the signed-in panel visible after the VM call returns.
            FormPanel.Visibility = WpfVisibility.Visible;
            FormPanel.Opacity = 1;
            SignedInPanel.Visibility = WpfVisibility.Collapsed;
            SignedInStatusPanel.Visibility = WpfVisibility.Collapsed;
            EmailBox.Focus();
        }

        /// <summary>
        /// Switches between sign-in form and signed-in profile view.
        /// The signed-in profile (welcome + role + sign-out) renders for ANY successful
        /// admin-login (i.e. IsAuthenticated is only ever true after entering credentials
        /// through this form or restoring that state), regardless of which specific role
        /// the server resolved — Company Admin, Project Admin, or any other named role
        /// (e.g. "Superadmin") that has its own rule-based protections configured. A device
        /// that was only ever validated by MachineId (never went through admin-login) has
        /// IsAuthenticated=false and always lands on the blank form. Role-based admin
        /// PRIVILEGES (ribbon buttons, protection bypass) are governed separately by
        /// isCompanyAdmin/isProjectAdmin — this panel only decides what's DISPLAYED after
        /// a real login, not what access the role grants.
        /// </summary>
        private void SwitchView()
        {
            if (_viewModel.IsAuthenticated)
            {
                FormPanel.Visibility = WpfVisibility.Collapsed;
                SignedInPanel.Visibility = WpfVisibility.Visible;

                // Set user initial and welcome name from username
                var user = _viewModel.AuthenticatedUser;
                if (!string.IsNullOrEmpty(user))
                {
                    UserInitial.Text = user.Substring(0, 1).ToUpper();
                    SignedInWelcomeName.Text = $"{user}!";
                }

                // Show/hide company row
                CompanyRow.Visibility = string.IsNullOrEmpty(_viewModel.CompanyDisplay)
                    ? WpfVisibility.Collapsed
                    : WpfVisibility.Visible;

                UpdateRoleBadge();
            }
            else
            {
                FormPanel.Visibility = WpfVisibility.Visible;
                FormPanel.Opacity = 1;
                SignedInPanel.Visibility = WpfVisibility.Collapsed;
                SignedInStatusPanel.Visibility = WpfVisibility.Collapsed;
                EmailBox.Focus();
            }
        }

        /// <summary>
        /// Updates the role card based on current login role.
        /// </summary>
        private void UpdateRoleBadge()
        {
            var role = _viewModel.RoleDisplay?.Trim().ToLowerInvariant() ?? "";

            if (role.StartsWith("comp") || (role.Contains("company") && role.Contains("admin")))
            {
                RoleBadge.Background = CompanyAdminBrush;
                RoleInitial.Text = "C";
                RoleDescription.Text = "Full access across all projects";
            }
            else if (role.StartsWith("proj") || (role.Contains("project") && role.Contains("admin")))
            {
                RoleBadge.Background = ProjectAdminBrush;
                RoleInitial.Text = "P";
                RoleDescription.Text = "Manage assigned projects";
            }
            else
            {
                RoleBadge.Background = UserBrush;
                RoleInitial.Text = "U";
                RoleDescription.Text = "Standard access with protections";
            }

            RoleBadgeText.Text = _viewModel.RoleDisplay ?? "User";
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                SignIn_Click(sender, e);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void EmailBox_GotFocus(object sender, RoutedEventArgs e)
        {
            EmailBorder.BorderBrush = FocusBorderBrush;
            EmailBorder.Background = FocusBackground;
        }

        private void EmailBox_LostFocus(object sender, RoutedEventArgs e)
        {
            EmailBorder.BorderBrush = DefaultBorderBrush;
            EmailBorder.Background = DefaultBackground;
        }

        private void EmailBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            EmailPlaceholder.Visibility = string.IsNullOrEmpty(EmailBox.Text)
                ? WpfVisibility.Visible
                : WpfVisibility.Collapsed;
        }

        private void PasswordBox_GotFocus(object sender, RoutedEventArgs e)
        {
            PasswordBorder.BorderBrush = FocusBorderBrush;
            PasswordBorder.Background = FocusBackground;
        }

        private void PasswordBox_LostFocus(object sender, RoutedEventArgs e)
        {
            PasswordBorder.BorderBrush = DefaultBorderBrush;
            PasswordBorder.Background = DefaultBackground;
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            PasswordPlaceholder.Visibility = string.IsNullOrEmpty(PasswordBox.Password)
                ? WpfVisibility.Visible
                : WpfVisibility.Collapsed;
        }

        private void PasswordToggle_Click(object sender, RoutedEventArgs e)
        {
            _passwordVisible = !_passwordVisible;

            if (_passwordVisible)
            {
                PasswordVisibleBox.Text = PasswordBox.Password;
                PasswordBox.Visibility = WpfVisibility.Collapsed;
                PasswordVisibleBox.Visibility = WpfVisibility.Visible;
                PasswordVisibleBox.Focus();
                PasswordVisibleBox.CaretIndex = PasswordVisibleBox.Text.Length;
            }
            else
            {
                PasswordBox.Password = PasswordVisibleBox.Text;
                PasswordVisibleBox.Visibility = WpfVisibility.Collapsed;
                PasswordBox.Visibility = WpfVisibility.Visible;
                PasswordBox.Focus();
            }

            // Swap the eye glyph so it reflects current state: strikethrough eye
            // (Segoe MDL2 'Hide' = U+ED1A) while the password is visible, regular eye
            // ('RedEye' = U+E7B3) while it is masked. Without this swap the icon
            // stayed the same and gave no signal that the toggle actually flipped state.
            EyeIcon.Text = _passwordVisible ? "" : "";

            PasswordPlaceholder.Visibility = string.IsNullOrEmpty(_passwordVisible ? PasswordVisibleBox.Text : PasswordBox.Password)
                ? WpfVisibility.Visible
                : WpfVisibility.Collapsed;
        }

        private void PasswordVisibleBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            PasswordPlaceholder.Visibility = string.IsNullOrEmpty(PasswordVisibleBox.Text)
                ? WpfVisibility.Visible
                : WpfVisibility.Collapsed;
        }

        private void UpdateStatusPanel()
        {
            if (string.IsNullOrEmpty(_viewModel.StatusMessage))
            {
                StatusPanel.Visibility = WpfVisibility.Collapsed;
                return;
            }

            ShowStatus(_viewModel.StatusMessage, _viewModel.HasError);
        }

        private void ShowStatus(string message, bool isError)
        {
            StatusPanel.Visibility = WpfVisibility.Visible;

            if (isError)
            {
                StatusPanel.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FEF2F2"));
                StatusIcon.Text = "\u26A0";
                StatusIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DC2626"));
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DC2626"));
            }
            else
            {
                StatusPanel.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F0FDF4"));
                StatusIcon.Text = "\u2713";
                StatusIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#16A34A"));
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#16A34A"));
            }

            StatusText.Text = message;
        }

        private void ShowSignedInStatus(string message, bool isError)
        {
            SignedInStatusPanel.Visibility = WpfVisibility.Visible;

            if (isError)
            {
                SignedInStatusPanel.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FEF2F2"));
                SignedInStatusIcon.Text = "\u26A0";
                SignedInStatusIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DC2626"));
                SignedInStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DC2626"));
            }
            else
            {
                SignedInStatusPanel.Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F0FDF4"));
                SignedInStatusIcon.Text = "\u2713";
                SignedInStatusIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#16A34A"));
                SignedInStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#16A34A"));
            }

            SignedInStatusText.Text = message;
        }
    }
}
