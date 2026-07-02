using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;


namespace BIManageRevit.BIManage.Views.Support
{
    public partial class SupportDialog : Window
    {
        private const string WebsiteUrl = "https://zestinetech.com/";
        private const string DocsUrl = "https://zestinetech.github.io/Zestine-Docs/ZeManageRevit/";

        private readonly string? _revitVersion;
        private readonly string? _revitBuild;
        private readonly string? _revitUsername;
        private readonly string? _sessionId;
        private readonly string? _modelName;
        private readonly string? _modelPath;
        private readonly string? _projectRoot;
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly ILogger? _logger;

        public SupportDialog(
            string? revitVersion = null,
            string? revitBuild = null,
            string? revitUsername = null,
            string? sessionId = null,
            string? modelName = null,
            string? modelPath = null,
            bool isAdmin = false,
            AuthenticatedHttpClient? httpClient = null,
            ILogger? logger = null)
        {
            InitializeComponent();
            _revitVersion = revitVersion;
            _revitBuild = revitBuild;
            _revitUsername = revitUsername;
            _sessionId = sessionId;
            _modelName = modelName;
            _modelPath = modelPath;
            _projectRoot = FindProjectRoot();
            _httpClient = httpClient;
            _logger = logger;

            // Documentation only visible to admins
            if (!isAdmin)
                DocumentationButton.Visibility = System.Windows.Visibility.Collapsed;

            // Set version text. The csproj suppresses AssemblyFileVersionAttribute and
            // AssemblyInformationalVersionAttribute generation, and AssemblyVersion is pinned
            // to 1.0.0.0 (so WPF BAML pack URIs stay stable across product version bumps).
            // The product version (FileVersion) is still embedded in the assembly's Win32
            // version resource via the SDK's FileVersion property, so read it from there.
            var displayVersion = ReadProductVersion();
            VersionText.Text = $"ZeManage v{displayVersion} · ZestineTech";
        }

        /// <summary>
        /// Reads the product version (e.g. "0.2.4") for display.
        /// Tries multiple sources because the csproj suppresses some attribute generation:
        ///   1. AssemblyInformationalVersionAttribute (if generated)
        ///   2. AssemblyFileVersionAttribute (if generated)
        ///   3. Win32 file version resource via FileVersionInfo (always present when FileVersion is set)
        ///   4. AssemblyVersion as a last resort (currently 1.0.0.0 — pinned for WPF BAML stability)
        /// </summary>
        private static string ReadProductVersion()
        {
            try
            {
                var asm = typeof(SupportDialog).Assembly;

                // 1. InformationalVersion attribute
                var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(infoVer))
                    return Trim3(infoVer);

                // 2. FileVersion attribute
                var fileVerAttr = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
                if (!string.IsNullOrWhiteSpace(fileVerAttr))
                    return Trim3(fileVerAttr);

                // 3. Win32 file version resource (set by <FileVersion> in csproj)
                var location = asm.Location;
                if (!string.IsNullOrEmpty(location) && File.Exists(location))
                {
                    var fvi = FileVersionInfo.GetVersionInfo(location);
                    var winVer = !string.IsNullOrWhiteSpace(fvi.ProductVersion)
                        ? fvi.ProductVersion
                        : fvi.FileVersion;
                    if (!string.IsNullOrWhiteSpace(winVer))
                        return Trim3(winVer);
                }

                // 4. AssemblyVersion (pinned to 1.0.0.0 by design — least useful)
                var asmVer = asm.GetName().Version?.ToString();
                if (!string.IsNullOrWhiteSpace(asmVer))
                    return Trim3(asmVer);
            }
            catch { }
            return "0.0.0";
        }

        private static string Trim3(string version)
        {
            // "0.2.4.0" → "0.2.4"; "0.2.4+sha" → "0.2.4"
            var clean = version.Split('+', '-')[0];
            var parts = clean.Split('.');
            return parts.Length >= 3
                ? $"{parts[0]}.{parts[1]}.{parts[2]}"
                : clean;
        }

        private static string? FindProjectRoot()
        {
            var assemblyDir = Path.GetDirectoryName(typeof(SupportDialog).Assembly.Location);
            var current = assemblyDir;
            for (int i = 0; i < 8 && current != null; i++)
            {
                if (File.Exists(Path.Combine(current, "BIManageRevit.csproj")))
                    return current;
                current = Path.GetDirectoryName(current);
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var searchPaths = new[]
            {
                Path.Combine(userProfile, "Documents", "GitHub", "BIManageRevit"),
                Path.Combine(userProfile, "source", "repos", "BIManageRevit"),
                @"C:\Users\Admin\Documents\GitHub\BIManageRevit",
            };
            foreach (var path in searchPaths)
            {
                if (File.Exists(Path.Combine(path, "BIManageRevit.csproj")))
                    return path;
            }

            return null;
        }

        private void Website_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = WebsiteUrl, UseShellExecute = true });
            }
            catch
            {
                MessageBox.Show($"Please visit: {WebsiteUrl}", "ZestineTech", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void Documentation_Click(object sender, RoutedEventArgs e)
        {
            // Always open the hosted documentation site so every user (dev or end-user)
            // sees the same canonical, up-to-date docs. The previous local-first fallback
            // walked up the directory tree looking for the cloned BIManageRevit.csproj
            // and — when found — opened <repoRoot>/docs/index.html, which meant developers
            // running from the repo were sent to a file:// URL with potentially stale
            // content instead of the public site at DocsUrl. End-user machines never had
            // the repo so they correctly hit DocsUrl already; routing devs through the
            // same path keeps behavior consistent.
            try
            {
                Process.Start(new ProcessStartInfo { FileName = DocsUrl, UseShellExecute = true });
            }
            catch
            {
                MessageBox.Show($"Please visit: {DocsUrl}", "Documentation", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

private void ReportIssue_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new ReportIssueDialog(
                    httpClient: _httpClient,
                    revitVersion: _revitVersion,
                    revitBuild: _revitBuild,
                    revitUsername: _revitUsername,
                    sessionId: _sessionId,
                    modelName: _modelName,
                    modelPath: _modelPath,
                    logger: _logger);
                dialog.Owner = this;
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open report dialog: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();
    }
}
