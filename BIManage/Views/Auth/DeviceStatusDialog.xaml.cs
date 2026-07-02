using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using BIManage.Infrastructure.Api;
using BIManage.Licensing;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;


namespace BIManageRevit.BIManage.Views.Auth
{
    public partial class DeviceStatusDialog : Window
    {
        /// <summary>Fired after live heartbeat resolves — (isActive, licenseMode) where licenseMode: 0=Breached, 1=Licensed, 2=Passive.</summary>
        public event Action<bool, int>? LiveStatusResolved;

        private readonly SessionSyncService? _sessionSyncService;
        private readonly string? _sessionId;
        private readonly string? _adminTypeLabel;
        private readonly string? _machineId;
        // Remember the cached registered/passive state captured at constructor time so the
        // heartbeat-failure fallback can decide whether to keep the green main display
        // (cache says healthy, just couldn't refresh right now) or repaint to yellow
        // (cache itself indicates a problem).
        private bool _cachedIsRegistered;
        private bool _cachedIsPassive;

        public DeviceStatusDialog(
            bool isRegistered,
            string machineId,
            bool isPassive = false,
            SessionSyncService? sessionSyncService = null,
            string? sessionId = null,
            string? adminTypeLabel = null,
            LicenseInfo? licenseInfo = null)
        {
            InitializeComponent();

            _sessionSyncService = sessionSyncService;
            _sessionId = sessionId;
            _adminTypeLabel = adminTypeLabel;
            _machineId = machineId;

            MachineIdText.Text = machineId;

            ApplyCachedStatus(isRegistered, isPassive);

            if (adminTypeLabel != null)
            {
                RoleText.Text = adminTypeLabel;
                RoleRow.Visibility = System.Windows.Visibility.Visible;
            }

            ApplyLicenseInfo(licenseInfo);
        }

        private void ApplyLicenseInfo(LicenseInfo? info)
        {
            if (info == null) return;

            if (!string.IsNullOrEmpty(info.CompanyName))
            {
                CompanyText.Text = info.CompanyName;
                CompanyRow.Visibility = System.Windows.Visibility.Visible;
            }

            if (info.EnabledModules != null && info.EnabledModules.Count > 0)
            {
                var names = info.EnabledModules
                    .Select(id => Enum.IsDefined(typeof(LicenseModule), id) ? ((LicenseModule)id).ToString() : $"Module {id}")
                    .ToList();
                ModulesText.Text = string.Join(", ", names);
                ModulesRow.Visibility = System.Windows.Visibility.Visible;
            }
        }

        private void ApplyCachedStatus(bool isRegistered, bool isPassive)
        {
            _cachedIsRegistered = isRegistered;
            _cachedIsPassive = isPassive;
            var green = WpfColor.FromRgb(22, 163, 74);
            var red = WpfColor.FromRgb(220, 38, 38);

            if (isRegistered)
            {
                StatusCircle.Background = new WpfBrush(green);
                StatusIcon.Text = "\u2713";
                StatusTitle.Text = "Device Registered";
                StatusTitle.Foreground = new WpfBrush(green);
                StatusValue.Text = "Registered";
                StatusValue.Foreground = new WpfBrush(green);
                LicenseModeText.Text = isPassive ? "Passive Mode" : "Licensed";
                LicenseModeText.Foreground = isPassive
                    ? new WpfBrush(WpfColor.FromRgb(202, 138, 4))
                    : new WpfBrush(green);
            }
            else
            {
                StatusCircle.Background = new WpfBrush(red);
                StatusIcon.Text = "!";
                StatusTitle.Text = "Device Not Registered";
                StatusTitle.Foreground = new WpfBrush(red);
                StatusValue.Text = "Not Registered";
                StatusValue.Foreground = new WpfBrush(red);
                LicenseModeText.Text = "Not Licensed";
                LicenseModeText.Foreground = new WpfBrush(red);
            }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_sessionSyncService == null || string.IsNullOrEmpty(_sessionId))
            {
                LastCheckedText.Text = "";
                return;
            }

            try
            {
                await _sessionSyncService.SendHeartbeatAsync(_sessionId);
                var response = _sessionSyncService.LastHeartbeatResponse;
                if (response != null)
                {
                    // Log heartbeat response for Device Status diagnostics
                    var rawBody = _sessionSyncService.LastHeartbeatResponseBody;
                    if (!string.IsNullOrEmpty(rawBody))
                        System.Diagnostics.Debug.WriteLine($"[DeviceStatus] Heartbeat response:\n{rawBody}");
                    try { var l = GetLogger(); l?.LogInfo($"[DeviceStatus] Heartbeat response body:\n{rawBody}"); l?.LogInfo($"[DeviceStatus] Parsed: licenseMode={response.LicenseMode}, sessions={response.ActiveSessionCount}/{response.MaxUsers}, isActive={response.IsActive}"); } catch { }

                    ApplyHeartbeatResponse(response);
                    LiveStatusResolved?.Invoke(response.IsActive, response.LicenseMode);
                }
                else
                    ApplyServerUnreachableStatus();
            }
            catch
            {
                ApplyServerUnreachableStatus();
            }
        }

        /// <summary>
        /// Repaints the status block as yellow ("can't verify with server right now") when
        /// the heartbeat fails. Previously only the small "Server unreachable" label updated
        /// while the big green circle + "Device Registered" title kept showing — misleading
        /// because the displayed state was the cached value from last successful check, not
        /// a live confirmation. Yellow signals "unverified" without overwriting the cached
        /// info underneath (StatusValue still reads "Registered"/"Not Registered" so the
        /// user knows the last known state). Only repaints colour fields touched by
        /// <see cref="ApplyCachedStatus"/> / <see cref="ApplyHeartbeatResponse"/> — every
        /// other piece of dialog content (Machine ID, Company, Role, Modules, Seats) is
        /// untouched because those don't depend on the live heartbeat.
        /// </summary>
        private void ApplyServerUnreachableStatus()
        {
            // Two cases the heartbeat-miss fallback has to cover:
            //
            //  (a) Cache says the device is REGISTERED — i.e. LicenseValidator already
            //      verified successfully at some prior point this session and the local
            //      state is healthy. The transient heartbeat miss (very common on Revit
            //      2022 / net48 where the offline-queue session confirmation lags a few
            //      seconds behind the dialog being opened) does NOT change the fact that
            //      the device is registered + licensed. Keep the main display green —
            //      previously we flipped everything to yellow "Cannot Verify" which made
            //      users with a perfectly valid license panic. Only surface the
            //      not-just-refreshed signal in the small LastCheckedText subtitle.
            //
            //  (b) Cache itself says NOT registered — we genuinely can't tell the user
            //      whether things are OK. Repaint the main display yellow with the
            //      "Cannot Verify" wording so they don't mistake "no cache" for "all good".
            if (_cachedIsRegistered)
            {
                LastCheckedText.Text = "Server unreachable — showing last verified state";
                return;
            }

            // Brighter "warning yellow" (Tailwind yellow-500). The previous shade
            // rgb(202,138,4) is yellow-600 which reads as dark amber/goldenrod — users
            // didn't recognise it as "yellow". rgb(234,179,8) keeps acceptable contrast
            // with the white StatusIcon while clearly reading as yellow on the white
            // dialog background.
            var yellow = WpfColor.FromRgb(234, 179, 8);
            StatusCircle.Background = new WpfBrush(yellow);
            StatusIcon.Text = "?";
            StatusTitle.Text = "Cannot Verify";
            StatusTitle.Foreground = new WpfBrush(yellow);
            // Keep StatusValue text as-is (cached "Registered" / "Not Registered") so the
            // user still sees the last known state, but tint it yellow to signal unverified.
            StatusValue.Foreground = new WpfBrush(yellow);
            LicenseModeText.Foreground = new WpfBrush(yellow);
            LastCheckedText.Text = "Server unreachable";
        }

        private void ApplyHeartbeatResponse(HeartbeatResponse response)
        {
            // A successful heartbeat response means the server recognized this device — it IS registered.
            // response.IsActive is the SESSION state (not device registration).
            var green = WpfColor.FromRgb(22, 163, 74);
            var yellow = WpfColor.FromRgb(202, 138, 4);
            var red = WpfColor.FromRgb(220, 38, 38);

            // A successful heartbeat = device is registered
            StatusCircle.Background = new WpfBrush(green);
            StatusIcon.Text = "\u2713";
            StatusTitle.Text = "Device Registered";
            StatusTitle.Foreground = new WpfBrush(green);
            StatusValue.Text = "Registered";
            StatusValue.Foreground = new WpfBrush(green);

            // License mode: 0=Breached, 1=Licensed, 2=Passive
            switch (response.LicenseMode)
            {
                case 1:
                    LicenseModeText.Text = "Licensed";
                    LicenseModeText.Foreground = new WpfBrush(green);
                    break;
                case 2:
                    LicenseModeText.Text = "Passive Mode";
                    LicenseModeText.Foreground = new WpfBrush(yellow);
                    StatusCircle.Background = new WpfBrush(yellow);
                    StatusIcon.Text = "~";
                    StatusTitle.Text = "Passive Mode";
                    StatusTitle.Foreground = new WpfBrush(yellow);
                    break;
                case 0: // Breached
                    LicenseModeText.Text = "License Breached";
                    LicenseModeText.Foreground = new WpfBrush(red);
                    StatusCircle.Background = new WpfBrush(red);
                    StatusIcon.Text = "!";
                    StatusTitle.Text = "License Breached";
                    StatusTitle.Foreground = new WpfBrush(red);
                    ReenterLicenseBtn.Visibility = System.Windows.Visibility.Visible;
                    break;
                default:
                    LicenseModeText.Text = "Unknown";
                    LicenseModeText.Foreground = new WpfBrush(yellow);
                    break;
            }

            // Seats
            if (response.MaxUsers > 0)
            {
                SeatsText.Text = $"{response.ActiveSessionCount} of {response.MaxUsers} seats in use";
                SeatsRow.Visibility = System.Windows.Visibility.Visible;
            }

            LastCheckedText.Text = $"Updated {DateTime.Now:HH:mm:ss}";
        }

        private void ReenterLicense_Click(object sender, RoutedEventArgs e)
        {
            Close();
            var licenseDialog = new LicenseKeyDialog(_machineId ?? "");
            new System.Windows.Interop.WindowInteropHelper(licenseDialog)
            {
                Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle
            };
            licenseDialog.ShowDialog();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();

        private global::BIManage.Infrastructure.Logging.ILogger? GetLogger()
        {
            try
            {
                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                return app?.GetType().GetProperty("Logger",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                    ?.GetValue(app) as global::BIManage.Infrastructure.Logging.ILogger;
            }
            catch { return null; }
        }
    }
}
