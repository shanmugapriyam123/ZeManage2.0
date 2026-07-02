using System.Windows;
using System.Windows.Input;
using BIManage.Core.Identity;


namespace BIManageRevit.BIManage.Views.Developer
{
    /// <summary>
    /// Role dialog that shows the current login-based role.
    /// Role is determined by the API response during authentication.
    /// </summary>
    public partial class RoleSwitchDialog : Window
    {
        /// <summary>
        /// The selected role after dialog closes
        /// </summary>
        public UserRole SelectedRole { get; private set; }

        /// <summary>
        /// The current role when dialog opened (from login)
        /// </summary>
        public UserRole CurrentRole { get; }

        /// <summary>
        /// The logged-in user identity
        /// </summary>
        private readonly UserIdentity? _userIdentity;

        public RoleSwitchDialog(UserRole currentRole, UserIdentity? userIdentity = null)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);
            CurrentRole = currentRole;
            SelectedRole = currentRole;
            _userIdentity = userIdentity;
            DataContext = this;

            // Set user initial from identity
            if (_userIdentity != null)
            {
                var name = _userIdentity.UserName ?? _userIdentity.Email ?? "";
                if (!string.IsNullOrEmpty(name))
                {
                    UserInitialText.Text = name.Substring(0, 1).ToUpper();
                }
            }

            UpdateRoleHighlight();
        }

        /// <summary>
        /// Display string for current role
        /// </summary>
        public string CurrentRoleDisplay => CurrentRole switch
        {
            UserRole.CompanyAdministrator => "Super Admin",
            UserRole.ProjectAdministrator => "Admin",
            UserRole.NormalUser => "User",
            _ => CurrentRole.ToString()
        };

        /// <summary>
        /// User name from login
        /// </summary>
        public string CurrentUserName => _userIdentity?.UserName ?? _userIdentity?.Email ?? System.Environment.UserName;

        /// <summary>
        /// User email from login
        /// </summary>
        public string CurrentUserEmail => _userIdentity?.Email ?? "";

        private void UpdateRoleHighlight()
        {
            SuperAdminCheck.Visibility = System.Windows.Visibility.Collapsed;
            AdminCheck.Visibility = System.Windows.Visibility.Collapsed;
            UserCheck.Visibility = System.Windows.Visibility.Collapsed;

            switch (CurrentRole)
            {
                case UserRole.CompanyAdministrator:
                    SuperAdminCheck.Visibility = System.Windows.Visibility.Visible;
                    break;
                case UserRole.ProjectAdministrator:
                    AdminCheck.Visibility = System.Windows.Visibility.Visible;
                    break;
                case UserRole.NormalUser:
                    UserCheck.Visibility = System.Windows.Visibility.Visible;
                    break;
            }
        }

        private void SuperAdminButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedRole = UserRole.CompanyAdministrator;
            DialogResult = true;
            Close();
        }

        private void AdminButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedRole = UserRole.ProjectAdministrator;
            DialogResult = true;
            Close();
        }

        private void UserButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedRole = UserRole.NormalUser;
            DialogResult = true;
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
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
