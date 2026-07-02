using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;

using WpfVisibility = System.Windows.Visibility;


namespace BIManageRevit.BIManage.Views.Bindings
{
    public partial class OtpGenerateDialog : Window
    {
        private readonly AuthenticatedHttpClient _httpClient;
        private readonly ILogger? _logger;
        private readonly string _generatedBy;
        private readonly string? _profileId;

        private bool _otpGenerated;
        private string _otpCode = string.Empty;
        private DateTime? _expiresAt;

        public string OtpCode => _otpCode;
        public bool Generated => _otpGenerated;

        public OtpGenerateDialog(AuthenticatedHttpClient httpClient, string generatedBy, ILogger? logger = null, string? profileId = null)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);
            _httpClient = httpClient;
            _generatedBy = generatedBy;
            _logger = logger;
            _profileId = profileId;
        }

        private async void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_otpGenerated)
            {
                DialogResult = true;
                Close();
                return;
            }

            ActionButton.IsEnabled = false;
            ActionButton.Content = "Generating...";
            ErrorBorder.Visibility = WpfVisibility.Collapsed;

            try
            {
                var reason = ReasonTextBox.Text?.Trim() ?? string.Empty;
                // Send profileId in body as fallback for device tokens that lack the JWT claim
                var body = !string.IsNullOrEmpty(_profileId)
                    ? (object)new { reason, profileId = _profileId }
                    : new { reason };

                var url = "/api/v1/Revit/otp/generate";

                _logger?.LogInfo($"[OTP] POST {url} => reason={reason}, user={_generatedBy}");

                // Run HTTP call on thread pool to avoid potential SynchronizationContext
                // deadlocks with SemaphoreSlim in AuthTokenManager
                var (statusCode, json) = await Task.Run(async () =>
                {
                    var resp = await _httpClient.PostAsync(url, body)
                        .ConfigureAwait(false);
                    var content = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return ((int)resp.StatusCode, content);
                });

                _logger?.LogInfo($"[OTP] Response ({statusCode}): {json}");

                if (statusCode < 200 || statusCode >= 300)
                {
                    _logger?.LogError($"[OTP] API error ({statusCode}): {json}");
                    var errorMsg = ExtractErrorMessage(json, statusCode);
                    string friendlyMessage;
                    if (statusCode == 401)
                        friendlyMessage = errorMsg ?? "Authentication expired. Please sign in again.";
                    else if (statusCode == 403)
                        friendlyMessage = errorMsg ?? "Access denied. You may not have permission to generate OTPs.";
                    else if (statusCode == 404)
                        friendlyMessage = "OTP service not available. Please contact your administrator.";
                    else if (statusCode >= 500)
                        friendlyMessage = errorMsg ?? "Server is temporarily unavailable. Please try again later.";
                    else
                        friendlyMessage = errorMsg ?? $"Request failed ({statusCode}).";
                    ShowError(friendlyMessage);
                    ActionButton.IsEnabled = true;
                    ActionButton.Content = "Generate OTP";
                    return;
                }

                // Parse response — server may return { data: { code, expiresAt } } or { code, expiresAt }
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Unwrap "data" wrapper if present
                var data = root;
                if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object)
                    data = dataElement;

                _otpCode = TryGetString(data, "code", "Code", "otpCode", "OtpCode") ?? string.Empty;

                var expiresStr = TryGetString(data, "expiresAt", "ExpiresAt");
                if (!string.IsNullOrEmpty(expiresStr) &&
                    DateTime.TryParse(expiresStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var exp))
                {
                    _expiresAt = exp;
                }

                if (string.IsNullOrEmpty(_otpCode))
                {
                    _logger?.LogWarning($"[OTP] Could not parse OTP code from response: {json}");
                    ShowError("Unexpected response format. Check logs.");
                    ActionButton.IsEnabled = true;
                    ActionButton.Content = "Generate OTP";
                    return;
                }

                // Switch to "Generated" state
                _otpGenerated = true;
                HeaderTitle.Text = "OTP Generated";
                Title = "OTP Generated";
                TxtOtpCode.Text = _otpCode;
                BtnCopy.Visibility = WpfVisibility.Visible;
                ReasonTextBox.IsReadOnly = true;

                if (_expiresAt.HasValue)
                {
                    UpdateExpiryText();
                    ExpiryRow.Visibility = WpfVisibility.Visible;
                }

                ActionButton.Content = "OK";
                ActionButton.IsEnabled = true;

                _logger?.LogInfo($"[OTP] Generated successfully: {_otpCode}, expires: {_expiresAt}");
            }
            catch (TaskCanceledException)
            {
                _logger?.LogWarning("[OTP] Request timed out");
                ShowError("Connection timed out. Please check your network and try again.");
                ActionButton.IsEnabled = true;
                ActionButton.Content = "Generate OTP";
            }
            catch (Exception ex)
            {
                var detail = GetExceptionChain(ex);
                _logger?.LogError($"[OTP] Generate error: {detail}", ex);

                // Translate common network errors to user-friendly messages
                var innerMsg = ex.InnerException?.Message ?? ex.Message;
                if (innerMsg.Contains("No connection could be made") ||
                    innerMsg.Contains("actively refused") ||
                    innerMsg.Contains("Unable to connect") ||
                    innerMsg.Contains("No such host"))
                {
                    ShowError("Unable to reach the server. Please check your network connection.");
                }
                else
                {
                    ShowError("An unexpected error occurred. Please try again.");
                }
                ActionButton.IsEnabled = true;
                ActionButton.Content = "Generate OTP";
            }
        }

        private static string GetExceptionChain(Exception ex)
        {
            // Walk to the deepest inner exception (root cause)
            var root = ex;
            while (root.InnerException != null)
                root = root.InnerException;

            // If the root cause is the same as the top-level, just show it
            if (root == ex)
                return $"{ex.GetType().Name}: {ex.Message}";

            // Show root cause + top-level for context
            return $"{root.GetType().Name}: {root.Message}";
        }

        private static string? TryGetString(JsonElement root, params string[] names)
        {
            foreach (var name in names)
            {
                if (root.TryGetProperty(name, out var prop))
                {
                    return prop.ValueKind == JsonValueKind.Number
                        ? prop.GetRawText()
                        : prop.GetString();
                }
            }
            return null;
        }

        private static string ExtractErrorMessage(string json, int statusCode)
        {
            if (string.IsNullOrWhiteSpace(json))
                return "Empty response from server";

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // ASP.NET validation errors: { errors: { Field: ["msg"] } }
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                {
                    var messages = new List<string>();
                    foreach (var field in errors.EnumerateObject())
                    {
                        if (field.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var err in field.Value.EnumerateArray())
                                messages.Add(err.GetString() ?? field.Name);
                        }
                        else
                        {
                            messages.Add($"{field.Name}: {field.Value}");
                        }
                    }
                    if (messages.Count > 0)
                        return string.Join("; ", messages);
                }

                // Try specific error fields
                var msg = TryGetString(root, "message", "Message", "detail", "Detail",
                    "title", "Title", "error", "Error");
                if (!string.IsNullOrEmpty(msg))
                    return msg;
            }
            catch { }

            // Show raw response for debugging
            var raw = json.Length > 300 ? json.Substring(0, 300) + "..." : json;
            return raw;
        }

        private void UpdateExpiryText()
        {
            if (!_expiresAt.HasValue) return;

            var remaining = _expiresAt.Value.ToUniversalTime() - DateTime.UtcNow;
            if (remaining.TotalSeconds <= 0)
            {
                TxtExpiry.Text = "Expired";
                return;
            }

            var totalMinutes = (int)Math.Round(remaining.TotalMinutes);
            if (totalMinutes < 60)
            {
                TxtExpiry.Text = $"{totalMinutes} min";
            }
            else
            {
                var hrs = totalMinutes / 60;
                var mins = totalMinutes % 60;
                TxtExpiry.Text = mins == 0
                    ? $"{hrs} hr{(hrs > 1 ? "s" : "")}"
                    : $"{hrs} hr{(hrs > 1 ? "s" : "")} {mins} min";
            }
        }

        private void CopyCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_otpCode);

                if (BtnCopy.Template.FindName("CopyText", BtnCopy) is System.Windows.Controls.TextBlock txt)
                {
                    txt.Text = "Copied!";
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    timer.Tick += (s, args) => { txt.Text = "Copy"; timer.Stop(); };
                    timer.Start();
                }
            }
            catch { }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = _otpGenerated;
            Close();
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorBorder.Visibility = WpfVisibility.Visible;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = _otpGenerated;
                Close();
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }
    }
}
