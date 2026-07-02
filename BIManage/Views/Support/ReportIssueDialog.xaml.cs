using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;


namespace BIManageRevit.BIManage.Views.Support
{
    public partial class ReportIssueDialog : Window
    {
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly ILogger? _logger;
        private readonly string? _revitVersion;
        private readonly string? _revitBuild;
        private readonly string? _revitUsername;
        private readonly string? _sessionId;
        private readonly string? _modelName;
        private readonly string? _modelPath;
        private readonly string? _logFilePath;
        private readonly string? _pluginVersion;

        public ReportIssueDialog(
            AuthenticatedHttpClient? httpClient = null,
            string? revitVersion = null,
            string? revitBuild = null,
            string? revitUsername = null,
            string? sessionId = null,
            string? modelName = null,
            string? modelPath = null,
            string? preSelectModule = null,
            ILogger? logger = null)
        {
            InitializeComponent();
            _httpClient = httpClient;
            _logger = logger;
            _revitVersion = revitVersion;
            _revitBuild = revitBuild;
            _revitUsername = revitUsername;
            _sessionId = sessionId;
            _modelName = modelName;
            _modelPath = modelPath;
            // FileVersion holds the product release identifier; AssemblyVersion is pinned to 1.0.0.0
            // to keep WPF pack URIs stable across product version bumps.
            _pluginVersion = typeof(ReportIssueDialog).Assembly
                .GetCustomAttribute<System.Reflection.AssemblyFileVersionAttribute>()?.Version
                ?? typeof(ReportIssueDialog).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";

            // Find latest log file
            _logFilePath = FindLatestLogFile();
            if (!string.IsNullOrEmpty(_logFilePath))
                LogFileName.Text = $" ({Path.GetFileName(_logFilePath)})";
            else
                AttachLogCheck.IsEnabled = false;

            // Auto-fill system info
            var info = $"Computer: {Environment.MachineName}\n" +
                       $"User: {Environment.UserName}\n" +
                       $"Revit: {_revitVersion ?? "N/A"} (Build {_revitBuild ?? "N/A"})\n" +
                       $"Revit User: {_revitUsername ?? "N/A"}\n" +
                       $"Model: {_modelName ?? "N/A"}\n" +
                       $"Session: {_sessionId ?? "N/A"}\n" +
                       $"Plugin: v{_pluginVersion}\n" +
                       $"OS: {Environment.OSVersion.Version}";
            SystemInfoText.Text = info;

            // Pre-select module if specified
            if (!string.IsNullOrEmpty(preSelectModule))
            {
                foreach (ComboBoxItem item in ModuleCombo.Items)
                {
                    if (item.Content?.ToString() == preSelectModule)
                    {
                        ModuleCombo.SelectedItem = item;
                        break;
                    }
                }
            }

            TitleInput.Focus();
        }

        private string? FindLatestLogFile()
        {
            try
            {
                var logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BIManageRevit", "Logs");

                if (!Directory.Exists(logDir)) return null;

                string? latest = null;
                DateTime latestTime = DateTime.MinValue;

                foreach (var f in Directory.GetFiles(logDir, "BIManageRevit_*.log"))
                {
                    var fi = new FileInfo(f);
                    if (fi.LastWriteTime > latestTime)
                    {
                        latestTime = fi.LastWriteTime;
                        latest = f;
                    }
                }
                return latest;
            }
            catch { return null; }
        }

        private void Input_TextChanged(object sender, TextChangedEventArgs e)
        {
            SubmitButton.IsEnabled = !string.IsNullOrWhiteSpace(TitleInput.Text)
                                  && !string.IsNullOrWhiteSpace(DescriptionInput.Text);
        }

        private async void Submit_Click(object sender, RoutedEventArgs e)
        {
            SubmitButton.IsEnabled = false;
            SubmitButton.Content = "Submitting...";
            StatusText.Text = "Submitting ticket...";
            StatusText.Foreground = System.Windows.Media.Brushes.Gray;
            StatusText.Visibility = System.Windows.Visibility.Visible;

            try
            {
                var id = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
                var module = (ModuleCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
                var priority = (PriorityCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Medium";
                var now = DateTime.Now;

                _logger?.LogInfo($"[Support] Submit clicked: title='{TitleInput.Text.Trim()}', module='{module}', priority='{priority}', attachLog={AttachLogCheck.IsChecked == true}");

                var sysInfo = $"Computer: {Environment.MachineName} | OS: {Environment.OSVersion.Version} | " +
                              $"Revit: {_revitVersion} Build {_revitBuild} | " +
                              $"Model: {_modelName ?? "N/A"} | Session: {_sessionId ?? "N/A"} | Plugin: v{_pluginVersion}";

                using var content = new MultipartFormDataContent();
                // Id is auto-filled by the server (not part of the multipart spec) —
                // client no longer sends it. The local `id` variable is still kept for
                // log correlation only.
                content.Add(new StringContent("Revit"), "TicketType"); // Required by API — identifies source
                content.Add(new StringContent(TitleInput.Text.Trim()), "Title");
                content.Add(new StringContent(module), "Module");
                content.Add(new StringContent(priority), "Priority");
                content.Add(new StringContent("Open"), "Status");
                content.Add(new StringContent(_revitVersion ?? ""), "RevitVersion");
                content.Add(new StringContent(_revitUsername ?? Environment.UserName), "ReportedBy");
                content.Add(new StringContent(""), "AssignedTo");
                content.Add(new StringContent(now.ToString("yyyy-MM-dd")), "ReportedDate");
                content.Add(new StringContent(_pluginVersion ?? ""), "TargetVersion");
                content.Add(new StringContent(DescriptionInput.Text.Trim() + "\n\n--- System Info ---\n" + sysInfo), "Description");
                content.Add(new StringContent(""), "Resolution");
                content.Add(new StringContent($"Auto-submitted from ZeManage plugin | {Environment.MachineName} | {now:yyyy-MM-dd HH:mm}"), "Remarks");

                // Attach log file
                if (AttachLogCheck.IsChecked == true && !string.IsNullOrEmpty(_logFilePath) && File.Exists(_logFilePath))
                {
                    try
                    {
                        byte[] logBytes;
                        using (var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var ms = new MemoryStream())
                        {
                            await fs.CopyToAsync(ms);
                            logBytes = ms.ToArray();
                        }
                        var fileContent = new ByteArrayContent(logBytes);
                        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                        content.Add(fileContent, "Attachment", Path.GetFileName(_logFilePath));
                    }
                    catch { /* Skip attachment if file locked */ }
                }

                // POST to API
                _logger?.LogInfo($"[Support] Pre-POST: httpClient={(_httpClient == null ? "null" : "set")}, isAuthenticated={_httpClient?.IsAuthenticated}");
                if (_httpClient != null && _httpClient.IsAuthenticated)
                {
                    bool hasAttachment = AttachLogCheck.IsChecked == true && !string.IsNullOrEmpty(_logFilePath) && File.Exists(_logFilePath);
                    _logger?.LogInfo($"[Support] POST /api/v1/master/tickets (Id={id}, TicketType=Revit, hasAttachment={hasAttachment})");
                    var response = await _httpClient.PostMultipartAsync("/api/v1/master/tickets", content);
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogInfo($"[Support] Response: HTTP {(int)response.StatusCode} {response.StatusCode}, bodyLength={responseBody?.Length ?? 0}");

                    System.Diagnostics.Debug.WriteLine($"[Ticket Submit] Status: {response.StatusCode}");
                    System.Diagnostics.Debug.WriteLine($"[Ticket Submit] Response Body: {responseBody}");

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger?.LogWarning($"[Support] POST failed: HTTP {(int)response.StatusCode}: {(responseBody != null && responseBody.Length > 300 ? responseBody.Substring(0, 300) : responseBody)}");
                        StatusText.Text = $"Failed: HTTP {(int)response.StatusCode}";
                        StatusText.Foreground = System.Windows.Media.Brushes.Red;
                        MessageBox.Show(
                            $"Failed to submit ticket.\n\nHTTP {(int)response.StatusCode} {response.StatusCode}\n\nResponse:\n{responseBody}",
                            "Submit Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // === Step 1: Trust the POST response (server is authoritative) ===
                    // The server returns { "success": true, "ticketId": "...", "ticketType": "Revit" } on success.
                    // If success=true, the ticket WAS saved — full stop.
                    // The optional GET-verification below is informational only (it filters by company/user
                    // and may legitimately return 0 even when the ticket exists).
                    bool postSuccess = false;
                    string? returnedTicketId = null;
                    try
                    {
                        using var postDoc = System.Text.Json.JsonDocument.Parse(responseBody);
                        var root = postDoc.RootElement;
                        if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (root.TryGetProperty("success", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.True)
                                postSuccess = true;
                            if (root.TryGetProperty("ticketId", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String)
                                returnedTicketId = t.GetString();
                            // Some servers return the row inside a "data" wrapper
                            if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                if (returnedTicketId == null && dataEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.String)
                                    returnedTicketId = idEl.GetString();
                                if (returnedTicketId == null && dataEl.TryGetProperty("ticketId", out var tEl) && tEl.ValueKind == System.Text.Json.JsonValueKind.String)
                                    returnedTicketId = tEl.GetString();
                            }
                        }
                    }
                    catch { /* non-JSON body — fall through */ }

                    // If the response wasn't parseable but HTTP was 2xx, assume success.
                    if (!postSuccess)
                        postSuccess = response.IsSuccessStatusCode;

                    // Use the server-assigned id when present, otherwise fall back to the locally-generated one.
                    var displayId = !string.IsNullOrWhiteSpace(returnedTicketId) ? returnedTicketId! : id;

                    // === Step 2: Optional GET soft-check (informational only) ===
                    int totalTickets = 0;
                    string? savedCompanyId = null;
                    try
                    {
                        var verifyResponse = await _httpClient.GetAsync("/api/v1/master/tickets/revit");
                        var verifyBody = await verifyResponse.Content.ReadAsStringAsync();
                        using var verifyDoc = System.Text.Json.JsonDocument.Parse(verifyBody);
                        if (verifyDoc.RootElement.TryGetProperty("data", out var dataArray)
                            && dataArray.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            totalTickets = dataArray.GetArrayLength();
                            foreach (var ticket in dataArray.EnumerateArray())
                            {
                                if (ticket.TryGetProperty("id", out var idProp)
                                    && string.Equals(idProp.GetString(), displayId, StringComparison.OrdinalIgnoreCase)
                                    && ticket.TryGetProperty("companyId", out var cidProp))
                                {
                                    savedCompanyId = cidProp.GetString();
                                    break;
                                }
                            }
                        }
                    }
                    catch { /* GET filtering is independent of save success — ignore failures */ }

                    if (postSuccess)
                    {
                        _logger?.LogInfo($"[Support] Ticket submitted successfully: ticketId={displayId}");
                        SuccessIdText.Text = $"Ticket ID: {displayId}";
                        SuccessTitleText.Text = TitleInput.Text.Trim();
                        SuccessOverlay.Visibility = System.Windows.Visibility.Visible;
                        return;
                    }
                    else
                    {
                        _logger?.LogWarning($"[Support] HTTP {(int)response.StatusCode} but server did not confirm success. Body: {(responseBody != null && responseBody.Length > 300 ? responseBody.Substring(0, 300) : responseBody)}");
                        StatusText.Text = "Submission failed";
                        StatusText.Foreground = System.Windows.Media.Brushes.Red;
                        MessageBox.Show(
                            $"The API responded but did not confirm success.\n\nSent Id: {id}\n\nResponse:\n{responseBody}",
                            "Submit Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    _logger?.LogWarning("[Support] _httpClient null or not authenticated — POST skipped");
                    StatusText.Text = "Not authenticated - cannot submit";
                    StatusText.Foreground = System.Windows.Media.Brushes.Red;

                    MessageBox.Show(
                        "Device not authenticated.\n\nPlease register your device first to submit tickets.",
                        "Authentication Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[Support] Submit threw: {ex.Message}", ex);
                StatusText.Text = $"Error: {ex.Message}";
                StatusText.Foreground = System.Windows.Media.Brushes.Red;

                MessageBox.Show($"Failed to submit: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SubmitButton.Content = "Submit Request";
                SubmitButton.IsEnabled = true;
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }
    }
}
