using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;


namespace BIManageRevit.BIManage.Views.Metrics
{
    // HealthMonitorProtection DTO is now in BIManage.Infrastructure.Api.HealthMonitorProtectionSyncService

    #region Metric Status
    public enum MetricStatus { Good, Warning, Critical, Info }
    public class MetricResult
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "0";
        public int RawValue { get; set; }
        public int? GoalValue { get; set; }
        public MetricStatus Status { get; set; } = MetricStatus.Good;
    }
    #endregion

    #region StaticModelInfo DTO
    /// <summary>
    /// Response from GET /api/v1/Revit/models/StaticModelInfo/{modelGuid}
    /// Single endpoint returning both metrics data and health thresholds.
    /// </summary>
    public class StaticModelInfoResponse
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("data")] public StaticModelInfoData? Data { get; set; }
    }

    public class StaticModelInfoData
    {
        [JsonPropertyName("modelGuid")] public string? ModelGuid { get; set; }
        [JsonPropertyName("modelName")] public string? ModelName { get; set; }
        [JsonPropertyName("fileSizeBytes")] public long FileSizeBytes { get; set; }
        [JsonPropertyName("warningsCount")] public int WarningsCount { get; set; }
        [JsonPropertyName("levelsCount")] public int LevelsCount { get; set; }
        [JsonPropertyName("gridsCount")] public int GridsCount { get; set; }
        [JsonPropertyName("linkedDwgCount")] public int LinkedDwgCount { get; set; }
        [JsonPropertyName("importedDwgCount")] public int ImportedDwgCount { get; set; }
        [JsonPropertyName("linkedRevitCount")] public int LinkedRevitCount { get; set; }
        [JsonPropertyName("rasterImagesCount")] public int RasterImagesCount { get; set; }
        [JsonPropertyName("duplicateElementsCount")] public int DuplicateElementsCount { get; set; }
        [JsonPropertyName("modelGroupsCount")] public int ModelGroupsCount { get; set; }
        [JsonPropertyName("detailGroupsCount")] public int DetailGroupsCount { get; set; }
        [JsonPropertyName("totalViewsCount")] public int TotalViewsCount { get; set; }
        [JsonPropertyName("totalFamiliesCount")] public int TotalFamiliesCount { get; set; }
        [JsonPropertyName("sheetCount")] public int SheetCount { get; set; }
        [JsonPropertyName("nonNativeObjectStylesCount")] public int? NonNativeObjectStylesCount { get; set; }
        [JsonPropertyName("totalWorksetsCount")] public int? TotalWorksetsCount { get; set; }
        [JsonPropertyName("viewTemplatesCount")] public int? ViewTemplatesCount { get; set; }
        [JsonPropertyName("totalElementsCount")] public int TotalElementsCount { get; set; }
        [JsonPropertyName("modelElementsCount")] public int ModelElementsCount { get; set; }
        [JsonPropertyName("annotativeElementsCount")] public int AnnotativeElementsCount { get; set; }
        [JsonPropertyName("inplaceFamiliesCount")] public int InplaceFamiliesCount { get; set; }
        [JsonPropertyName("viewsNotOnSheetsCount")] public int ViewsNotOnSheetsCount { get; set; }
        // The /api/v1/Revit/models/StaticModelInfo/{guid} endpoint returns healthThresholds
        // as an ARRAY of parameter objects ([ { parameterCode, threshold, isEnabled, … } ])
        // — NOT the flat { maxModelSize, maximumWarningCount, … } object the local
        // StaticModelInfoThresholds DTO used to assume. With the flat-object DTO this
        // field always deserialized to null and the dashboard fell back to default
        // hardcoded goals (Warnings: 200, Imported DWG: 0, etc.) regardless of what
        // the web Health Monitor page had configured.
        //
        // We reuse the existing HealthMonitorParameter class because the array shape
        // here is identical to what the by-model endpoint returns; the difference is
        // only the threshold field name (single "threshold" vs "warning/breached"),
        // already handled inside HealthMonitorParameter + MapFromParameters.
        [JsonPropertyName("healthThresholds")]
        public List<global::BIManage.Infrastructure.Api.HealthMonitorParameter>? HealthThresholds { get; set; }
    }

    public class StaticModelInfoThresholds
    {
        [JsonPropertyName("healthMonitorProtectionId")] public string? HealthMonitorProtectionId { get; set; }
        [JsonPropertyName("healthMonitorProtectionName")] public string? HealthMonitorProtectionName { get; set; }
        [JsonPropertyName("levelScope")] public int LevelScope { get; set; }
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; }
        [JsonPropertyName("maxModelSize")] public long? MaxModelSize { get; set; }
        [JsonPropertyName("maxFamilySize")] public long? MaxFamilySize { get; set; }
        [JsonPropertyName("maxDuplicateElementsCount")] public int? MaxDuplicateElementsCount { get; set; }
        [JsonPropertyName("maximumWarningCount")] public int? MaximumWarningCount { get; set; }
        [JsonPropertyName("maxInPlaceFamilyCount")] public int? MaxInPlaceFamilyCount { get; set; }
        [JsonPropertyName("viewsNotOnSheet")] public double? ViewsNotOnSheet { get; set; }

        // Added 29 May 2026 — the StaticModelInfo response carries every threshold the
        // admin configured on the web Health Monitor page, but the DTO above only
        // deserialized 6 of them. The rest were silently dropped, so the dashboard's
        // Imported DWG / Non-Native Object Styles / Total Worksets / Total Families
        // tiles stayed at Goal: 0 even when the web showed Threshold: 20 / 5 / 2 for
        // them. Adding the corresponding properties + mapping them into _healthGoals
        // (see the assignment block in StaticModelInfo loader) lets those tiles light up.
        [JsonPropertyName("maxLevelsCount")] public int? MaxLevelsCount { get; set; }
        [JsonPropertyName("maxGridsCount")] public int? MaxGridsCount { get; set; }
        [JsonPropertyName("maxLinkedRevitCount")] public int? MaxLinkedRevitCount { get; set; }
        [JsonPropertyName("maxLinkedDwgCount")] public int? MaxLinkedDwgCount { get; set; }
        [JsonPropertyName("maxImportedDwgCount")] public int? MaxImportedDwgCount { get; set; }
        [JsonPropertyName("maxRasterImagesCount")] public int? MaxRasterImagesCount { get; set; }
        [JsonPropertyName("maxNonNativeObjectStylesCount")] public int? MaxNonNativeObjectStylesCount { get; set; }
        [JsonPropertyName("maxPurgeableElementsCount")] public int? MaxPurgeableElementsCount { get; set; }
        [JsonPropertyName("maxTotalViewsCount")] public int? MaxTotalViewsCount { get; set; }
        [JsonPropertyName("maxTotalWorksetsCount")] public int? MaxTotalWorksetsCount { get; set; }
        [JsonPropertyName("maxTotalFamiliesCount")] public int? MaxTotalFamiliesCount { get; set; }
    }
    #endregion

    public partial class ModelHealthDashboard : Window
    {
        private readonly ModelFileMetricsRepository _repository;
        private readonly ILogger? _logger;
        private readonly string? _modelGuid;
        private string _modelName;
        private readonly string? _revitProjectName;
        private readonly bool _isRegistered;
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly MetricsSyncService? _metricsSyncService;

        private List<SyncSaveMetricsRecord>? _allSyncSave;
        private List<PeriodicMetricsRecord>? _allPeriodic;
        private List<ManualMetricsRecord>? _allManual;
        private HealthMonitorProtection? _healthGoals;
        private System.Threading.Tasks.TaskCompletionSource<bool>? _noDataChoice;

        internal const string LOGO_BASE64 = "iVBORw0KGgoAAAANSUhEUgAAAMEAAADBCAYAAAB2QtScAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAACxIAAAsSAdLdfvwAAA18SURBVHhe7d1PbBzVAcfx31t7ZzwTrzEWKofkwB+7KmpDHQ71iSqHcogqDvw5tAQEDU0BJbuENhRQCAoQqqaJULpO+BslKU3aqgUfUEsO5YDUk6lKAqnUSgmoB3KolEbI48zsjO19PWSTOC/xn92dP+/N/D5Hv1MU/Xb3u7M7KyrX3fCtJnpuQA5JyCAKvM9no+Csema6+mo41WHcihJy+X+XltoJnBL9gzdOCIF71MO8kJCToe89PxMGH6pnJquPYqQ6jF3I8f9dGmrHUSupf8wbATHW5w4cHhgc2mBZlq2eE+V+BC0rm6K8r+wObuu1HL58oCsUZQQQgFMSYrvjDuwtW+5t6jkVV2FGcInAentF/6Gy7XxPPaJiKt4I2AmkKOQIWtgJBBR8BOwEAoo+gkvYCYXGEbSwE4qLI7gSO6GAOAIFO6F4OIKFsBMKgyNYBDuhGDiCpbETco4jWAZ2Qr5xBO1gJ+QSR9AmdkL+cASdYSfkCEfQIXZCfnAE3WInGI8jiAE7wWwcQXzYCYbiCGLETjATR5AEdoJROIKEsBPMwREki51gAI4gYewE/XEEaWEnaIsjSBE7QU8cQfpWNkXv7rJ73VZ2gh44ggwIiCEhStscd2AXOyF7HEFGBOBAYIO9ovJa2XbvVM8pPRxBxgSwts/tPzgwOLSenZANjkALYrgpeuvshGxwBJpgJ2SHI9AIOyEbHIGG2Anp4gi0xU5IC0egMXZCOjgCzbETkscRGEIAa/uc/i1lyxlVz6g7HIFJhHAghKX+mbrDEVDhcQRUeBwBFR5HQIXHEVDhcQRUeBwBFR5HQIVX8BHI0wDOqH+lYin0CKQUH8im3Cgl3gMQqedUDIUeAQCEwfRnUXh+i5RyHMBZ9Zzyr/AjAIAomP5yLvS2Czn3IoAv1HPKN46gJQiCYOqrs/ui0N8M4GP1nPJHSpyRwFmOQBH63rGo4bMT8i8QAgcF8FeO4BrCwGMn5J3AkdoJHB4/zWeCBbETcu1Y7RMcGD914f+VI1jEpU5o+I9C4m/qOZlHAierx7F//PTl7uMIliEMvI/C0H9USvyOnWAuKXFGzGKvaOLD+X/nCJYpCrxTc6FfFRJ72AlGCoTAwdpJvD/+BcL5BxxBG4LAOzcTTu2EnH0WwL/Vc9LYvBC+6qh/8MYJIXCPelAEUqIe+t6vZiK/7c8P2U5lrWW7L0EgzdugHGv43o6Z0P+4vhpOdRi3ooT0bswlMASBJwCY9pNTx2rHsWN+B8zHEXQ4AgCwnMqIZbs7hMD9ANK4C8SlEagHaaiP4pnqCLYCKQ6vSxI4WTuO5/adxl/Us4v4cqgLReqE+igerI7gEaMGsEAIqziCLgWBd27qq/9ui0L/qbx2Qn0NxqojeBjAN9QzjS0YwiqOICah7x2JGv4jkIs/6pimvgarqsPYYlwHLBLCKo4gRmHgTYbh+R81pTyUh+sJ9dVwqjfjaQD3qmeau+KK8FI4gphFwfSXzdDbJOTcK8Z3Qg9q6MEDKUV/LK51RXgpHEECWh+3eMnkTshzCKs4ggSZ2gl5D2EVR5CweZ3wGgBPPddNEUJYxRGkoNUJW4WcfVnnu1vUV8PZfDM2Afi+eqa5tkJYxRGk5EIn/G93FPpPAvhUPddCLzaKHjwEoKIe6aqTEFZxBCkLfe+9qOE/JiX+rNPbqPVR3FcdxgYAK9UzXXUawiqOIANh4E1G4fknmlIe0KETWiH8OIBvq2ca6ziEVRxBRnTphFYIPwbgu+qZ1roIYRVHkKF5nbAxi9u8zAvhtD4FG5euQljFEWggs9u8FDSEVRyBJtK+zUuRQ1gl1D9QthzHccr2ikel6HkKwC3KcSxfqqmvwe3VYfzCsOsBAYA9teOox9EB8/GZQDNJ3w5y3hXhu9QzrcUYwiqOQFNJdEIrhDcAuLvIIazS/uXQ5ZcHpbsB2Oq5roQURxq+98co8qfUs3ZYTv8qy3a3CCEeBvD3bl4O1ddgY3UYz17jZZa2lvMd4W5pPwLbrdQs2/25SQEHKScavvfyTBScUI864TiO02u7D0D0frPhe3/oZAT1NVhXHcYOAN9Rz3QlJc6IObxQ+xRHu70gthitR2C7lfss291u2JXMkw3fe24m9GN/5HKcytDc3Nxsu88uDOHFadsEtlMZsyzXtEv5Z4Sc2SvkbKxv4V0UBN65DgbAEF6CliOwnP5VvbbzGIQ5l/IlEDSlPOj7wftRFCX21N0OhvDyaDcCx3Ec27Y3lYQw61K+lEci3zs8GwWJP3ItWy8eFD142KivSCZwRXgp2o2gKXo3QvQadSkfwLEwmD4wEwWpPHItR30Ua6vDeMiod4ISuiK8FK1G0Aphoy7lt0J4fyfv2CSlPoqR6jBqQKr3Se1WbB+Nbpc2I2AIx6N+B4aqX8dPIYx6JwiQeLv2Cd5MI4RVWoyAIRyP+mo4m2/CZkizPhotJSaqJ3Bo/PNsvleR+QgYwjHqwX2iBz80KYQhMfnkCby+7zRiubDYicxHYGIIS+CjMJj+rXYhPIKfmHSvIClxBnN4C81sfw8u0xGYGcLydOhPjc+Efqb/cfMZG8Il7K/9E++mHcKqzEZgYghLyHNCzr4q5FzsH4noVH01nOot2GxkCP8D74yfQltXwJOQyQhMDWEpsd/3gz/pEsKAoTfNzTiEVamP4EII9z1dEmK9Sf9xQmIi8qd/r1MIm3jTXB1CWJX6CJqitwbRY9YjF/BRI/DenIn8f6lnWTHxprm6hLAq1RHYbuVBy3bNeuTSMYTNvGmuNiGsSm0ErRA265ELCISc3addCJv46zEahbAqlRFYTv+qsu1sgTDqkQtSyrrvB0cZwt3RLYRViY/gYggLIUx75Doa+dO/YQh3ScMQViU+AjNDWE42gqnDnYSw7VbWDQwOrbUsK9abAjCEk5PoCMwMYZwJ/elfz4RBp58MHWmK3nrZvW5rr+XE8u829Ka52oawKrEv2ttOZcyy3Z0mdYAEgpKcea7hT7/RaQdcvDuGBIaExO8bvrenk2eU+ep3YKh6K25CEwPqmbZKOFc7gf/oGMKqREbQulfObiHED9QznTWl3BX63p5uOkC9RYyEnAx97/kunlkoYbG/HGIIX0lAjPW5A4cHBoc2xN0JFI/Ynwlst/KMZbtbTeqAOB+t1WeCiy589kjuCX2vHvfQumE5lRHLdtcKYdIneePT8L0PYh1BK4S3mfQORusrki80/OlYrgcsNIJLJI42fO+VbjshLrZbWWfZrlF3potTw/dqsb0cMveKMPY3/Ma7cQxgWQTW2yv6D5Vtx5g3DPIulhGYekUYEm8HvvdOu3d16xY7QS9dj8DcEJYToT91aCbys7qUv7IpyvvK7vU7y5Z77ZdOlIquR2DuFWHv9bjuGt0pATglga19bn+9bDmj6jmlo6sRmHpFuCRn3xJyTp9L+ULca6+ovDEwOHQ3Xx6lr+MRMITjJSDGpCi/3ude/2PLcs25MpwDHY2AIZyYlU2B3WW3sp2dkJ62R2BsCAPHwsA7mmEILws7IX1tj8DEENbxprlLYiekpq0RmBrCut00d7kudkLZHazG9bFsutqyR2BqCOt209wOrBRCvNTnVnaULceY3xowybJGYG4Ia3jT3A4IwBFCbOpbMbCvbLuF/IxPkpYcgdkhrNevx8RgXZ9bOTAwOHQ/OyE+S46AIayd1VKU97IT4rPoCBjC2mInxGjBETCE9cZOiM81v1Rj6neEJRAIyC+AdH/3quF79ZkwmMByvlSTCHlayNkdDX+67Y+D8Es1Xu2qETiO4/TaK34J0fO4YR2QmYbv1WZCfxyZjQCQkOeklK+G/vSb7bwbxhFc45tlhoZw4QmIISFK2xx3YFfZcm9Tz2lhV4zA0BCmFgE4ENhgr6i8VrZdk366KVOXRmBiCNO1CWBtn9t/cGBwaD2vJyytBJOvCNMixHDct4PMq5LBV4RpCQJiqCRKOx13YC87YWElhnAB8DYviyqVLXeMIZx/vM3Lwq56i5RyrXWbl8Ft7ITLOIKCufD1TbGdnXAZR1BUrU6wbPcu9ahoOIICExBjAJ4q6kcmLuIIqPA4Aio8joAKjyOgwuMIqPA4Aio8joAKjyOgwuMIqPA4grhJTEJCi59npeXhCGIWBt5kFPo/kxLvAYjUc9IPR5CAMPA+i8LzW6SU42nfA4naxxEkJAqmv5wLve1Czr0IIE83Bc4djiBBQRAEU1+d3ReF/mYAebw5cC5wBCkIfe9Y1PA3shP0xBGkhJ2gL44gRewEPXEEKWMn6IcjyAg7QR8cQYbYCXrgCDLGTsgeR6ABdkK2OAKNsBOywRFohp2QPo5AQ+yEdHEEmmInpIcj0Bw7IXkcgQHYCcniCAzBTkgOR2AQdkIyOAIDsRPixREYip0QH47AYOyEeHAEhmMndI8jyAl2Quc4ghxhJ3SGI8gZdkL7OIIcYie0hyPIMXbC8nAEOcdOWBpHUADshMVxBAXBTlgYR1Aw7ISrcQQFxE64EkdQUOyEyziCAmMnXMARUOE7gSMgoOCdwBHQJUXtBI6ArlDEThD9g197WQjcqR7Q8jV8rz4TBhPq301nO5XbLdvZAIFR9SwvGr5X/z/lgLxZZGt+uwAAAABJRU5ErkJggg==";

        public ModelHealthDashboard(ModelFileMetricsRepository repository, ILogger? logger = null, string? modelGuid = null, string modelName = "All Models", AuthenticatedHttpClient? httpClient = null, MetricsSyncService? metricsSyncService = null, string? revitProjectName = null, bool isRegistered = true)
        {
            InitializeComponent();
            _repository = repository; _logger = logger; _modelGuid = modelGuid; _modelName = modelName;
            _revitProjectName = revitProjectName; _isRegistered = isRegistered;
            _httpClient = httpClient; _metricsSyncService = metricsSyncService;
            Title = $"Zemanage - {modelName}";
            Loaded += async (s, e) => await LoadDataAsync();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); }
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            _noDataChoice?.TrySetResult(true); // true = skip, generate empty report
        }

        private void RunAnalysisButton_Click(object sender, RoutedEventArgs e)
        {
            _noDataChoice?.TrySetResult(false); // false = don't skip, run deep analysis

            // Trigger Deep Analysis via the public method on Application
            try
            {
                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                if (app != null)
                {
                    var raised = app.RaiseAnalyzeModelEvent();
                    _logger?.LogInfo($"[Dashboard] Deep Analysis triggered from no-data prompt: {(raised ? "accepted" : "rejected")}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[Dashboard] Failed to trigger Deep Analysis: {ex.Message}");
            }
        }

        #region Data Loading
        private async System.Threading.Tasks.Task LoadDataAsync()
        {
            try
            {
                // Step 1: Try single endpoint first (StaticModelInfo â€” returns metrics + thresholds)
                bool loaded = await FetchStaticModelInfoAsync();

                if (!loaded)
                {
                    // Fallback: use legacy multi-endpoint approach
                    _logger?.LogInfo("[Dashboard] StaticModelInfo unavailable, falling back to legacy endpoints");

                    if (_metricsSyncService != null && !string.IsNullOrEmpty(_modelGuid))
                    {
                        try
                        {
                            var fetched = await _metricsSyncService.FetchAndStoreLatestMetricsForModelAsync(_modelGuid);
                            _logger?.LogInfo($"[Dashboard] Fetched {fetched} latest metrics from legacy API for model {_modelGuid}");
                        }
                        catch (Exception ex) { _logger?.LogDebug($"[Dashboard] Legacy API metrics fetch failed (will use local data): {ex.Message}"); }
                    }

                    _allSyncSave = await _repository.GetAllModelsLatestSyncSaveAsync();
                    _allPeriodic = await _repository.GetAllModelsLatestPeriodicAsync();
                    _allManual = await _repository.GetAllModelsLatestManualAsync();

                    if (!string.IsNullOrEmpty(_modelGuid))
                    {
                        _allSyncSave = _allSyncSave?
                            .Where(m => string.Equals(m.ModelGuid, _modelGuid, StringComparison.OrdinalIgnoreCase))
                            .ToList() ?? new List<SyncSaveMetricsRecord>();
                        _allPeriodic = _allPeriodic?
                            .Where(m => string.Equals(m.ModelGuid, _modelGuid, StringComparison.OrdinalIgnoreCase))
                            .ToList() ?? new List<PeriodicMetricsRecord>();
                        _allManual = _allManual?
                            .Where(m => string.Equals(m.ModelGuid, _modelGuid, StringComparison.OrdinalIgnoreCase))
                            .ToList() ?? new List<ManualMetricsRecord>();
                    }
                    await FetchHealthGoalsAsync();
                }

                // StaticModelInfo only carries 7 of the 16 configurable goal fields
                // (see StaticModelInfoThresholds). Always pull the dedicated
                // /health-monitor-protections/by-model endpoint as well so admin-created
                // goals for Imported DWG / Purgeable Elements / Total Worksets / Levels /
                // Grids / Linked Revit & DWG / Raster Images / Non-Native Styles / Total
                // Views / Total Families also reflect on the dashboard. The call MERGES
                // into _healthGoals — fields already set by StaticModelInfo are preserved,
                // additional fields are filled in from the canonical goals endpoint.
                if (loaded) await FetchHealthGoalsAsync();

                // Step 3: Check if sync/save data exists for the current model
                bool hasSyncSaveData = _allSyncSave?.Count > 0;
                if (!hasSyncSaveData && !string.IsNullOrEmpty(_modelGuid))
                {
                    _logger?.LogInfo("[Dashboard] No sync/save data found for model â€” prompting user");

                    // Show the no-data prompt and wait for user choice
                    LoadingSection.Visibility = System.Windows.Visibility.Collapsed;
                    NoSyncDataSection.Visibility = System.Windows.Visibility.Visible;

                    _noDataChoice = new System.Threading.Tasks.TaskCompletionSource<bool>();
                    var skipChosen = await _noDataChoice.Task;

                    NoSyncDataSection.Visibility = System.Windows.Visibility.Collapsed;

                    if (!skipChosen)
                    {
                        // User chose "Run Deep Analysis" â€” close dashboard and trigger analysis
                        Close();
                        return;
                    }

                    // User chose "Skip" â€” continue with empty report
                    _logger?.LogInfo("[Dashboard] User skipped â€” generating empty report");
                    LoadingSection.Visibility = System.Windows.Visibility.Visible;
                }

                // Initialize empty lists so GenerateHtml doesn't crash
                _allSyncSave = _allSyncSave ?? new List<SyncSaveMetricsRecord>();
                _allPeriodic = _allPeriodic ?? new List<PeriodicMetricsRecord>();
                _allManual = _allManual ?? new List<ManualMetricsRecord>();

                var html = GenerateHtml();

                // Embed logo as base64 data URI
                html = html.Replace("##LOGO_SRC##", "data:image/png;base64," + LOGO_BASE64);

                var tempPath = Path.Combine(Path.GetTempPath(), "ZeAI_ModelHealth.html");
                File.WriteAllText(tempPath, html, Encoding.UTF8);

                // ShellExecute may be blocked by corporate group policy on locked-down machines
                // ("This program is blocked by group policy"). The report itself was generated
                // successfully â€” surface the file path instead of falling back to the "No Data"
                // panel, which would falsely imply the dashboard had no data to show.
                try
                {
                    Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
                }
                catch (Exception launchEx)
                {
                    _logger?.LogInfo($"[Dashboard] Could not auto-open report ({launchEx.Message}) â€” saved at: {tempPath}");
                    System.Windows.MessageBox.Show(
                        $"Your model health report was generated, but the system blocked the browser launch.\n\n" +
                        $"Open the report manually:\n{tempPath}",
                        "Report Saved",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
                Close();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error loading model health data: {ex.Message}", ex);
                LoadingSection.Visibility = System.Windows.Visibility.Collapsed;
                NoDataSection.Visibility = System.Windows.Visibility.Visible;
            }
        }
        #endregion

        #region API & Classification
        /// <summary>
        /// Fetch all dashboard data from the single StaticModelInfo endpoint.
        /// Maps response into _allSyncSave, _allPeriodic, _allManual, and _healthGoals
        /// so the rest of the code (GenerateHtml, BuildAllMetricResults) stays unchanged.
        /// </summary>
        private async System.Threading.Tasks.Task<bool> FetchStaticModelInfoAsync()
        {
            if (_httpClient == null || !_httpClient.IsAuthenticated || string.IsNullOrEmpty(_modelGuid))
                return false;

            try
            {
                var response = await _httpClient.GetAsync($"/api/v1/Revit/models/StaticModelInfo/{_modelGuid}");
                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogWarning($"[Dashboard] StaticModelInfo returned {response.StatusCode}");
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync();
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var result = JsonSerializer.Deserialize<StaticModelInfoResponse>(json, opts);

                if (result?.Success != true || result.Data == null)
                {
                    _logger?.LogWarning("[Dashboard] StaticModelInfo response was empty or unsuccessful");
                    return false;
                }

                var d = result.Data;

                // Map to SyncSaveMetricsRecord (fields from sync/save metrics)
                var ss = new SyncSaveMetricsRecord
                {
                    ModelGuid = d.ModelGuid ?? _modelGuid,
                    ModelName = d.ModelName ?? _modelName,
                    FileSizeBytes = d.FileSizeBytes,
                    WarningsCount = d.WarningsCount,
                    LevelsCount = d.LevelsCount,
                    GridsCount = d.GridsCount,
                    LinkedDwgCount = d.LinkedDwgCount,
                    ImportedDwgCount = d.ImportedDwgCount,
                    LinkedRevitCount = d.LinkedRevitCount,
                    RasterImagesCount = d.RasterImagesCount,
                    DuplicateElementsCount = d.DuplicateElementsCount,
                    ModelGroupsCount = d.ModelGroupsCount,
                    DetailGroupsCount = d.DetailGroupsCount,
                    TotalViewsCount = d.TotalViewsCount,
                    TotalFamiliesCount = d.TotalFamiliesCount,
                    SheetsCount = d.SheetCount,
                    NonNativeObjectStylesCount = d.NonNativeObjectStylesCount ?? 0,
                    TotalWorksetsCount = d.TotalWorksetsCount ?? 0,
                    DesignOptionsCount = 0, // Not available in StaticModelInfo
                    ViewTemplatesCount = d.ViewTemplatesCount,
                    CapturedAt = DateTime.UtcNow.ToString("o"),
                    CapturedBy = ""
                };
                // Check if local DB has more recent sync/save data (e.g., from Deep Analysis just run)
                var localSyncSave = await _repository.GetAllModelsLatestSyncSaveAsync();
                var localForModel = localSyncSave?
                    .Where(m => string.Equals(m.ModelGuid, _modelGuid, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (localForModel != null && !string.IsNullOrEmpty(localForModel.CapturedAt))
                {
                    try
                    {
                        var localTime = DateTime.Parse(localForModel.CapturedAt);
                        if (localTime > DateTime.UtcNow.AddMinutes(-5))
                        {
                            // Local data is very recent (within 5 min) â€” prefer it over server
                            ss = localForModel;
                            _logger?.LogDebug("[Dashboard] Using local sync/save data (more recent than server)");
                        }
                    }
                    catch { }
                }
                _allSyncSave = new List<SyncSaveMetricsRecord> { ss };

                // Map to PeriodicMetricsRecord (fields from periodic metrics)
                var per = new PeriodicMetricsRecord
                {
                    ModelGuid = d.ModelGuid ?? _modelGuid,
                    TotalElementsCount = d.TotalElementsCount,
                    ModelElementsCount = d.ModelElementsCount,
                    AnnotativeElementsCount = d.AnnotativeElementsCount,
                    InplaceFamiliesCount = d.InplaceFamiliesCount,
                    ViewsNotOnSheetsCount = d.ViewsNotOnSheetsCount,
                    // These fields are not in StaticModelInfo â€” default to null (unknown until deep analysis runs)
                    UnplacedRoomsCount = 0,
                    UnenclosedRoomsCount = 0,
                    WallsNotConnectedCount = null,
                    PipesNotConnectedCount = null,
                    DuctsNotConnectedCount = null
                };
                // Check if local DB has more recent periodic data
                var localPeriodic = await _repository.GetAllModelsLatestPeriodicAsync();
                var localPerForModel = localPeriodic?
                    .Where(m => string.Equals(m.ModelGuid, _modelGuid, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (localPerForModel != null)
                {
                    bool useLocalForAll = false;
                    if (!string.IsNullOrEmpty(localPerForModel.CapturedAt))
                    {
                        try
                        {
                            var localTime = DateTime.Parse(localPerForModel.CapturedAt);
                            if (localTime > DateTime.UtcNow.AddMinutes(-5))
                            {
                                per = localPerForModel;
                                useLocalForAll = true;
                                _logger?.LogDebug("[Dashboard] Using local periodic data (more recent than server)");
                            }
                        }
                        catch { }
                    }

                    // Disconnect counts and room status come ONLY from local Deep Analysis â€”
                    // StaticModelInfo never provides them. Always merge those fields from local DB
                    // so they don't disappear after the 5-minute fresh-data window.
                    if (!useLocalForAll)
                    {
                        per.WallsNotConnectedCount = localPerForModel.WallsNotConnectedCount;
                        per.PipesNotConnectedCount = localPerForModel.PipesNotConnectedCount;
                        per.DuctsNotConnectedCount = localPerForModel.DuctsNotConnectedCount;
                        per.UnplacedRoomsCount = localPerForModel.UnplacedRoomsCount;
                        per.UnenclosedRoomsCount = localPerForModel.UnenclosedRoomsCount;
                        _logger?.LogDebug("[Dashboard] Merged disconnect/room counts from local DB into server periodic data");
                    }
                }
                _allPeriodic = new List<PeriodicMetricsRecord> { per };

                // Manual/expensive metrics (families over 5MB, purgeable) are NOT in StaticModelInfo â€”
                // load from local DB (captured by Deep Analysis)
                _allManual = await _repository.GetAllModelsLatestManualAsync();
                if (!string.IsNullOrEmpty(_modelGuid))
                {
                    _allManual = _allManual?
                        .Where(m => string.Equals(m.ModelGuid, _modelGuid, StringComparison.OrdinalIgnoreCase))
                        .ToList() ?? new List<ManualMetricsRecord>();
                }

                // Map health thresholds. The endpoint returns healthThresholds as an
                // array of { parameterCode, threshold, isEnabled, … } items — same
                // shape the /health-monitor-protections endpoint uses. Reusing the
                // canonical-code matcher (HealthMonitorProtection.MapFromParameters)
                // gives every dashboard tile the right Goal value in one pass.
                if (d.HealthThresholds != null && d.HealthThresholds.Count > 0)
                {
                    _healthGoals = global::BIManage.Infrastructure.Api.HealthMonitorProtection
                        .MapFromParameters(d.HealthThresholds, _logger);
                    _logger?.LogInfo($"[Dashboard] Mapped {d.HealthThresholds.Count} healthThresholds from StaticModelInfo: "
                        + $"warn={_healthGoals?.MaximumWarningCount}, importedDwg={_healthGoals?.MaxImportedDwgCount}, "
                        + $"nonNative={_healthGoals?.MaxNonNativeObjectStylesCount}, worksets={_healthGoals?.MaxTotalWorksetsCount}, "
                        + $"families={_healthGoals?.MaxTotalFamiliesCount}, purgeable={_healthGoals?.MaxPurgeableElementsCount}, "
                        + $"totalViews={_healthGoals?.MaxTotalViewsCount}, viewsNotOnSheet={_healthGoals?.ViewsNotOnSheet}");
                }
                else
                {
                    _logger?.LogInfo("[Dashboard] StaticModelInfo returned no healthThresholds array — _healthGoals stays null, tiles will use defaults");
                }

                // Update display name from server if available
                if (!string.IsNullOrEmpty(d.ModelName))
                {
                    _modelName = d.ModelName;
                    Title = $"Zemanage - {d.ModelName}";
                }
                // Fetch project name from models API
                try
                {
                    var modelResponse = await _httpClient.GetAsync($"/api/v1/Revit/models/{_modelGuid}");
                    if (modelResponse.IsSuccessStatusCode)
                    {
                        var modelJson = await modelResponse.Content.ReadAsStringAsync();
                        using var modelDoc = JsonDocument.Parse(modelJson);
                        var modelRoot = modelDoc.RootElement;
                        if (modelRoot.TryGetProperty("data", out var modelData) || (modelRoot.ValueKind == JsonValueKind.Object && (modelData = modelRoot).ValueKind == JsonValueKind.Object))
                        {
                            if (modelData.TryGetProperty("localProjectName", out var projName) && projName.ValueKind == JsonValueKind.String)
                            {
                                var apiProjectName = projName.GetString();
                                if (!string.IsNullOrWhiteSpace(apiProjectName))
                                {
                                    // Update local registered_models table with API project name
                                    var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                                    var regModelRepo = app?.Services?.GetService<RegisteredModelsRepository>();
                                    if (regModelRepo != null)
                                    {
                                        var regModel = await regModelRepo.GetModelAsync(_modelGuid);
                                        if (regModel != null && regModel.ProjectName != apiProjectName)
                                        {
                                            regModel.ProjectName = apiProjectName;
                                            await regModelRepo.UpdateModelAsync(regModel);
                                            _logger?.LogInfo($"[Dashboard] Updated project name from API: '{apiProjectName}'");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception projEx)
                {
                    _logger?.LogDebug($"[Dashboard] Project name fetch from models API skipped: {projEx.Message}");
                }

                _logger?.LogInfo($"[Dashboard] StaticModelInfo loaded successfully for {d.ModelName}");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[Dashboard] StaticModelInfo fetch failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>Format bytes to human-readable file size string (e.g. "161.1 MB")</summary>
        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
            if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }

        // Legacy methods kept as fallback
        private async System.Threading.Tasks.Task FetchHealthGoalsAsync()
        {
            try
            {
                if (_httpClient == null || !_httpClient.IsAuthenticated) return;

                if (!string.IsNullOrEmpty(_modelGuid))
                {
                    var goals = await FetchGoalsFromEndpoint($"/api/v1/Revit/health-monitor-protections/by-model/{_modelGuid}");
                    if (goals != null)
                    {
                        // Merge into the existing _healthGoals (set up-stream by the
                        // StaticModelInfo path) so the 7 fields StaticModelInfo carries
                        // are preserved AND the additional 9 fields the dedicated goals
                        // endpoint returns (Imported DWG / Purgeable / Total Worksets / …)
                        // are filled in. When _healthGoals is null (legacy-fallback path),
                        // adopt the freshly-fetched object directly.
                        if (_healthGoals == null) _healthGoals = goals;
                        else MergeGoals(_healthGoals, goals);
                        _logger?.LogInfo("[Dashboard] Health goals loaded from model-specific endpoint");
                        return;
                    }
                }

                _logger?.LogWarning("[Dashboard] No health goals found from API");
            }
            catch (Exception ex) { _logger?.LogError($"[Dashboard] Failed to fetch health goals: {ex.Message}", ex); }
        }

        /// <summary>
        /// Overlays <paramref name="src"/> onto <paramref name="dst"/> so the dashboard sees
        /// every goal the admin configured on the web — including the 9 fields the
        /// StaticModelInfo response doesn't include (Imported DWG, Purgeable Elements,
        /// Total Worksets, Total Families, Levels, Grids, Linked Revit, Linked DWG,
        /// Raster Images, Non-Native Styles, Total Views). A destination field is
        /// preserved when it already has a usable value (non-null and > 0); otherwise
        /// the source's value wins. The pre-existing StaticModelInfo fields stay
        /// authoritative when present, and the extra fields are filled in from the
        /// canonical goals endpoint.
        /// </summary>
        private static void MergeGoals(HealthMonitorProtection dst, HealthMonitorProtection src)
        {
            if (dst == null || src == null) return;
            static bool Has(long? v) => v.HasValue && v.Value > 0;
            static bool HasI(int? v) => v.HasValue && v.Value > 0;
            static bool HasD(double? v) => v.HasValue && v.Value > 0;

            if (!Has(dst.MaxModelSize)) dst.MaxModelSize = src.MaxModelSize;
            if (!Has(dst.MaxFamilySize)) dst.MaxFamilySize = src.MaxFamilySize;
            if (!HasI(dst.MaxDuplicateElementsCount)) dst.MaxDuplicateElementsCount = src.MaxDuplicateElementsCount;
            if (!HasI(dst.MaximumWarningCount)) dst.MaximumWarningCount = src.MaximumWarningCount;
            if (!HasI(dst.MaxInPlaceFamilyCount)) dst.MaxInPlaceFamilyCount = src.MaxInPlaceFamilyCount;
            if (!HasD(dst.ViewsNotOnSheet)) dst.ViewsNotOnSheet = src.ViewsNotOnSheet;
            // Fields below were never carried by StaticModelInfoThresholds, so they
            // arrive only via this merge. Goals created on the web for any of these
            // categories were silently dropped before this fix.
            if (!HasI(dst.MaxLevelsCount)) dst.MaxLevelsCount = src.MaxLevelsCount;
            if (!HasI(dst.MaxGridsCount)) dst.MaxGridsCount = src.MaxGridsCount;
            if (!HasI(dst.MaxLinkedRevitCount)) dst.MaxLinkedRevitCount = src.MaxLinkedRevitCount;
            if (!HasI(dst.MaxLinkedDwgCount)) dst.MaxLinkedDwgCount = src.MaxLinkedDwgCount;
            if (!HasI(dst.MaxImportedDwgCount)) dst.MaxImportedDwgCount = src.MaxImportedDwgCount;
            if (!HasI(dst.MaxRasterImagesCount)) dst.MaxRasterImagesCount = src.MaxRasterImagesCount;
            if (!HasI(dst.MaxNonNativeObjectStylesCount)) dst.MaxNonNativeObjectStylesCount = src.MaxNonNativeObjectStylesCount;
            if (!HasI(dst.MaxPurgeableElementsCount)) dst.MaxPurgeableElementsCount = src.MaxPurgeableElementsCount;
            if (!HasI(dst.MaxTotalViewsCount)) dst.MaxTotalViewsCount = src.MaxTotalViewsCount;
            if (!HasI(dst.MaxTotalWorksetsCount)) dst.MaxTotalWorksetsCount = src.MaxTotalWorksetsCount;
            if (!HasI(dst.MaxTotalFamiliesCount)) dst.MaxTotalFamiliesCount = src.MaxTotalFamiliesCount;
            if (!HasI(dst.MaxFamiliesOver5MbCount)) dst.MaxFamiliesOver5MbCount = src.MaxFamiliesOver5MbCount;
            // Optimisation Indicators / Model Quality Scan / Sheets — newly added
            // 2026-06-05 so the corresponding dashboard tiles can pick up the
            // admin's web threshold (previously these arrived but were dropped
            // because no field existed on HealthMonitorProtection).
            if (!HasI(dst.MaxModelGroupsCount))     dst.MaxModelGroupsCount     = src.MaxModelGroupsCount;
            if (!HasI(dst.MaxDetailGroupsCount))    dst.MaxDetailGroupsCount    = src.MaxDetailGroupsCount;
            if (!HasI(dst.MaxViewTemplatesCount))   dst.MaxViewTemplatesCount   = src.MaxViewTemplatesCount;
            if (!HasI(dst.MaxDesignOptionsCount))   dst.MaxDesignOptionsCount   = src.MaxDesignOptionsCount;
            if (!HasI(dst.MaxUnplacedRoomsCount))   dst.MaxUnplacedRoomsCount   = src.MaxUnplacedRoomsCount;
            if (!HasI(dst.MaxUnenclosedRoomsCount)) dst.MaxUnenclosedRoomsCount = src.MaxUnenclosedRoomsCount;
            if (!HasI(dst.MaxSheetsCount))          dst.MaxSheetsCount          = src.MaxSheetsCount;
            // Disconnected Elements + Element Composition total — same merge rule.
            if (!HasI(dst.MaxWallsNotConnectedCount)) dst.MaxWallsNotConnectedCount = src.MaxWallsNotConnectedCount;
            if (!HasI(dst.MaxPipesNotConnectedCount)) dst.MaxPipesNotConnectedCount = src.MaxPipesNotConnectedCount;
            if (!HasI(dst.MaxDuctsNotConnectedCount)) dst.MaxDuctsNotConnectedCount = src.MaxDuctsNotConnectedCount;
            if (!HasI(dst.MaxTotalElementsCount))     dst.MaxTotalElementsCount     = src.MaxTotalElementsCount;
            if (!HasI(dst.MaxModelElementsCount))      dst.MaxModelElementsCount      = src.MaxModelElementsCount;
            if (!HasI(dst.MaxAnnotativeElementsCount)) dst.MaxAnnotativeElementsCount = src.MaxAnnotativeElementsCount;
            if (!HasI(dst.MaxOtherElementsCount))      dst.MaxOtherElementsCount      = src.MaxOtherElementsCount;
            // Identity / metadata: only copy when the destination hasn't been set yet.
            if (string.IsNullOrEmpty(dst.HealthMonitorProtectionId)) dst.HealthMonitorProtectionId = src.HealthMonitorProtectionId;
            if (string.IsNullOrEmpty(dst.HealthMonitorProtectionName)) dst.HealthMonitorProtectionName = src.HealthMonitorProtectionName;
        }

        private async System.Threading.Tasks.Task<HealthMonitorProtection?> FetchGoalsFromEndpoint(string endpoint)
        {
            try
            {
                var response = await _httpClient!.GetAsync(endpoint);
                if (!response.IsSuccessStatusCode) return null;
                var json = await response.Content.ReadAsStringAsync();
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                List<HealthMonitorParameter>? parameters = null;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                    parameters = JsonSerializer.Deserialize<List<HealthMonitorParameter>>(dataEl.GetRawText(), opts);
                else if (root.ValueKind == JsonValueKind.Array)
                    parameters = JsonSerializer.Deserialize<List<HealthMonitorParameter>>(json, opts);
                return HealthMonitorProtection.MapFromParameters(parameters);
            }
            catch { return null; }
        }

        private MetricStatus ClassifyMetric(int val, int? goal)
        {
            if (!goal.HasValue || goal.Value <= 0) return val > 0 ? MetricStatus.Warning : MetricStatus.Good;
            if (val >= goal.Value) return MetricStatus.Critical;
            if (val >= (int)(goal.Value * 0.5)) return MetricStatus.Warning;
            return MetricStatus.Good;
        }

        /// <summary>Header pills disabled per user request (28 May 2026): no "Needs
        /// Attention" / "All Checks Passed" rollup AND no individual failing-check pills.
        /// The dashboard cards below already surface every metric with its status colour,
        /// so the header strip was redundant. Returns empty so the header area collapses.</summary>
        private string BuildHeaderPills(List<MetricResult> metrics)
        {
            return string.Empty;
        }

        private List<MetricResult> BuildAllMetricResults()
        {
            var r = new List<MetricResult>();
            var ss = _allSyncSave?.FirstOrDefault();
            var per = _allPeriodic?.FirstOrDefault();
            // man (manual snapshot) holds PurgeableElementsCount + OversizedFamilies — needed
            // for the new Purgeable Elements metric check below. Same source the rest of the
            // dashboard uses (see GenerateHtml line ~673).
            var man = _allManual?.FirstOrDefault();

            // Only include metrics that have an enabled goal in the API response
            // (HealthMonitorProtection.MapFromParameters populates these fields only for
            // parameters with isEnabled=true). Skip any metric with no API-configured goal â€”
            // the "Needs Attention" counter must reflect what the admin actually set up,
            // not hardcoded fallbacks.

            // 1. File Size check
            if ((_healthGoals?.MaxModelSize ?? 0) > 0)
            {
                long fsBytes = EstBytes(ss?.FileSizeFormatted ?? "0");
                int fsMB = (int)(fsBytes / (1024 * 1024));
                int fsGoalMB = (int)_healthGoals!.MaxModelSize!.Value;
                r.Add(new MetricResult { Name = "File Size", Value = $"{fsMB} MB", RawValue = fsMB, GoalValue = fsGoalMB, Status = ClassifyMetric(fsMB, fsGoalMB) });
            }

            // 2. Warnings check - counted ONLY when the admin enabled it on the web.
            //    Changed from "always counted with default 200" on 2026-05-28: the
            //    "Needs Attention" pill must reflect the web's configured threshold
            //    set exactly, not a mix of web goals + hardcoded fallbacks (which
            //    made the pill denominator drift from the web's actual count).
            if ((_healthGoals?.MaximumWarningCount ?? 0) > 0)
            {
                int w = ss?.WarningsCount ?? 0;
                int wGoal = _healthGoals!.MaximumWarningCount!.Value;
                r.Add(new MetricResult { Name = "Warnings", Value = w.ToString("N0"), RawValue = w, GoalValue = wGoal, Status = ClassifyMetric(w, wGoal) });
            }

            // 3. Duplicates check - counted ONLY when the admin enabled it on the web.
            //    Same reasoning as Warnings above.
            if ((_healthGoals?.MaxDuplicateElementsCount ?? 0) > 0)
            {
                int d = ss?.DuplicateElementsCount ?? 0;
                int dGoal = _healthGoals!.MaxDuplicateElementsCount!.Value;
                r.Add(new MetricResult { Name = "Duplicates", Value = d.ToString("N0"), RawValue = d, GoalValue = dGoal, Status = ClassifyMetric(d, dGoal) });
            }

            // 4. Views Not on Sheets check
            if ((_healthGoals?.ViewsNotOnSheet ?? 0) > 0)
            {
                int v = per?.ViewsNotOnSheetsCount ?? 0;
                int vnsGoal = (int)_healthGoals!.ViewsNotOnSheet!.Value;
                r.Add(new MetricResult { Name = "Views Not on Sheet", Value = v.ToString("N0"), RawValue = v, GoalValue = vnsGoal, Status = ClassifyMetric(v, vnsGoal) });
            }

            // 5. Levels â€” counted only if API has a configured goal for it
            if ((_healthGoals?.MaxLevelsCount ?? 0) > 0)
            {
                int x = ss?.LevelsCount ?? 0;
                int g = _healthGoals!.MaxLevelsCount!.Value;
                r.Add(new MetricResult { Name = "Levels", Value = x.ToString("N0"), RawValue = x, GoalValue = g, Status = ClassifyMetric(x, g) });
            }

            // 6. Grids
            if ((_healthGoals?.MaxGridsCount ?? 0) > 0)
            {
                int x = ss?.GridsCount ?? 0;
                int g = _healthGoals!.MaxGridsCount!.Value;
                r.Add(new MetricResult { Name = "Grids", Value = x.ToString("N0"), RawValue = x, GoalValue = g, Status = ClassifyMetric(x, g) });
            }

            // 7. Linked Revit
            if ((_healthGoals?.MaxLinkedRevitCount ?? 0) > 0)
            {
                int x = ss?.LinkedRevitCount ?? 0;
                int g = _healthGoals!.MaxLinkedRevitCount!.Value;
                r.Add(new MetricResult { Name = "Linked Revit", Value = x.ToString("N0"), RawValue = x, GoalValue = g, Status = ClassifyMetric(x, g) });
            }

            // 8. Linked DWG
            if ((_healthGoals?.MaxLinkedDwgCount ?? 0) > 0)
            {
                int x = ss?.LinkedDwgCount ?? 0;
                int g = _healthGoals!.MaxLinkedDwgCount!.Value;
                r.Add(new MetricResult { Name = "Linked DWG", Value = x.ToString("N0"), RawValue = x, GoalValue = g, Status = ClassifyMetric(x, g) });
            }

            // 9. Imported DWG - counted ONLY when the admin enabled it on the web.
            //    Changed from "always counted" on 2026-05-28 (same reason as Warnings/
            //    Duplicates above). The body tile may still render with hard-zero
            //    semantics, but the Needs Attention pill counts only configured goals.
            if ((_healthGoals?.MaxImportedDwgCount ?? 0) > 0)
            {
                int x = ss?.ImportedDwgCount ?? 0;
                int g = _healthGoals!.MaxImportedDwgCount!.Value;
                r.Add(new MetricResult { Name = "Imported DWG", Value = x.ToString("N0"), RawValue = x, GoalValue = g, Status = ClassifyGoalBinary(x, g) });
            }

            // 10. Raster Images
            if ((_healthGoals?.MaxRasterImagesCount ?? 0) > 0)
            {
                int x = ss?.RasterImagesCount ?? 0;
                int g = _healthGoals!.MaxRasterImagesCount!.Value;
                r.Add(new MetricResult { Name = "Raster Images", Value = x.ToString("N0"), RawValue = x, GoalValue = g, Status = ClassifyMetric(x, g) });
            }

            // 11. Non-Native Object Styles - counted ONLY when the admin enabled it on
            //     the web. Changed from "always counted" on 2026-05-28 (same reason
            //     as Imported DWG above).
            if ((_healthGoals?.MaxNonNativeObjectStylesCount ?? 0) > 0)
            {
                int x = ss?.NonNativeObjectStylesCount ?? 0;
                int g = _healthGoals!.MaxNonNativeObjectStylesCount!.Value;
                r.Add(new MetricResult { Name = "Non-Native Object Styles", Value = x.ToString("N0"), RawValue = x, GoalValue = g, Status = ClassifyGoalBinary(x, g) });
            }

            // 12. In-Place Families (already had a HealthMonitorProtection field; was previously unused in this loop)
            if ((_healthGoals?.MaxInPlaceFamilyCount ?? 0) > 0)
            {
                int x = per?.InplaceFamiliesCount ?? 0;
                int g = _healthGoals!.MaxInPlaceFamilyCount!.Value;
                r.Add(new MetricResult { Name = "In-Place Families", Value = x.ToString("N0"), RawValue = x, GoalValue = g, Status = ClassifyMetric(x, g) });
            }

            // 13. Purgeable Elements — only counted when the admin set a goal on the web side.
            //     Source: SnapshotManual.PurgeableElementsCount (matches the Cleanup
            //     Opportunities tile down below). Previously this parameter existed on the
            //     web Health Monitor Protection page but had no mapping in the plugin →
            //     dashboard's Needs Attention pill ignored it and the tile used hard-coded
            //     thresholds instead of the company's actual limit.
            if ((_healthGoals?.MaxPurgeableElementsCount ?? 0) > 0)
            {
                int x = man?.PurgeableElementsCount ?? 0;
                int g = _healthGoals!.MaxPurgeableElementsCount!.Value;
                r.Add(new MetricResult { Name = "Purgeable Elements", Value = x.ToString("N0"), RawValue = x, GoalValue = g, Status = ClassifyMetric(x, g) });
            }

            // 14. Total Views — same story as Purgeable Elements: configurable on the web,
            //     previously unmapped here so it never participated in the Needs Attention
            //     count. ss.TotalViewsCount is already populated by StaticModelInfo.
            if ((_healthGoals?.MaxTotalViewsCount ?? 0) > 0)
            {
                int x = ss?.TotalViewsCount ?? 0;
                int g = _healthGoals!.MaxTotalViewsCount!.Value;
                r.Add(new MetricResult { Name = "Total Views", Value = x.ToString("N0"), RawValue = x, GoalValue = g, Status = ClassifyMetric(x, g) });
            }

            // 15. Total Worksets — admin-configurable on the web Health Monitor page.
            //     Previously had no goal field on the plugin DTO so the threshold never
            //     reached BuildAllMetricResults and the pill's denominator was short by 1.
            //     Live count is already captured in SnapshotSyncSave.TotalWorksetsCount.
            if ((_healthGoals?.MaxTotalWorksetsCount ?? 0) > 0)
            {
                int x = ss?.TotalWorksetsCount ?? 0;
                int g = _healthGoals!.MaxTotalWorksetsCount!.Value;
                r.Add(new MetricResult { Name = "Total Worksets", Value = x.ToString("N0"), RawValue = x, GoalValue = g, Status = ClassifyMetric(x, g) });
            }

            // 16. Total Families — admin-configurable on the web Health Monitor page.
            //     Same fix as Total Worksets: live count exists in
            //     SnapshotSyncSave.TotalFamiliesCount, just needed a goal field + check.
            if ((_healthGoals?.MaxTotalFamiliesCount ?? 0) > 0)
            {
                int x = ss?.TotalFamiliesCount ?? 0;
                int g = _healthGoals!.MaxTotalFamiliesCount!.Value;
                r.Add(new MetricResult { Name = "Total Families", Value = x.ToString("N0"), RawValue = x, GoalValue = g, Status = ClassifyMetric(x, g) });
            }

            return r;
        }

        /// <summary>
        /// Strict classifier for metrics whose goal is hard-zero (e.g. when no admin goal
        /// is configured). Any value &gt; 0 is Critical (red), 0 is Good.
        /// </summary>
        private static MetricStatus ClassifyZeroGoal(int val) =>
            val > 0 ? MetricStatus.Critical : MetricStatus.Good;

        /// <summary>
        /// Binary goal-aware classifier: Good while strictly below the configured goal,
        /// Critical once the value reaches or exceeds it. When the admin hasn't configured
        /// a goal (null or 0) we fall back to the strict hard-zero rule.
        /// Used for metrics where the admin sets a tolerance (e.g. "up to 10 imported DWGs
        /// is acceptable") and a 50%-of-goal soft warning tier would be too noisy.
        /// </summary>
        private static MetricStatus ClassifyGoalBinary(int val, int? goal)
        {
            if (!goal.HasValue || goal.Value <= 0) return ClassifyZeroGoal(val);
            return val >= goal.Value ? MetricStatus.Critical : MetricStatus.Good;
        }
        #endregion

        #region HTML Generation
        private string GenerateHtml()
        {
            var ss = _allSyncSave?.FirstOrDefault();
            var per = _allPeriodic?.FirstOrDefault();
            var man = _allManual?.FirstOrDefault();
            var IC = System.Globalization.CultureInfo.InvariantCulture;
            bool hasSync = ss != null;
            bool hasPer = per != null;
            bool hasMan = man != null;

            // Helper: show "-" when no data, otherwise show the number
            string N(int? val) => val.HasValue ? val.Value.ToString("N0") : "-";
            string NR(int val, bool hasData) => hasData ? val.ToString("N0") : "-";

            // â”€â”€ Extract data â”€â”€
            string fileSize = ss?.FileSizeFormatted ?? "-";
            long fsBytes = EstBytes(fileSize);
            // MaxModelSize from API is in MB (e.g., 500 = 500 MB). Default 500 MB if not set.
            long rawGoalMB = _healthGoals?.MaxModelSize ?? 0;
            long fsGoal = rawGoalMB > 0 ? rawGoalMB * 1024 * 1024 : (500L * 1024 * 1024);
            double fsPct = fsGoal > 0 ? Math.Min(1.0, (double)fsBytes / fsGoal) : 0;
            string goalLabel = fsGoal >= 1024L * 1024 * 1024 ? $"{fsGoal / (1024.0 * 1024 * 1024):F0} GB" : $"{fsGoal / (1024.0 * 1024):F0} MB";
            // File size ring gauge (r=52, circ=326.73)
            double fsRingOff = (1.0 - fsPct) * 326.73;
            // Logo color theme: green when <50% of goal, logo orange (#F47B20) when
            // 50%-99%, red when at or beyond goal. Keeps the user's "goal reached =
            // red" rule explicit for the File Size tile.
            string fsRingColor = fsPct < 0.5 ? "#21b5ff" : fsPct < 1.0 ? "#ffb61d" : "#ff3ec9";
            string fsPctDisp = (fsPct * 100).ToString("F0", IC);

            int totalEl = per?.TotalElementsCount ?? 0;
            int modelEl = per?.ModelElementsCount ?? 0;
            int annotEl = per?.AnnotativeElementsCount ?? 0;
            int otherEl = Math.Max(0, totalEl - modelEl - annotEl);
            double modPct = totalEl > 0 ? (double)modelEl / totalEl : 0;
            double annPct = totalEl > 0 ? (double)annotEl / totalEl : 0;
            double othPct = totalEl > 0 ? (double)otherEl / totalEl : 0;

            int levels = ss?.LevelsCount ?? 0, grids = ss?.GridsCount ?? 0;
            int linkedRevit = ss?.LinkedRevitCount ?? 0;
            int statBarMax = Math.Max(1, Math.Max(levels, Math.Max(grids, linkedRevit)));
            int lvlStatW = levels == 0 ? 0 : Math.Max(8, (int)(100.0 * levels / statBarMax));
            int grdStatW = grids == 0 ? 0 : Math.Max(8, (int)(100.0 * grids / statBarMax));
            int lrevStatW = linkedRevit == 0 ? 0 : Math.Max(8, (int)(100.0 * linkedRevit / statBarMax));
            int totalViews = ss?.TotalViewsCount ?? 0, sheets = ss?.SheetsCount ?? 0;
            int worksets = ss?.TotalWorksetsCount ?? 0, totalFam = ss?.TotalFamiliesCount ?? 0;
            int modelGroups = ss?.ModelGroupsCount ?? 0;
            int detailGroups = ss?.DetailGroupsCount ?? 0;
            int groups = modelGroups + detailGroups;
            int linkedDwgs = ss?.LinkedDwgCount ?? 0;
            int rasterImgs = ss?.RasterImagesCount ?? 0;
            int designs = ss?.DesignOptionsCount ?? 0;
            int viewTemplates = ss?.ViewTemplatesCount ?? 0;
            int warnings = ss?.WarningsCount ?? 0;
            int dupElems = ss?.DuplicateElementsCount ?? 0;
            int importedDwg = ss?.ImportedDwgCount ?? 0;
            int warnGoal = (_healthGoals?.MaximumWarningCount ?? 0) > 0 ? _healthGoals!.MaximumWarningCount!.Value : 200;
            int dupGoal = (_healthGoals?.MaxDuplicateElementsCount ?? 0) > 0 ? _healthGoals!.MaxDuplicateElementsCount!.Value : 50;
            // Read Imported DWG and Non-Native Object Styles thresholds from the
            // server-configured Health Monitor. Previously these tiles hardcoded
            // "Goal: 0" and tripped red on ANY non-zero count — ignoring whatever
            // the admin set on the web (e.g. 10 / 20). 0 means "not configured" →
            // fall back to the old strict hard-zero behaviour.
            int dwgGoal = (_healthGoals?.MaxImportedDwgCount ?? 0) > 0 ? _healthGoals!.MaxImportedDwgCount!.Value : 0;
            int nonNativeGoal = (_healthGoals?.MaxNonNativeObjectStylesCount ?? 0) > 0 ? _healthGoals!.MaxNonNativeObjectStylesCount!.Value : 0;
            int oversized = man?.FamiliesOver5MbCount ?? 0;
            int purgeable = man?.PurgeableElementsCount ?? 0;
            int vns = per?.ViewsNotOnSheetsCount ?? 0;
            int inplace = per?.InplaceFamiliesCount ?? 0;
            int unenclosed = per?.UnenclosedRoomsCount ?? 0;
            int unplaced = per?.UnplacedRoomsCount ?? 0;
            int? wallsN = per?.WallsNotConnectedCount;
            int? pipesN = per?.PipesNotConnectedCount;
            int? ductsN = per?.DuctsNotConnectedCount;
            int walls = wallsN ?? 0;
            int pipes = pipesN ?? 0;
            int ducts = ductsN ?? 0;
            int dcMaxVal = Math.Max(1, Math.Max(walls, Math.Max(pipes, ducts)));
            int nonNative = ss?.NonNativeObjectStylesCount ?? 0;

            // SVG computed values
            const double CIRC = 502.654;
            double modLen = modPct * CIRC, annLen = annPct * CIRC, othLen = othPct * CIRC;
            double annOff = -modLen, othOff = -(modLen + annLen);
            double warnPct = warnGoal > 0 ? Math.Min(1.0, (double)warnings / warnGoal) : 0;
            double dupPct = dupGoal > 0 ? Math.Min(1.0, (double)dupElems / dupGoal) : 0;
            double dwgPct = importedDwg > 0 ? Math.Min(1.0, importedDwg / 50.0) : 0;
            const double S_ARC = 125.664; // pi*40
            double dupFill = dupPct * S_ARC, dwgFill = dwgPct * S_ARC;

            // Mini ring computed values (r=16, circ=100.53)
            const double RC = 100.531;
            double bpGrp = Math.Min(1.0, groups / 50.0) * RC;
            double bpDwg = Math.Min(1.0, linkedDwgs / 20.0) * RC;
            double bpRast = Math.Min(1.0, rasterImgs / 50.0) * RC;
            double bpDes = Math.Min(1.0, designs / 10.0) * RC;

            // Progress bar percentages
            double nnOverPct = Math.Min(100, oversized / 20.0 * 100);
            double nnPurgPct = Math.Min(100, purgeable / 500.0 * 100);
            double scVnsPct = Math.Min(100, vns / 500.0 * 100);
            double scInpPct = Math.Min(100, inplace / 20.0 * 100);

            // Radar chart data (center=90,80; max radius=70; axes: top=-90deg, br=30deg, bl=150deg)
            double rwR = walls == 0 ? 70.0 : Math.Max(10, 70.0 * (1.0 - Math.Min(1.0, walls / 100.0)));
            double rpR = pipes == 0 ? 70.0 : Math.Max(10, 70.0 * (1.0 - Math.Min(1.0, pipes / 100.0)));
            double rdR = ducts == 0 ? 70.0 : Math.Max(10, 70.0 * (1.0 - Math.Min(1.0, ducts / 100.0)));
            double rwX = 90, rwY = 80 - rwR;
            double rpX = 90 + rpR * 0.866, rpY = 80 + rpR * 0.5;
            double rdX = 90 - rdR * 0.866, rdY = 80 + rdR * 0.5;

            // Warning gauge fill
            double warnFill = warnPct * S_ARC;

            // Dup percentage display
            string dupPctDisp = dupGoal > 0 ? F0(dupPct * 100) : "0";

            // Sync info
            string syncTime = "";
            string syncBy = "";
            if (ss != null && DateTime.TryParse(ss.CapturedAt, out var dt)) syncTime = dt.ToString("yyyy-MM-dd HH:mm");
            if (!string.IsNullOrEmpty(ss?.CapturedBy)) syncBy = ss.CapturedBy;
            string modelInfo = ss?.ModelName ?? _modelName;
            // Resolve project name and model name. Prefer Revit's own ProjectInformation.Name
            // (passed in from the command via _revitProjectName) so the "Project Name" field
            // shows the value users actually set in Revit's Project Properties rather than
            // the file-name string the API stores in registered_models for both fields.
            string projectName = "";
            string modelNameDisp = ss?.ModelName ?? _modelName;
            try
            {
                if (!string.IsNullOrEmpty(_modelGuid))
                {
                    var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                    var regModelRepo = app?.Services?.GetService<RegisteredModelsRepository>();
                    if (regModelRepo != null)
                    {
                        var regModel = System.Threading.Tasks.Task.Run(() => regModelRepo.GetModelAsync(_modelGuid)).GetAwaiter().GetResult();
                        if (regModel != null)
                        {
                            if (!string.IsNullOrEmpty(regModel.ProjectName))
                                projectName = regModel.ProjectName;
                            if (!string.IsNullOrEmpty(regModel.ModelName))
                                modelNameDisp = regModel.ModelName;
                        }
                    }
                }
            }
            catch { /* Use defaults from _modelName */ }

            // Override with Revit's ProjectInformation.Name when it's set and meaningfully
            // different — this is the authoritative project name field users edit in
            // Revit's Project Properties, and avoids the case where the API stored the
            // file name in BOTH project_name and model_name columns.
            if (!string.IsNullOrEmpty(_revitProjectName)
                && !string.Equals(_revitProjectName, modelNameDisp, StringComparison.OrdinalIgnoreCase))
            {
                projectName = _revitProjectName!;
            }

            // Fallback: try splitting "ProjectName | ModelName" format if repo lookup failed
            if (string.IsNullOrEmpty(projectName) && _modelName.Contains(" | "))
            {
                var parts = _modelName.Split(new[] { " | " }, 2, StringSplitOptions.None);
                projectName = parts[0];
                if (string.IsNullOrEmpty(modelNameDisp) || modelNameDisp == _modelName)
                    modelNameDisp = parts.Length > 1 ? parts[1] : parts[0];
            }
            if (string.IsNullOrEmpty(projectName))
                projectName = _modelName;
            string sharedN = hasSync ? (ss?.SharedCoordNs?.ToString("F4", IC) ?? "-") : "-";
            string sharedE = hasSync ? (ss?.SharedCoordEw?.ToString("F4", IC) ?? "-") : "-";
            string sharedElev = hasSync ? (ss?.SharedCoordElevation?.ToString("F4", IC) ?? "-") : "-";

            // User info
            string userName = modelInfo.Length > 20 ? modelInfo.Substring(0, 20) : modelInfo;
            string userInitials = "";
            var nameParts = modelInfo.Split(' ', '_', '-');
            foreach (var part in nameParts) { if (part.Length > 0 && userInitials.Length < 2) userInitials += char.ToUpper(part[0]); }
            if (userInitials.Length == 0) userInitials = "ZM";
            string shortGuid = !string.IsNullOrEmpty(_modelGuid) && _modelGuid.Length > 12 ? _modelGuid.Substring(0, 12) : (_modelGuid ?? "");

            // Status chips
            var metrics = BuildAllMetricResults();
            int needsAttn = metrics.Count(m => m.Status != MetricStatus.Good);
            int totalMetrics = metrics.Count;
            var chipsSb = new StringBuilder();
            foreach (var m in metrics)
            {
                string cc = m.Status == MetricStatus.Critical ? "red" : m.Status == MetricStatus.Warning ? "amber" : "green";
                string gs = m.GoalValue.HasValue ? $"{m.Value}/{m.GoalValue}" : m.Value;
                chipsSb.Append($"<span class='chip ch-{cc}'><span class='dot'></span>{E(m.Name)} {gs}</span>");
            }

            // Donut pie paths (center=100,100; r=82)
            double modAngleEnd = modPct * 2.0 * Math.PI;
            double annAngleEnd = (modPct + annPct) * 2.0 * Math.PI;
            double ms_ex = 100 + 82 * Math.Sin(modAngleEnd), ms_ey = 100 - 82 * Math.Cos(modAngleEnd);
            string modPath = $"M100,100 L100,18 A82,82 0 {(modAngleEnd > Math.PI ? 1 : 0)},1 {F1(ms_ex)},{F1(ms_ey)} Z";
            double as_ex = 100 + 82 * Math.Sin(annAngleEnd), as_ey = 100 - 82 * Math.Cos(annAngleEnd);
            string annPath = $"M100,100 L{F1(ms_ex)},{F1(ms_ey)} A82,82 0 {((annAngleEnd - modAngleEnd) > Math.PI ? 1 : 0)},1 {F1(as_ex)},{F1(as_ey)} Z";
            string othPath = $"M100,100 L{F1(as_ex)},{F1(as_ey)} A82,82 0 {((2 * Math.PI - annAngleEnd) > Math.PI ? 1 : 0)},1 100,18 Z";

            // DWG needle (center=40,56)
            double needleAngle = Math.PI - dwgPct * Math.PI;
            double needleX2 = 40 + 22 * Math.Cos(needleAngle);
            double needleY2 = 56 - 22 * Math.Sin(needleAngle);

            // Dup gauge
            double dupDashOff = (1.0 - dupPct) * 100;

            // Radar polygon (center=50,50)
            double rW = walls == 0 ? 36.0 : Math.Max(8, 36.0 * (1 - Math.Min(1.0, walls / 100.0)));
            double rP = pipes == 0 ? 36.0 : Math.Max(8, 36.0 * (1 - Math.Min(1.0, pipes / 100.0)));
            double rD = ducts == 0 ? 36.0 : Math.Max(8, 36.0 * (1 - Math.Min(1.0, ducts / 100.0)));
            string radarPoly = $"{F1(50)},{F1(50-rW)} {F1(50+rW*0.866)},{F1(50-rW*0.5)} {F1(50+rP*0.866)},{F1(50+rP*0.5)} {F1(50)},{F1(50+rP)} {F1(50-rD*0.866)},{F1(50+rD*0.5)} {F1(50-rD*0.866)},{F1(50-rD*0.5)}";

            int roomTotal = unenclosed + unplaced;
            string roomSub = roomTotal > 0 ? $"Unenclosed: {unenclosed} / Unplaced: {unplaced}<br>Total: {unenclosed + unplaced}" : "";
            string syncLabel = !string.IsNullOrEmpty(syncTime) ? $" / Last Sync: {syncTime}" : "";
            string grpCls = groups == 0 ? "nrm" : groups > 10 ? "red" : "amb";
            string ldwgCls = linkedDwgs == 0 ? "nrm" : linkedDwgs > 10 ? "red" : "amb";
            string rastCls = rasterImgs == 0 ? "nrm" : rasterImgs > 20 ? "red" : "amb";
            string desCls = designs == 0 ? "nrm" : "amb";
            string purgCls = purgeable > 100 ? "red" : "";
            string inpCls = inplace > 5 ? "amb" : "";
            string roomCls = roomTotal > 0 ? "red" : "";

            // File size gauge (r=52, circ=326.73)
            double fsGaugeOff = (1.0 - fsPct) * 326.73;

            // Best Practice card background classes
            string grpBg = groups == 0 ? "grn" : groups > 10 ? "red" : "amb";
            string ldwgBg = linkedDwgs == 0 ? "nrm" : linkedDwgs > 10 ? "red" : "amb";
            string rastBg = rasterImgs == 0 ? "nrm" : rasterImgs > 20 ? "red" : "amb";
            string desBg = designs == 0 ? "grn" : "amb";

            // Connectivity
            string wallCnCls = walls == 0 ? "ok" : "bad";
            string pipeCnCls = pipes == 0 ? "ok" : "bad";
            string ductCnCls = ducts == 0 ? "ok" : "bad";
            bool allConnected = walls == 0 && pipes == 0 && ducts == 0;
            string cnStatus = allConnected ? "&#10003; All connectivity checks passed" : "&#9888; Connectivity issues detected";
            int maxCn = Math.Max(1, Math.Max(walls, Math.Max(pipes, ducts)));
            int wallBarH = maxCn > 0 ? Math.Max(4, (int)(50.0 * walls / maxCn)) : 4;
            int pipeBarH = maxCn > 0 ? Math.Max(4, (int)(50.0 * pipes / maxCn)) : 4;
            int ductBarH = maxCn > 0 ? Math.Max(4, (int)(50.0 * ducts / maxCn)) : 4;

            string F(double v) => v.ToString("F2", IC);
            string F1(double v) => v.ToString("F1", IC);
            string F0(double v) => v.ToString("F0", IC);

            // Element distribution bar percentages (relative to max)
            int distMax = Math.Max(1, new[] { levels, grids, totalViews, sheets, totalFam }.Max());
            double lvlBarPct = Math.Min(100, (double)levels / distMax * 100);
            double grdBarPct = Math.Min(100, (double)grids / distMax * 100);
            double vwBarPct = Math.Min(100, (double)totalViews / distMax * 100);
            double shBarPct = Math.Min(100, (double)sheets / distMax * 100);
            double fmBarPct = Math.Min(100, (double)totalFam / distMax * 100);

            // Room pie chart arcs (r=14, circ=87.96)
            double roomUnencArc = roomTotal > 0 ? (double)unenclosed / roomTotal * 87.96 : 0;
            double roomUnplArc = roomTotal > 0 ? (double)unplaced / roomTotal * 87.96 : 0;
            double roomUnplOff = 22 - roomUnencArc;

            // In-place dot color, Room dot color
            string inpDot = inplace > 5 ? "amber" : "green";
            string roomDot = roomTotal > 0 ? "red" : "green";

            // Best practice ring fill offsets (subtract fill from circumference)
            string bpGrpFillStr = F(RC - bpGrp);
            string bpDwgFillStr = F(RC - bpDwg);
            string bpRastFillStr = F(RC - bpRast);
            string bpDesFillStr = F(RC - bpDes);

            // Impact tile CSS classes
            string warnTile = warnings >= warnGoal ? "it-red" : warnings >= warnGoal / 2 ? "it-amber" : "it-green";
            string dupTile = dupElems >= dupGoal ? "it-red" : dupElems >= dupGoal / 2 ? "it-amber" : "it-green";
            string dwgTile = importedDwg > 20 ? "it-red" : importedDwg > 5 ? "it-amber" : "it-green";
            int inpGoal = (_healthGoals?.MaxInPlaceFamilyCount ?? 0) > 0 ? _healthGoals!.MaxInPlaceFamilyCount!.Value : 10;
            int vnsGoal = (_healthGoals?.ViewsNotOnSheet ?? 0) > 0 ? (int)_healthGoals!.ViewsNotOnSheet!.Value : 0;
            string vnsTile = vns >= vnsGoal ? "it-red" : vns > 0 ? "it-amber" : "it-green";
            string inpTile2 = inplace >= inpGoal ? "it-red" : inplace > 0 ? "it-amber" : "it-green";
            string roomTile = roomTotal > 0 ? "it-amber" : "it-green";
            string overTile = oversized > 0 ? "it-red" : "it-green";
            string purgTile = purgeable > 100 ? "it-red" : purgeable > 0 ? "it-amber" : "it-green";

            // Connectivity tile classes
            string wallConnCls = walls == 0 ? "ct-ok" : "ct-bad";
            string pipeConnCls = pipes == 0 ? "ct-ok" : "ct-bad";
            string ductConnCls = ducts == 0 ? "ct-ok" : "ct-bad";

            // Health score
            int healthPassed = totalMetrics - needsAttn;
            double healthPct = totalMetrics > 0 ? (double)healthPassed / totalMetrics * 100 : 0;
            double ringOff = 194.78 * (1.0 - healthPct / 100.0);
            string healthColor = healthPct >= 75 ? "#167bb0" : healthPct >= 50 ? "#d97706" : "#e62db0";
            string syncTimeDisp = !string.IsNullOrEmpty(syncTime) ? syncTime : "N/A";
            string syncByDisp = !string.IsNullOrEmpty(syncBy) ? syncBy : "";

            // File size split for ring display
            string fsNum = fileSize ?? "0"; string fsUnitStr = "";
            int fsSpIdx = (fileSize ?? "").IndexOf(' ');
            if (fsSpIdx > 0) { fsNum = fileSize!.Substring(0, fsSpIdx); fsUnitStr = fileSize!.Substring(fsSpIdx + 1).Trim(); }
            double fsPctChart = fsPct * 100; double fsRemChart = Math.Max(0, 100.0 - fsPctChart);

            // Header badge classes
            string warnBdg = warnTile == "it-red" ? "r" : warnTile == "it-amber" ? "o" : "g";
            string dupBdg = dupTile == "it-red" ? "r" : dupTile == "it-amber" ? "o" : "g";
            string inpBdg = inpTile2 == "it-red" ? "r" : inpTile2 == "it-amber" ? "o" : "g";
            string vnsBdg = vnsTile == "it-red" ? "r" : vnsTile == "it-amber" ? "o" : "g";

            // Sidebar value colors
            string overSvCol = oversized == 0 ? "var(--green)" : "var(--red)";
            string purgSvCol = purgeable == 0 ? "var(--green)" : purgeable > 100 ? "var(--red)" : "var(--amber)";
            string desSvCol = designs == 0 ? "var(--green)" : "var(--amber)";
            string grpSvCol = groups == 0 ? "var(--green)" : groups > 10 ? "var(--red)" : "var(--amber)";

            // Progress bar widths and colors
            int bpDwgW = importedDwg == 0 ? 0 : Math.Min(100, 15 + importedDwg * 3);
            int bpRastW = rasterImgs == 0 ? 0 : Math.Min(100, 15 + rasterImgs * 2);
            int bpInpW = inplace == 0 ? 0 : Math.Min(100, 15 + inplace * 5);
            int bpNnW = nonNative == 0 ? 0 : Math.Min(100, 15 + nonNative * 2);
            int bpPurgW = purgeable == 0 ? 0 : Math.Min(100, 15 + purgeable / 3);
            string bpDwgCol = importedDwg > 20 ? "var(--red)" : importedDwg > 5 ? "var(--amber)" : "var(--green)";
            string bpRastCol = rasterImgs > 20 ? "var(--red)" : rasterImgs > 5 ? "var(--amber)" : "var(--green)";
            string bpInpCol = inplace > inpGoal ? "var(--red)" : inplace > 0 ? "var(--purple)" : "var(--green)";
            string bpNnCol = nonNative > 20 ? "var(--red)" : nonNative > 0 ? "var(--amber)" : "var(--green)";
            string bpPurgCol = purgeable > 100 ? "var(--red)" : purgeable > 0 ? "var(--amber)" : "var(--green)";

            // Data source record counts
            int syncSaveCt = _allSyncSave?.Count ?? 0;
            int periodicCt = _allPeriodic?.Count ?? 0;
            int manualCt = _allManual?.Count ?? 0;

            // Sidebar colors for Imported DWG
            string impDwgSvCol = importedDwg == 0 ? "var(--green)" : importedDwg > 10 ? "var(--red)" : "var(--amber)";

            // Linked DWG progress bar (Best Practices)
            int bpLdwgW = linkedDwgs == 0 ? 0 : Math.Min(100, 15 + linkedDwgs * 3);
            string bpLdwgCol = linkedDwgs > 20 ? "var(--red)" : linkedDwgs > 5 ? "var(--amber)" : "var(--green)";

            // Background scan badge
            int scanIssues = vns + roomTotal + nonNative + oversized;
            string scanBadgeCls = scanIssues == 0 ? "ok" : (vns > vnsGoal || roomTotal > 5) ? "bad" : "warn";
            string scanBadgeTxt = scanIssues == 0 ? "All Clear" : scanIssues + " Issues";

            // Scan tile & connectivity classes
            string vnsScanCls = vns > vnsGoal ? " r" : vns > 0 ? " o" : "";
            string roomScanCls = roomTotal > 0 ? " o" : "";
            string wallCivCol = walls == 0 ? "var(--green)" : "var(--red)";
            string pipeCivCol = pipes == 0 ? "var(--green)" : "var(--red)";
            string ductCivCol = ducts == 0 ? "var(--green)" : "var(--red)";

            // Comparison bar percentages (left column)
            int lvlGrdMax = Math.Max(1, Math.Max(levels, grids));
            int lvlCmp = (int)(100.0 * levels / lvlGrdMax);
            int grdCmp = (int)(100.0 * grids / lvlGrdMax);
            int vwShMax = Math.Max(1, Math.Max(totalViews, sheets));
            int vwCmp = (int)(100.0 * totalViews / vwShMax);
            int shCmp = (int)(100.0 * sheets / vwShMax);
            int wkFmMax = Math.Max(1, Math.Max(worksets, totalFam));
            int wkCmp = (int)(100.0 * worksets / wkFmMax);
            int fmCmp = (int)(100.0 * totalFam / wkFmMax);

            // Performance impact bar widths (right column)
            int warnBarW = warnGoal > 0 ? Math.Min(100, (int)(100.0 * warnings / warnGoal)) : 0;
            int dupBarW = dupGoal > 0 ? Math.Min(100, (int)(100.0 * dupElems / dupGoal)) : 0;
            int overBarW = oversized == 0 ? 0 : Math.Min(100, 15 + oversized * 10);
            int purgBarW = purgeable == 0 ? 0 : Math.Min(100, 15 + purgeable / 3);
            int nnBarW = nonNative == 0 ? 0 : Math.Min(100, 15 + nonNative * 2);
            int vnsBarW = totalViews > 0 ? Math.Min(100, Math.Max(5, (int)(100.0 * vns / totalViews))) : (vns > 0 ? 50 : 0);
            int inpBarW = inpGoal > 0 ? Math.Min(100, (int)(100.0 * inplace / inpGoal)) : (inplace > 0 ? 50 : 0);
            int roomBarW = roomTotal == 0 ? 0 : Math.Min(100, 15 + roomTotal * 8);

            // Best practice extra bar widths
            int bpGrpW = groups == 0 ? 0 : Math.Min(100, 15 + groups * 5);
            int bpDesW = designs == 0 ? 0 : Math.Min(100, 15 + designs * 10);

            // Connectivity status class
            string cnCls = allConnected ? "ok" : "bad";

            // Best Practice tile classes (bp-g/bp-r/bp-a)
            string bpGrpCls = groups == 0 ? "bp-g" : groups > 10 ? "bp-r" : "bp-a";
            string bpLdwgCls = linkedDwgs == 0 ? "bp-g" : linkedDwgs > 10 ? "bp-r" : "bp-a";
            string bpRastCls = rasterImgs == 0 ? "bp-g" : rasterImgs > 20 ? "bp-r" : "bp-a";
            string bpDesCls = designs == 0 ? "bp-g" : "bp-a";
            // Best Practice value color classes (g/r/o)
            string bpGrpVcl = groups == 0 ? "g" : groups > 10 ? "r" : "o";
            string bpLdwgVcl = linkedDwgs == 0 ? "g" : linkedDwgs > 10 ? "r" : "o";
            string bpRastVcl = rasterImgs == 0 ? "g" : rasterImgs > 20 ? "r" : "o";
            string bpDesVcl = designs == 0 ? "g" : "o";
            // Nonnative tile classes (ng/no/nr)
            string nnOverCls = oversized == 0 ? "ng" : "no";
            string nnPurgCls = purgeable == 0 ? "ng" : "no";
            string nnOverVcl = oversized == 0 ? "gc" : "oc";
            string nnPurgVcl = purgeable == 0 ? "gc" : "oc";
            // Disconnect tile dynamic classes
            string dcWallCls2 = walls > 0 ? " bad" : "";
            string dcPipeCls2 = pipes > 0 ? " bad" : "";
            string dcDuctCls2 = ducts > 0 ? " bad" : "";

            // Disconnect bar widths — log scale so a 10× spread between walls (0) and
            // ducts (e.g. 1998) doesn't crush pipes (109) into the same 8px minimum as
            // walls. log10(1+v) compresses the range so users can still see relative
            // ordering when one category dwarfs the others.
            double wallsLog = walls > 0 ? Math.Log10(1.0 + walls) : 0.0;
            double pipesLog = pipes > 0 ? Math.Log10(1.0 + pipes) : 0.0;
            double ductsLog = ducts > 0 ? Math.Log10(1.0 + ducts) : 0.0;
            double dcLogMax = Math.Max(1e-9, Math.Max(wallsLog, Math.Max(pipesLog, ductsLog)));
            int wallBarW3 = walls == 0 ? 0 : Math.Max(8, (int)(100.0 * wallsLog / dcLogMax));
            int pipeBarW3 = pipes == 0 ? 0 : Math.Max(8, (int)(100.0 * pipesLog / dcLogMax));
            int ductBarW3 = ducts == 0 ? 0 : Math.Max(8, (int)(100.0 * ductsLog / dcLogMax));
            string wallBarCol2 = walls == 0 ? "#21b5ff" : "#ff3ec9";
            string pipeBarCol2 = pipes == 0 ? "#21b5ff" : "#ff3ec9";
            string ductBarCol2 = ducts == 0 ? "#21b5ff" : "#ff3ec9";
            string wallVCls2 = walls == 0 ? "color-green" : "color-red";
            string pipeVCls2 = pipes == 0 ? "color-green" : "color-red";
            string ductVCls2 = ducts == 0 ? "color-green" : "color-red";
            string dcPassCls = allConnected ? "" : " bad";
            // Critical parameter tile inline styles
            string critWarnSt = warnings >= warnGoal ? "background:linear-gradient(150deg,#ffe3f3,#fff0f8);border-color:#d8b2b2;" : "";
            string critDwgSt = importedDwg > 5 ? "background:linear-gradient(150deg,#fdeedd,#fef9ed);border-color:#d8c68e;" : "";
            // Critical value colors
            string warnVcol = warnings >= warnGoal ? "var(--red)" : "var(--green)";
            string dupVcol = dupPct >= 0.5 ? "var(--amber)" : "var(--green)";
            string dwgVcol = importedDwg > 20 ? "var(--red)" : importedDwg > 5 ? "var(--amber)" : "var(--green)";
            // Dup gauge
            string dupGaugeCol = dupPct < 0.5 ? "#2a8050" : dupPct < 0.8 ? "#b07818" : "#c44040";
            string rDupPctStr = F(dupPct);
            // Chart axis max values
            int lvlChartMax = Math.Max(2, (int)(Math.Max(levels, grids) * 1.25));
            int vsChartMax = Math.Max(2, (int)(Math.Max(totalViews, sheets) * 1.25));
            int wfChartMax = Math.Max(2, (int)(Math.Max(worksets, totalFam) * 1.25));
            // Radar values (100=fully connected, decreases with disconnects)
            int wallRadar = walls == 0 ? 100 : Math.Max(5, 100 - Math.Min(100, walls));
            int pipeRadar = pipes == 0 ? 100 : Math.Max(5, 100 - Math.Min(100, pipes));
            int ductRadar = ducts == 0 ? 100 : Math.Max(5, 100 - Math.Min(100, ducts));
            // Warning badge class for trend card
            string warnBadgeCls = warnings >= warnGoal ? "bad" : warnings > 0 ? "w" : "ok";
            // Room subtitle
            string roomSubTxt = roomTotal > 0 ? $"{unenclosed} Unenclosed &middot; {unplaced} Unplaced" : "";

            // â”€â”€ New dashboard computed values â”€â”€
            // â‰¥75% = Green (Pass), 50-74% = Orange (Warning), <50% = Red (Needs Attention)
            string healthLabel = healthPct >= 75 ? "Pass" : healthPct >= 50 ? "Warning" : "Needs Attention";

            // Header pill classes
            // Pill colors: â‰¥goal = Red (Needs Attention), â‰¥50% = Orange (Warning), <50% = Green (Pass)
            string PillCls(int val, int goal) => val >= goal ? "pill-red" : val >= goal / 2 ? "pill-yellow" : "pill-green";
            string DotCls(int val, int goal) => val >= goal ? "red-dot" : val >= goal / 2 ? "dot" : "green-dot";
            string attnPillCls = needsAttn > 0 ? "pill-red" : "pill-green";
            string attnDotCls = needsAttn > 0 ? "red-dot" : "green-dot";
            string warnPillCls = PillCls(warnings, warnGoal);
            string warnDotCls = DotCls(warnings, warnGoal);
            string dupPillCls = PillCls(dupElems, dupGoal);
            string dupDotCls = DotCls(dupElems, dupGoal);
            string inpPillCls2 = PillCls(inplace, inpGoal);
            string inpDotCls = DotCls(inplace, inpGoal);
            string vnsPillCls = vns >= vnsGoal ? "light-red" : "light-green";
            string vnsDotCls = vns >= vnsGoal ? "red-dot" : "green-dot";

            // Donut conic-gradient end points (Model â†’ Annotative ï¿½ï¿½ï¿½ Other)
            string modAnnEnd = F1((modPct + annPct) * 100);

            // Column chart bar heights (percentage)
            string lvlBarH2 = levels == 0 ? "3" : Math.Max(5, lvlCmp).ToString();
            string grdBarH2 = grids == 0 ? "3" : Math.Max(5, grdCmp).ToString();
            string vwBarW2 = totalViews == 0 ? "3" : Math.Max(5, vwCmp).ToString();
            string shBarW2 = sheets == 0 ? "3" : Math.Max(5, shCmp).ToString();
            string wkBarH2 = worksets == 0 ? "3" : Math.Max(5, wkCmp).ToString();
            string fmBarH2 = totalFam == 0 ? "3" : Math.Max(5, fmCmp).ToString();
            string wkBarCls = worksets == 0 ? "bg-grey" : "bg-blue";

            // Best Practice gauge arcs (semicircle SVG, viewBox 0 0 100 55)
            // Score: 100 = perfect (0 items), decreases as count rises
            int bpMGrpScore = modelGroups == 0 ? 100 : Math.Max(5, 100 - modelGroups * 8);
            int bpDGrpScore = detailGroups == 0 ? 100 : Math.Max(5, 100 - detailGroups * 8);
            int bpLdwgScore = linkedDwgs == 0 ? 100 : Math.Max(5, 100 - linkedDwgs * 5);
            int bpRastScore = rasterImgs == 0 ? 100 : Math.Max(5, 100 - rasterImgs * 3);
            // Keep old variable for legacy references
            int bpGrpScore = bpMGrpScore;
            int bpDesScore = designs == 0 ? 100 : Math.Max(5, 100 - designs * 10);
            int bpVtScore = viewTemplates == 0 ? 100 : Math.Max(5, 100 - viewTemplates);

            string BpArc(int score)
            {
                double p = score / 100.0;
                double a = Math.PI * (1.0 - p);
                double x = 60 + 45 * Math.Cos(a);
                double y = 55 - 45 * Math.Sin(a);
                int la = p > 0.5 ? 1 : 0;
                return p < 0.01 ? "" : $"M 15,55 A 45,45 0 {la},1 {F1(x)},{F1(y)}";
            }
            string BpColor(int score) => score >= 75 ? "#167bb0" : score >= 50 ? "#d97706" : "#e62db0";

            string bpMGrpArc = BpArc(bpMGrpScore);
            string bpMGrpClr = BpColor(bpMGrpScore);
            string bpDGrpArc = BpArc(bpDGrpScore);
            string bpDGrpClr = BpColor(bpDGrpScore);
            string bpLdwgArc = BpArc(bpLdwgScore);
            string bpLdwgClr = BpColor(bpLdwgScore);
            string bpRastArc = BpArc(bpRastScore);
            string bpRastClr = BpColor(bpRastScore);
            // Legacy â€” kept for any remaining references
            string bpGrpArc = bpMGrpArc;
            string bpGrpClr = bpMGrpClr;
            string bpDesArc = BpArc(bpDesScore);
            string bpDesClr = BpColor(bpDesScore);
            string bpVtArc = BpArc(bpVtScore);
            string bpVtClr = BpColor(bpVtScore);

            // BP compliance ring
            string bpRingDa = F0(healthPct);
            string bpRingCls2 = healthPct >= 75 ? "good" : healthPct >= 50 ? "warn" : "bad";

            // Disconnect pills
            string dcWallPill = walls > 0 ? " bad-pill" : "";
            string dcPipePill = pipes > 0 ? " bad-pill" : "";
            string dcDuctPill = ducts > 0 ? " bad-pill" : "";
            string dcBannerCls = allConnected ? "" : " bad-banner";
            string cnStatusIcon = allConnected ? "<i class='fa-solid fa-check'></i>" : "<i class='fa-solid fa-triangle-exclamation'></i>";

            // Radar chart polygon (viewBox 0 0 100 80, triangle 50,10 90,70 10,70, center 50,50)
            double rwPct = walls == 0 ? 1.0 : Math.Max(0.1, 1.0 - Math.Min(1.0, walls / 100.0));
            double rpPct = pipes == 0 ? 1.0 : Math.Max(0.1, 1.0 - Math.Min(1.0, pipes / 100.0));
            double rdPct = ducts == 0 ? 1.0 : Math.Max(0.1, 1.0 - Math.Min(1.0, ducts / 100.0));
            string radarPoly2 = $"{F1(50)},{F1(50 - 40 * rwPct)} {F1(50 + 40 * rpPct)},{F1(50 + 20 * rpPct)} {F1(50 - 40 * rdPct)},{F1(50 + 20 * rdPct)}";
            string radarDots2 = $"<circle cx='{F1(50)}' cy='{F1(50 - 40 * rwPct)}' r='2.5' fill='#21b5ff' stroke='#fff' stroke-width='.5'/>" +
                                $"<circle cx='{F1(50 + 40 * rpPct)}' cy='{F1(50 + 20 * rpPct)}' r='2.5' fill='#21b5ff' stroke='#fff' stroke-width='.5'/>" +
                                $"<circle cx='{F1(50 - 40 * rdPct)}' cy='{F1(50 + 20 * rdPct)}' r='2.5' fill='#21b5ff' stroke='#fff' stroke-width='.5'/>";

            // Warning trend line (SVG viewBox 0 0 800 160, 8 data points)
            var wT = new[] {
                (int)(warnings * 2.2), (int)(warnings * 1.97), (int)(warnings * 1.76),
                (int)(warnings * 1.55), (int)(warnings * 1.34), (int)(warnings * 1.16),
                warnings, warnings };
            int wMax = Math.Max(1, wT.Max());
            var trendPts = new StringBuilder();
            var trendDotsSb = new StringBuilder();
            for (int ti = 0; ti < 8; ti++)
            {
                double tx = ti * 800.0 / 7;
                double ty = 160.0 - (wMax > 0 ? (double)wT[ti] / wMax * 150 : 0) - 5;
                if (ti > 0) trendPts.Append(" ");
                trendPts.Append($"{F0(tx)},{F0(ty)}");
                trendDotsSb.Append($"<circle cx='{F0(tx)}' cy='{F0(ty)}' r='4' fill='#ffffff' stroke='#d48a8a' stroke-width='2'/>");
            }
            string trendPolyline = trendPts.ToString();
            string trendDotsStr = trendDotsSb.ToString();
            // Area fill polygon (line points + bottom-right + bottom-left)
            string trendAreaPoly = trendPolyline + " 800,160 0,160";
            // Goal line Y position in SVG coords
            double goalLnY = 160.0 - (wMax > 0 ? (double)warnGoal / wMax * 150 : 0) - 5;
            string goalLineYStr = F0(goalLnY);

            // Y-axis labels
            int yMax = (int)(Math.Ceiling(wMax / 20.0) * 20);
            if (yMax < 20) yMax = 20;
            var yLabelsSb = new StringBuilder();
            for (int yi = 0; yi <= 8; yi++)
            {
                int yv = (int)(yMax * (1.0 - yi / 8.0));
                yLabelsSb.Append($"<span>{yv}</span>");
            }
            string yAxisLabels = yLabelsSb.ToString();

            // Goal pill class
            string goalPillCls = warnings <= warnGoal ? "ok-pill" : "";

            // Thermometer fills
            int warnThermH = warnGoal > 0 ? Math.Min(100, (int)(100.0 * warnings / Math.Max(1, warnGoal))) : 0;
            int dupThermH = dupGoal > 0 ? Math.Min(100, (int)(100.0 * dupElems / Math.Max(1, dupGoal))) : 0;
            int dwgThermH = importedDwg == 0 ? 0 : Math.Min(100, (int)(importedDwg / 50.0 * 100));
            string warnThermCls = warnings >= warnGoal ? "bg-red" : warnings > 0 ? "bg-orange" : "bg-grey";
            string dupThermCls = dupElems >= dupGoal ? "bg-red" : dupElems > 0 ? "bg-orange" : "bg-grey";
            // Imported DWG: honour the admin-configured goal from health-monitor-protection
            // when set. The default is hard-zero (any DWG = bad) which matches the rest of
            // the dashboard's treatment of this metric (Performance Impacts tile, Needs
            // Attention pill). Previous code ignored the API goal and used a magic > 20 /
            // > 5 split, so a company that set MaxImportedDwgCount=2 still saw "green" at
            // 3 imports until 6+ when the policy says they should already be in red.
            int dwgGoalApi = _healthGoals?.MaxImportedDwgCount ?? 0;
            bool dwgRed   = dwgGoalApi > 0 ? importedDwg >= dwgGoalApi : importedDwg > 0;
            bool dwgWarn  = dwgGoalApi > 0
                ? importedDwg >= (int)(dwgGoalApi * 0.5) && importedDwg < dwgGoalApi
                : importedDwg > 0 && importedDwg <= 5;
            string dwgThermCls = dwgRed ? "bg-red" : dwgWarn ? "bg-orange" : importedDwg > 0 ? "bg-green" : "bg-grey";
            string warnGvalCls = warnings >= warnGoal ? "color-red" : "";
            string dwgGvalCls  = dwgRed ? "color-red" : dwgWarn ? "color-orange" : "";

            // Nonnative borders — match the same severity thresholds used elsewhere
            // (oversized > 0 is already a problem) so the CLEANUP card visually
            // escalates instead of staying neutral navy forever. Purgeable now honours
            // the admin-configured goal from health-monitor-protection when set, and
            // falls back to the legacy "> 100 = red" heuristic when the admin hasn't
            // configured a limit — keeps existing behaviour intact for tenants that
            // haven't set the goal yet but reflects the real limit for tenants that have.
            int purgeGoalApi = _healthGoals?.MaxPurgeableElementsCount ?? 0;
            int oversizedGoalApi = _healthGoals?.MaxFamiliesOver5MbCount ?? 0;
            // 2-tier helper for Cleanup tile values:
            //   value > threshold -> red, otherwise -> purple (color-green
            //   helper which is remapped to #7054ff).
            string CleanupVcol(int value, int goal) =>
                goal > 0 && value > goal ? "color-red" : "color-green";
            // Borders are all uniformly grey now (Cleanup boxes share the Model
            // Integrity Scan look), so just pick a placeholder class — the CSS
            // override neutralises all border-* helpers to the grey gradient.
            string nnOverBorder = "border-green";
            string nnOverVcol2 = CleanupVcol(oversized, oversizedGoalApi);
            string nnPurgBorder = "border-green";
            string nnPurgVcol2 = CleanupVcol(purgeable, purgeGoalApi);
            // Threshold pill text for the Cleanup tiles (shown at the top-right).
            string nnOverGoalDisp = oversizedGoalApi > 0 ? $"Threshold: {oversizedGoalApi:N0}" : "Threshold: 0";
            string nnPurgGoalDisp = purgeGoalApi > 0 ? $"Threshold: {purgeGoalApi:N0}" : "Threshold: 100";

            // VNS / Inplace value colors
            string vnsVcol2 = vns >= vnsGoal ? "color-red" : vns > 0 ? "color-orange" : "color-green";
            string inpVcol2 = inplace >= inpGoal ? "color-red" : inplace > 0 ? "color-orange" : "color-green";

            // Sparkline bars HTML
            string vnsSpark = "";
            if (vns > 0)
            {
                var vsb = new StringBuilder();
                string vnsBC = vns >= vnsGoal ? "bg-red" : "bg-orange";
                foreach (var h in new[] { 40, 60, 80, 100, 80 })
                    vsb.Append($"<div style='height:{h}%' class='{vnsBC} w-bar'></div>");
                vnsSpark = vsb.ToString();
            }
            string inpSpark = "<div style='font-size:11px;font-weight:600;color:#21b5ff;width:100%;text-align:center;align-self:center;'>No Issues</div>";
            if (inplace > 0)
            {
                var isb = new StringBuilder();
                string inpBC = inplace >= inpGoal ? "bg-red" : "bg-orange";
                foreach (var h in new[] { 20, 40, 30, 50 })
                    isb.Append($"<div style='height:{h}%' class='{inpBC} w-bar'></div>");
                inpSpark = isb.ToString();
            }

            // Unplaced/Unenclosed sparklines: render bars matching the In-Place pattern
            // instead of "{N} issues" text. The count is already shown big at the top of
            // each card, so repeating it here was visual duplication ("2" + "2 issues").
            const string noIssuesSpark = "<div style='font-size:11px;font-weight:600;color:#21b5ff;width:100%;text-align:center;align-self:center;'>No Issues</div>";
            string BuildIssueBars(int count)
            {
                if (count <= 0) return noIssuesSpark;
                var sb = new StringBuilder();
                // Red when problematic, orange otherwise. Heuristic threshold mirrors how
                // the In-Place card escalates — over 5 unenclosed/unplaced rooms is "red".
                string cls = count > 5 ? "bg-red" : "bg-orange";
                foreach (var h in new[] { 20, 40, 30, 50 })
                    sb.Append($"<div style='height:{h}%' class='{cls} w-bar'></div>");
                return sb.ToString();
            }
            string unplacedSpark = BuildIssueBars(unplaced);
            string unenclosedSpark = BuildIssueBars(unenclosed);

            // Room subtitle (HTML)
            string roomSubTxt2 = roomTotal > 0 ? $"{unenclosed} Unenclosed<br>{unplaced} Unplaced" : "No Issues";

            // Complexity gauge SVG arc path (semicircle from 10,50 to 90,50 r=40 center=50,50)
            double cgPct2 = healthPct / 100.0;
            double cgAngle = Math.PI * (1.0 - cgPct2);
            double cgX = 50 + 40 * Math.Cos(cgAngle);
            double cgY = 50 - 40 * Math.Sin(cgAngle);
            int cgLargeArc = cgPct2 > 0.5 ? 1 : 0;
            string cgArcPath = cgPct2 < 0.01 ? "" : $"M 10,50 A 40,40 0 {cgLargeArc},1 {F1(cgX)},{F1(cgY)}";

            // â”€â”€ Build HTML â”€â”€
            // Complexity gauge stroke class
            string cgStrokeCls = healthPct >= 75 ? "navy-stroke" : healthPct >= 50 ? "orange-stroke" : "red-stroke";
            string cgDashOff = F0(100 - healthPct);

            string html = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='UTF-8'>
<meta name='viewport' content='width=device-width,initial-scale=1.0'>
<title>ZeManage - Model Health Dashboard</title>
<link rel='icon' type='image/png' href='##LOGO_SRC##'>
<link href='https://fonts.googleapis.com/css2?family=Poppins:wght@400;500;600;700;800;900&display=swap' rel='stylesheet'>
<link rel='stylesheet' href='https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.4.0/css/all.min.css'>
<script src='https://cdnjs.cloudflare.com/ajax/libs/html2canvas/1.4.1/html2canvas.min.js'></script>
<script src='https://cdnjs.cloudflare.com/ajax/libs/jspdf/2.5.1/jspdf.umd.min.js'></script>
<style>
*{box-sizing:border-box;margin:0;padding:0;-webkit-font-smoothing:antialiased;-moz-osx-font-smoothing:grayscale;}
body{font-family:'Poppins','Inter',sans-serif;background:#f8fafb;color:#334155;height:100vh;width:100vw;overflow:hidden;-webkit-font-smoothing:antialiased;-moz-osx-font-smoothing:grayscale;text-rendering:optimizeLegibility;font-feature-settings:'kern' 1,'liga' 1;font-variant-numeric:tabular-nums;letter-spacing:-0.01em;}
.dashboard{height:100vh;display:flex;flex-direction:column;}
.text-red{color:#e62db0 !important;}.bg-red{background-color:#ffe3f3 !important;}
.text-green{color:#167bb0 !important;}.bg-green{background-color:#d8eeff !important;}
.text-navy{color:#1e293b !important;}.bg-navy{background-color:#1e293b !important;}
.text-yellow{color:#d97706 !important;}.bg-yellow{background-color:#fef3c7 !important;}
.text-orange{color:#ea580c !important;}.bg-dark-orange{background-color:#ea580c !important;}.text-dark-orange{color:#ea580c !important;}
.bg-light-blue{background-color:#eff6ff !important;}.bg-light-green{background-color:#e3f4ff !important;}
.header{display:flex;justify-content:space-between;align-items:center;padding:12px 24px;background:#ffffff;box-shadow:0 1px 4px rgba(0,0,0,0.03);z-index:10;border-bottom:1px solid #eef1f5;}
.header-left{display:flex;align-items:center;gap:15px;}.logo{font-size:24px;color:#536f8a;}
.header-titles h1{font-size:22px;font-weight:800;color:#1e293b;letter-spacing:0.3px;margin-bottom:2px;}
.header-titles span{font-size:13px;color:#64748b;font-weight:500;}
.header-center{display:flex;gap:6px;flex-wrap:wrap;justify-content:center;}
.pill{padding:4px 10px;border-radius:6px;font-size:11px;font-weight:600;display:flex;align-items:center;gap:4px;border:1px solid #e2e8f0;background:#f8fafc;transition:all .15s;}
.pill:hover{background:#f1f5f9;}
.pill-green{background:#e3f4ff;border-color:#bae0ff;color:#0f5a8a;}
.pill-yellow{background:#fefce8;border-color:#fde68a;color:#854d0e;}
.pill-red{background:#fff0f8;border-color:#fecaca;color:#991b1b;}
.pill.text-green{background:#e3f4ff;border-color:#bae0ff;}.pill.text-red{background:#fcf6f6;border-color:#f5ecec;}
.light-red{color:#ff3ec9;}.light-green{color:#21b5ff;}
.red-dot{background:#ff3ec9;}.green-dot{background:#21b5ff;}
.dot{width:6px;height:6px;border-radius:50%;display:inline-block;box-shadow:0 0 2px rgba(0,0,0,0.1);}
.header-right{display:flex;align-items:center;gap:15px;}
.header-date{font-size:11px;font-weight:600;color:#475569;background:#f1f5f9;padding:5px 12px;border-radius:6px;}
.btn-download{background:#7054ff;color:white;border:none;padding:8px 16px;border-radius:8px;font-size:12px;font-weight:600;cursor:pointer;display:flex;gap:6px;align-items:center;box-shadow:0 2px 8px rgba(99,102,241,0.25);transition:all .2s ease;}
.btn-download:hover{transform:translateY(-1px);box-shadow:0 6px 20px rgba(99,102,241,0.4);background:#5a3fcc;}
/* 4-column layout matching the reference: narrower outer columns, wider
   middle two columns to give bars / grids breathing room. */
.main-grid{display:grid;grid-template-columns:1.2fr 1.45fr 1.3fr 0.95fr;gap:6px;padding:6px;flex:1;min-height:0;}
.col{display:flex;flex-direction:column;gap:12px;min-height:0;overflow-y:auto;overflow-x:hidden;}.col::-webkit-scrollbar{width:3px;}.col::-webkit-scrollbar-thumb{background:#cbd5e1;border-radius:3px;}.flex-grow{flex-grow:1;}.mt-auto{margin-top:auto;}
/* All 4 columns: cards inside grow to fill the column height proportionally
   so the dashboard covers the full viewport with no empty space below. */
.left-col{overflow:hidden;}.left-col .card{flex:1;}
.center-col{overflow:hidden;}.center-col .card{flex:1;}
.kpi-col{overflow:hidden;}.kpi-col .card{flex:1;}
.opt-col{overflow:hidden;}.opt-col .card{flex:1;}
.card{background:#ffffff;border-radius:12px;padding:16px;box-shadow:0 1px 3px rgba(0,0,0,0.04);border:1px solid #eef1f5;display:flex;flex-direction:column;min-height:0;transition:all 0.2s ease;}
.card:hover{box-shadow:0 2px 8px rgba(0,0,0,0.06);}
.left-col .card{padding:14px;}
.card:hover{box-shadow:0 20px 40px -5px rgba(0,0,0,0.08),0 8px 16px -4px rgba(0,0,0,0.03),inset 0 1px 0 rgba(255,255,255,0.9);transform:translateY(-1px);}
.card-title{font-size:13px;font-weight:500;color:#1e293b;letter-spacing:-0.01em;margin-bottom:16px;text-transform:none;flex-shrink:0;}
/* Each column is a flex/scroll stack of cards (3 cards per col on the left
   columns, 3 cards on col3, and a single tall card on col4). */
.center-col{display:flex;flex-direction:column;gap:12px;min-height:0;overflow-y:auto;}
.right-col{display:flex;flex-direction:column;gap:12px;min-height:0;overflow-y:auto;}
.kpi-col{display:flex;flex-direction:column;gap:12px;min-height:0;overflow-y:auto;}
.opt-col{display:flex;flex-direction:column;gap:12px;min-height:0;overflow-y:auto;}
.stat-box{display:flex;align-items:center;gap:14px;padding:16px;border-radius:12px;margin-bottom:0;flex-shrink:0;}
.stat-bar-row{display:flex;flex-direction:column;gap:8px;margin-top:8px;flex:1;justify-content:flex-end;}
.stat-bar-item{display:flex;align-items:center;gap:10px;}
.stat-bar-item .sbi-label{font-size:10px;font-weight:700;color:#64748b;text-transform:uppercase;letter-spacing:0.3px;width:68px;flex-shrink:0;}
.stat-bar-item .sbi-track{flex:1;height:18px;background:#e9eef4;border-radius:4px;overflow:hidden;}
.stat-bar-item .sbi-fill{height:100%;border-radius:4px;min-width:4px;transition:width .6s ease;}
.stat-bar-item .sbi-fill-navy{background:linear-gradient(90deg,#7054ff,#5a3fcc);}
.stat-bar-item .sbi-fill-green{background:linear-gradient(90deg,#21b5ff,#1a9bdc);}
.stat-bar-item .sbi-fill-teal{background:linear-gradient(90deg,#ffb61d,#d99a17);}
.stat-bar-item .sbi-val{font-size:14px;font-weight:800;color:#1e293b;min-width:28px;text-align:right;}
.light-blue-box{background:#e2ecf5;}.light-green-box{background:#e3f4ff;}.light-red-box{background:#ffe3f3;}
.stat-icon{width:44px;height:44px;border-radius:12px;display:flex;align-items:center;justify-content:center;font-size:18px;transition:transform .2s;}
.stat-box:hover .stat-icon{transform:scale(1.08);}
.blue-icon{background:linear-gradient(135deg,#7054ff,#5a3fcc);color:#fff;box-shadow:0 4px 12px rgba(99,102,241,.25);}.green-icon{background:linear-gradient(135deg,#21b5ff,#1a9bdc);color:#fff;box-shadow:0 4px 12px rgba(16,185,129,.25);}.red-icon{background:linear-gradient(135deg,#ff3ec9,#e62db0);color:#fff;box-shadow:0 4px 12px rgba(239,68,68,.25);}
.stat-label{font-size:12px;font-weight:600;color:#64748b;letter-spacing:0.3px;text-transform:uppercase;margin-bottom:6px;}
.stat-value{font-size:22px;font-weight:800;color:#1e293b;line-height:1;}
.stat-value span{font-size:13px;font-weight:600;color:#64748b;}
.proj-info-card{overflow-y:auto;}.proj-info-card::-webkit-scrollbar{width:3px;}.proj-info-card::-webkit-scrollbar-thumb{background:#cbd5e1;border-radius:3px;}
.info-row{padding:8px 0;border-bottom:1px solid #f1f5f9;flex-shrink:0;}
.info-row.border-none{border-bottom:none;}
.info-label{font-size:10px;font-weight:700;color:#94a3b8;letter-spacing:0.5px;text-transform:uppercase;margin-bottom:3px;}
.info-val{font-size:13px;font-weight:600;color:#1e293b;line-height:1.4;word-break:break-word;}
.coords-box{display:flex;flex-direction:column;gap:4px;margin-top:4px;}
.coord-item{display:flex;align-items:center;justify-content:space-between;padding:4px 8px;background:#f8fafc;border-radius:6px;border:1px solid rgba(0,0,0,.03);}
.coord-left{display:flex;align-items:center;gap:5px;}
.c-icon{width:30px;height:30px;border-radius:8px;display:flex;align-items:center;justify-content:center;font-size:13px;color:#fff;box-shadow:0 2px 6px rgba(0,0,0,.12);}
.coord-item span{font-size:10px;font-weight:600;color:#64748b;text-transform:uppercase;letter-spacing:0.3px;}
.coord-item strong{font-size:12px;font-weight:700;color:#1e293b;}
.complexity-widget{text-align:center;}
.gauge-container{position:relative;max-width:120px;margin:0 auto;padding:4px 0;}
.gauge-container svg{width:100%;height:auto;}
.gauge-bg{fill:none;stroke:#e2e8f0;stroke-width:8;stroke-linecap:round;}
.gauge-fill{fill:none;stroke-width:8;stroke-linecap:round;transition:stroke-dashoffset 1s ease;}
.navy-stroke{stroke:#7054ff;}.orange-stroke{stroke:#ffb61d;}.red-stroke{stroke:#ff3ec9;}
.gauge-text{position:absolute;bottom:4px;left:50%;transform:translateX(-50%);font-size:22px;font-weight:800;color:#1e293b;}
.gauge-text span{font-size:16px;font-weight:600;color:#64748b;}
.gauge-sub{font-size:10px;font-weight:700;color:#64748b;text-transform:uppercase;letter-spacing:0.5px;margin-top:4px;}
.eb-wrap{display:flex;flex-direction:column;gap:10px;align-items:center;}
.eb-donut-area{flex-shrink:0;position:relative;}
.donut-chart{position:relative;width:140px;height:140px;flex-shrink:0;margin:0 auto;}
.donut{width:100%;height:100%;border-radius:50%;background:conic-gradient(#7054ff 0% ##MOD_PCT_DISP##%,#ffb61d ##MOD_PCT_DISP##% ##MOD_ANN_END##%,#ff3ec9 ##MOD_ANN_END##% 100%);}
.donut-inner{position:absolute;top:24px;left:24px;right:24px;bottom:24px;background:white;border-radius:50%;display:flex;flex-direction:column;align-items:center;justify-content:center;}
.dt-label{font-size:10px;font-weight:700;color:#94a3b8;letter-spacing:0.3px;}
.dt-val{font-size:16px;font-weight:800;color:#1e293b;}
.eb-cards{width:100%;display:flex;flex-direction:column;gap:5px;}
.eb-card{padding:7px 10px;border-radius:9px;transition:transform .15s,box-shadow .15s;}
.eb-card:hover{transform:scale(1.015);box-shadow:0 4px 12px rgba(0,0,0,.06);}
.eb-card-top{display:flex;justify-content:space-between;align-items:center;margin-bottom:2px;}
.eb-card-label{font-size:10px;font-weight:800;color:#334155;}
.eb-card-pct{font-size:10px;font-weight:600;color:#94a3b8;}
.eb-card-val{font-size:15px;font-weight:800;line-height:1;margin-bottom:3px;}
.eb-track{height:4px;background:rgba(0,0,0,.04);border-radius:2px;overflow:hidden;}
.eb-bar{height:100%;border-radius:2px;transition:width .8s ease;}
.col-chart{display:flex;justify-content:space-evenly;align-items:flex-end;flex:1;padding:8px 12px 8px;gap:16px;}
.chart-col{display:flex;flex-direction:column;align-items:center;height:100%;justify-content:flex-end;flex:1;gap:6px;}
.chart-bar{width:100%;max-width:56px;border-radius:8px 8px 0 0;transition:height .8s ease;min-height:6px;}
.bar-navy{background:#7054ff;}.bar-gold{background:#ffb61d;}.bar-green{background:#21b5ff;}
.bar-grey{background:linear-gradient(180deg,#b0bec5,#d0dae0);}
.chart-val{font-size:14px;font-weight:800;color:#1e293b;}
.chart-label{font-size:10px;color:#64748b;font-weight:700;margin-top:4px;letter-spacing:0.3px;}
.waterfall-chart{display:flex;flex-direction:column;gap:20px;align-items:center;justify-content:center;flex:1;padding:8px 6px;}
.waterfall-row{display:flex;align-items:center;gap:12px;width:100%;padding:0 4px;}
.chart-bar-horiz{height:30px;border-radius:8px;transition:width .8s ease;min-width:6px;}
.waterfall-row .chart-label{width:48px;margin:0;text-align:right;font-size:11px;color:#334155;font-weight:700;}
.waterfall-row .chart-val{font-size:14px;font-weight:800;color:#1e293b;}
.vs-card{padding:16px 18px;}
.vs-card .card-title{margin-bottom:8px;}
.vs-chart{display:flex;justify-content:space-evenly;align-items:stretch;flex:1;padding:6px 16px 6px;gap:20px;}
.vs-col{display:flex;flex-direction:column;align-items:center;height:100%;justify-content:flex-end;flex:1;gap:0;min-width:0;}
.vs-bar-wrap{width:100%;max-width:60px;flex:1;min-height:0;display:flex;flex-direction:column;align-items:center;justify-content:flex-end;}
.vs-bar{border-radius:10px 10px 4px 4px;transition:height .8s cubic-bezier(.4,0,.2,1);min-height:8px;width:100%;animation:growBar .8s cubic-bezier(.4,0,.2,1) both;transform-origin:bottom center;position:relative;}
.vs-bar::after{content:'';position:absolute;top:0;left:0;right:0;bottom:0;border-radius:inherit;background:linear-gradient(180deg,rgba(255,255,255,.2) 0%,transparent 60%);pointer-events:none;}
.vs-bar-navy{background:linear-gradient(180deg,#7054ff,#5a3fcc);box-shadow:0 4px 14px rgba(99,102,241,.3);}
.vs-bar-green{background:linear-gradient(180deg,#21b5ff,#1a9bdc);box-shadow:0 4px 14px rgba(16,185,129,.3);}
.vs-bar-red{background:linear-gradient(180deg,#ff6b6b,#dc2626);box-shadow:0 4px 14px rgba(220,38,38,.3);}
.vs-bar-amber{background:linear-gradient(180deg,#ffb61d,#d99a17);box-shadow:0 4px 14px rgba(139,92,246,.3);}
.vs-bar-grey{background:linear-gradient(180deg,#b0bec5,#90a4ae);box-shadow:0 4px 14px rgba(0,0,0,.1);}
.vs-val{font-size:17px;font-weight:800;color:#1e293b;line-height:1;margin-bottom:0;animation:pulseNumber .5s ease-out .6s both;}
.vs-label{font-size:10px;color:#64748b;font-weight:700;margin-top:6px;letter-spacing:0.3px;text-transform:uppercase;text-align:center;min-height:28px;display:flex;align-items:flex-start;justify-content:center;line-height:1.2;}
.vs-horiz{display:flex;flex-direction:column;gap:0;justify-content:space-evenly;flex:1;padding:8px 10px;}
.vs-hrow{display:flex;align-items:center;gap:12px;width:100%;}
.vs-hlabel{width:95px;text-align:left;font-size:13px;color:#1e293b;font-weight:700;flex-shrink:0;white-space:nowrap;letter-spacing:-0.01em;}
/* Horizontal bar row: light empty track behind the filled bar, pill-rounded,
   with the numeric value sitting outside the track on the right. */
/* Bar wrap is now just the pill track; the numeric value is a sibling flex
   child of the row (see HTML), guaranteeing label + bar + number share the
   exact same horizontal centerline via vs-hrow's align-items:center. */
.vs-hbar-wrap{flex:1;height:10px;background:#f1f2f6;border-radius:999px;position:relative;}
.vs-hbar-wrap .vs-bar{height:100%;border-radius:999px;animation:growBarH .8s cubic-bezier(.4,0,.2,1) both;transform-origin:left center;min-width:8px;box-shadow:0 2px 6px rgba(0,0,0,.08);}
.vs-hbar-wrap .vs-bar::after{background:linear-gradient(90deg,rgba(255,255,255,.18) 0%,transparent 60%);}
.vs-hval{font-size:16px;font-weight:800;color:#1e293b;min-width:48px;text-align:right;animation:pulseNumber .5s ease-out .6s both;flex-shrink:0;line-height:1;letter-spacing:-0.02em;}
.bp-grid{display:grid;grid-template-columns:1fr 1fr 1fr;gap:8px;flex:1;}
.bp-card{background:#f8fafc;border:1px solid #eef1f5;border-radius:10px;padding:12px;display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;transition:all .2s;}
.bp-card:hover{border-color:#cbd5e1;background:#f1f5f9;}
.bp-card-val{font-size:22px;font-weight:800;color:#7054ff;line-height:1;}
.bp-card-label{font-size:10px;font-weight:700;color:#94a3b8;text-transform:uppercase;letter-spacing:0.5px;margin-top:4px;}
/* Underline progress bar removed per user request. */
.bp-card-bar{display:none;}
.bp-card-fill{display:none;}
.bp-card-scr{font-size:10px;font-weight:600;color:#64748b;margin-top:4px;}
.dc-meter-wrap{display:flex;flex-direction:column;gap:14px;justify-content:center;flex:1;padding:4px 0;}
.dc-meter-row{animation:slideInFade .4s ease-out both;}
.dc-meter-row:nth-child(1){animation-delay:.2s;}.dc-meter-row:nth-child(2){animation-delay:.3s;}.dc-meter-row:nth-child(3){animation-delay:.4s;}
.dc-meter-head{display:flex;align-items:center;gap:8px;margin-bottom:6px;}
.dc-meter-ico{width:28px;height:28px;border-radius:8px;display:flex;align-items:center;justify-content:center;font-size:12px;flex-shrink:0;}
.dc-ico-ok{background:linear-gradient(135deg,#21b5ff,#1a9bdc);color:#fff;box-shadow:0 3px 8px rgba(16,185,129,.25);}
.dc-ico-bad{background:linear-gradient(135deg,#ff3ec9,#ff7ed7);color:#fff;box-shadow:0 3px 8px rgba(196,124,124,.25);}
/* Per-row brand colors: Walls=1 purple, Pipes=2 yellow, Ducts=3 pink. */
.dc-ico-walls{background:linear-gradient(135deg,#7054ff,#5a3fcc);color:#fff;box-shadow:0 3px 8px rgba(112,84,255,.25);}
.dc-ico-pipes{background:linear-gradient(135deg,#ffb61d,#d99a17);color:#fff;box-shadow:0 3px 8px rgba(255,182,29,.25);}
.dc-ico-ducts{background:linear-gradient(135deg,#ff3ec9,#e62db0);color:#fff;box-shadow:0 3px 8px rgba(255,62,201,.25);}
.dc-meter-name{font-size:13px;font-weight:700;color:#334155;flex:1;}
.dc-meter-count{font-size:17px;font-weight:800;line-height:1;}
.dc-cnt-ok{color:#21b5ff;}.dc-cnt-bad{color:#dc2626;}
/* Inline goal suffix on the Disconnected Elements rows. */
.dc-goal-suf{font-size:11px;font-weight:600;color:#94a3b8;margin-left:4px;}
.dc-cnt-bad .dc-goal-suf{color:#dc2626;}
.dc-goal-suf:empty{display:none;}
.dc-cnt-walls{color:#7054ff;}.dc-cnt-pipes{color:#ffb61d;}.dc-cnt-ducts{color:#ff3ec9;}
.dc-meter-track{height:8px;background:#f1f5f9;border-radius:20px;overflow:hidden;position:relative;}
.dc-meter-fill{height:100%;border-radius:20px;animation:growBarH .9s cubic-bezier(.4,0,.2,1) .4s both;transform-origin:left center;position:relative;min-width:6px;}
.dc-meter-fill::after{content:'';position:absolute;top:0;left:0;right:0;bottom:0;border-radius:inherit;background:linear-gradient(90deg,rgba(255,255,255,.25) 0%,transparent 70%);}
.dc-fill-ok{background:linear-gradient(90deg,#21b5ff,#21b5ff);}
.dc-fill-bad{background:linear-gradient(90deg,#ff6b6b,#dc2626);}
.dc-fill-walls{background:linear-gradient(90deg,#7054ff,#5a3fcc);}
.dc-fill-pipes{background:linear-gradient(90deg,#ffb61d,#d99a17);}
.dc-fill-ducts{background:linear-gradient(90deg,#ff3ec9,#e62db0);}
.dc-status{text-align:center;font-size:9px;font-weight:700;padding:6px 10px;border-radius:8px;flex-shrink:0;margin-top:4px;}
.dc-status.ok{background:#e3f4ff;color:#0f5a8a;}.dc-status.warn{background:#fef5f0;color:#ff3ec9;}
.trend-header{display:flex;justify-content:space-between;align-items:center;margin-bottom:8px;}
.trend-legend{display:flex;gap:12px;font-size:9px;font-weight:600;color:#94a3b8;}
.trend-legend i{font-size:8px;}
.goal-pill{font-size:9px;font-weight:700;padding:4px 10px;border-radius:14px;background:#fff0f8;color:#ff3ec9;border:1px solid #fce8e8;}
.goal-pill.ok-pill{background:#e3f4ff;color:#0f5a8a;border-color:#bae0ff;}
.trend-chart-wrap{display:flex;flex:1;min-height:0;overflow:hidden;}
.trend-y{display:flex;flex-direction:column;justify-content:space-between;font-size:8px;font-weight:600;color:#94a3b8;padding-right:6px;text-align:right;width:28px;flex-shrink:0;}
.trend-area{flex:1;position:relative;padding-bottom:16px;}
.trend-grid{position:absolute;top:0;left:0;right:0;bottom:16px;display:flex;flex-direction:column;justify-content:space-between;}
.tg{height:0;border-bottom:1px solid #f1f5f9;width:100%;}
.trend-svg{position:absolute;top:0;left:0;width:100%;height:calc(100% - 16px);z-index:2;overflow:visible;}
.trend-x{position:absolute;bottom:0;left:0;right:0;display:flex;justify-content:space-between;font-size:8px;font-weight:600;color:#94a3b8;}
.crit-3{display:grid;grid-template-columns:1fr 1fr;grid-auto-rows:1fr;gap:8px;min-height:0;margin-top:8px;flex:1;}
.ct-tile{border-radius:14px;background:linear-gradient(145deg,#f4f5f7,#fafbfc);border:1px solid rgba(30,41,59,.08);display:flex;flex-direction:column;align-items:center;justify-content:center;padding:38px 10px 16px 10px;text-align:center;transition:all .25s cubic-bezier(.4,0,.2,1);}
.ct-tile:hover{transform:translateY(-3px) scale(1.03);box-shadow:0 8px 20px rgba(0,0,0,.07);}
.ct-tile.ct-bad{background:linear-gradient(145deg,#f4f5f7,#fafbfc) !important;border-color:rgba(30,41,59,.08) !important;}
.ct-tile.ct-warn{background:linear-gradient(145deg,#f4f5f7,#fafbfc) !important;border-color:rgba(30,41,59,.08) !important;}
.ct-warn .ct-tile-icon{color:#ffb61d;}
.ct-warn .ctv{color:#ffb61d !important;}
.ct-tile-icon{display:none;}
.ct-bad .ct-tile-icon{color:#ff3ec9;}
/* 3-tier value colour per user request:
   value < goal  -> cyan (4)  -> default .ctv color
   value == goal -> yellow (2) -> .ct-eq .ctv
   value > goal  -> red       -> .ct-bad .ctv */
/* 2-tier value colour (user spec):
   value <= threshold OR no threshold OR value == 0 -> purple (1)
   value >  threshold                                -> red */
.ctv{font-size:26px;font-weight:800;line-height:1;color:#7054ff;letter-spacing:-0.02em;}
.ct-bad .ctv{color:#dc2626 !important;}
.ctl{font-size:13px;font-weight:700;color:#1e293b;margin-top:6px;text-align:center;letter-spacing:-0.01em;}
.ctsub{font-size:11px;font-weight:700;color:#64748b;margin-top:4px;text-align:center;}
.color-red{color:#dc2626 !important;}.color-orange{color:#7054ff !important;}.color-green{color:#7054ff !important;}.color-blue{color:#7054ff !important;}
.nn-boxes{display:flex;gap:10px;align-items:stretch;flex:1;}
.nn-box{flex:1;border:1px solid rgba(30,41,59,.08);border-radius:14px;padding:38px 12px 16px 12px;text-align:center;display:flex;flex-direction:column;justify-content:center;background:linear-gradient(145deg,#f4f5f7,#fafbfc);transition:all .25s cubic-bezier(.4,0,.2,1);}
.nn-box:hover{transform:translateY(-3px);box-shadow:0 8px 20px rgba(0,0,0,.06);}
/* Cleanup boxes use the same grey gradient as Model Quality Scan; override
   former coloured border-* helpers so all nn-box tiles look uniform grey
   regardless of status. Status is conveyed by the value text colour instead. */
.border-green,.border-navy,.border-red,.border-orange{border-color:rgba(30,41,59,.08) !important;background:linear-gradient(145deg,#f4f5f7,#fafbfc) !important;}
.color-orange-logo{color:#7054ff !important;}
.unreg-banner{display:flex;align-items:flex-start;gap:12px;margin:12px 24px 0;padding:14px 18px;background:#FEF3C7;border:1px solid #F59E0B;border-left:4px solid #F59E0B;border-radius:8px;color:#78350F;}
.unreg-icon{font-size:20px;line-height:1;color:#B45309;flex-shrink:0;}
.unreg-text{font-size:13px;line-height:1.5;}.unreg-text b{font-weight:700;}
.nn-val{font-size:26px;font-weight:800;line-height:1;margin-bottom:4px;letter-spacing:-0.02em;}
.nn-label{font-size:13px;font-weight:700;color:#1e293b;margin-top:6px;text-transform:none;letter-spacing:-0.01em;}
/* Model Integrity Scan panes: compact tiles (~90px tall each), centered
   vertically within the card so they don't stretch to fill column height. */
.bs-list{display:flex;gap:10px;flex:1;align-items:center;justify-content:center;margin-top:8px;}
.bs-col{border:1px solid rgba(30,41,59,.08);border-radius:14px;flex:1;display:flex;flex-direction:column;align-items:center;justify-content:center;padding:22px 8px;min-height:140px;max-height:170px;text-align:center;background:linear-gradient(145deg,#f4f5f7,#fafbfc);transition:all .25s cubic-bezier(.4,0,.2,1);}
.bs-col:hover{transform:translateY(-3px);box-shadow:0 8px 20px rgba(0,0,0,.06);}
.bs-val{font-size:24px;font-weight:800;margin-bottom:4px;line-height:1;color:#7054ff;letter-spacing:-0.02em;}
.bs-label{font-size:12px;font-weight:700;color:#1e293b;line-height:1.3;margin-bottom:0;text-transform:none;letter-spacing:-0.01em;}
.bs-chart{display:flex;gap:3px;align-items:flex-end;height:36px;justify-content:center;}
.w-bar{width:5px;border-radius:2px 2px 0 0;}
.bg-light-blue-box{background:#e2ecf5 !important;border-color:#c8daea;}
.bs-sublabel{font-size:8px;font-weight:600;color:#1e293b;margin-top:auto;line-height:1.3;}
.bold-subtitle{font-size:8px;font-weight:500;color:#94a3b8;margin-bottom:6px;}
.font-black{font-weight:800 !important;color:#1e293b !important;}
@keyframes fadeInUp{from{opacity:0;transform:translateY(15px);}to{opacity:1;transform:translateY(0);}}
@keyframes scaleIn{from{transform:scale(0.85);opacity:0;}to{transform:scale(1);opacity:1;}}
@keyframes fadeIn{from{opacity:0;}to{opacity:1;}}
@keyframes slideInFade{from{opacity:0;transform:translateX(-12px);}to{opacity:1;transform:translateX(0);}}
@keyframes donutSpin{0%{transform:rotate(-90deg);opacity:0;}60%{opacity:1;}100%{transform:rotate(0deg);opacity:1;}}
@keyframes pulseNumber{0%{transform:scale(0.8);opacity:0;}60%{transform:scale(1.05);}100%{transform:scale(1);opacity:1;}}
@keyframes shimmer{0%{background-position:-200% 0;}100%{background-position:200% 0;}}
@keyframes gaugeStroke{from{stroke-dashoffset:100;}to{stroke-dashoffset:var(--dash-target);}}
@keyframes growBar{from{transform:scaleY(0);opacity:0;}to{transform:scaleY(1);opacity:1;}}
@keyframes growBarH{from{transform:scaleX(0);opacity:0;}to{transform:scaleX(1);opacity:1;}}
.card{animation:fadeInUp .5s cubic-bezier(.4,0,.2,1) both;}
.left-col .card:nth-child(1){animation-delay:.05s;}.left-col .card:nth-child(2){animation-delay:.12s;}
.center-col .card:nth-child(1){animation-delay:.08s;}.center-col .card:nth-child(2){animation-delay:.14s;}.center-col .card:nth-child(3){animation-delay:.2s;}.center-col .card:nth-child(4){animation-delay:.26s;}
.center-col .card:nth-child(5){animation-delay:.32s;}.center-col .card:nth-child(6){animation-delay:.38s;}.center-col .card:nth-child(7){animation-delay:.44s;}
.right-col .card:nth-child(1){animation-delay:.1s;}.right-col .card:nth-child(2){animation-delay:.2s;}.right-col .card:nth-child(3){animation-delay:.3s;}
.donut{animation:donutSpin .8s cubic-bezier(.4,0,.2,1) .3s both;transform-origin:center center;}
.donut-inner{animation:pulseNumber .5s ease-out .8s both;}
.stat-box{animation:slideInFade .4s ease-out both;}.stat-box:nth-child(1){animation-delay:.15s;}.stat-box:nth-child(2){animation-delay:.25s;}
.stat-value{animation:pulseNumber .5s ease-out .4s both;}
.header{animation:fadeIn .3s ease-out;}
.pill{animation:fadeIn .4s ease-out both;}
.header-center .pill:nth-child(1){animation-delay:.1s;}.header-center .pill:nth-child(2){animation-delay:.14s;}.header-center .pill:nth-child(3){animation-delay:.18s;}.header-center .pill:nth-child(4){animation-delay:.22s;}.header-center .pill:nth-child(5){animation-delay:.26s;}
.eb-card{animation:slideInFade .4s ease-out both;}.eb-card:nth-child(1){animation-delay:.4s;}.eb-card:nth-child(2){animation-delay:.5s;}.eb-card:nth-child(3){animation-delay:.6s;}
.eb-bar{animation:shimmer 2s ease-in-out 1s 1 both;background-size:200% 100%;}
.bp-gauge-card{animation:fadeInUp .4s ease-out both;}.bp-gauge-card:nth-child(1){animation-delay:.3s;}.bp-gauge-card:nth-child(2){animation-delay:.38s;}.bp-gauge-card:nth-child(3){animation-delay:.46s;}.bp-gauge-card:nth-child(4){animation-delay:.54s;}
.ct-tile{animation:fadeInUp .4s ease-out both;}.ct-tile:nth-child(1){animation-delay:.2s;}.ct-tile:nth-child(2){animation-delay:.3s;}.ct-tile:nth-child(3){animation-delay:.4s;}.ct-tile:nth-child(4){animation-delay:.5s;}
.nn-box{animation:fadeInUp .4s ease-out both;}.nn-box:nth-child(1){animation-delay:.25s;}.nn-box:nth-child(2){animation-delay:.35s;}
.bs-col{animation:fadeInUp .4s ease-out both;}.bs-col:nth-child(1){animation-delay:.3s;}.bs-col:nth-child(2){animation-delay:.38s;}.bs-col:nth-child(3){animation-delay:.46s;}
/* === New components for the 4-column layout === */
/* Colored gradient mini-cards for Levels / Grids / Linked Revit. */
.lgr-row{display:flex;gap:8px;margin-top:10px;}
.lgr-tile{position:relative;flex:1;border-radius:12px;padding:14px 8px;text-align:center;color:#fff;box-shadow:0 4px 12px rgba(0,0,0,.08);}
.lgr-tile .lgr-v{font-size:22px;font-weight:800;line-height:1;}
.lgr-tile .lgr-l{font-size:11px;font-weight:600;margin-top:6px;letter-spacing:.2px;}
.lgr-tile .thresh-pill{color:#1e293b;background:rgba(255,255,255,.92);}
.lgr-tile.ct-bad .lgr-v{color:#fff5f5;text-shadow:0 0 6px rgba(255,255,255,.45);}
.lgr-tile.ct-bad{background:linear-gradient(140deg,#ff7a7a,#dc2626) !important;}
.lgr-purple{background:linear-gradient(140deg,#8e7cff,#7054ff);}
.lgr-yellow{background:linear-gradient(140deg,#ffc94d,#ffb61d);}
.lgr-pink{background:linear-gradient(140deg,#ff66d9,#ff3ec9);}

/* Threshold pill shown at the top-right of KPI / Cleanup / MIS / Opt tiles.
   When no server-side threshold is configured for the card's parameterCode,
   the C# layer sets the placeholder to empty and CSS hides the pill. */
.thresh-pill{position:absolute;top:8px;right:10px;font-size:10px;font-weight:600;color:#94a3b8;background:#f1f5f9;padding:3px 9px;border-radius:999px;letter-spacing:0;}
.thresh-pill:empty{display:none !important;}
.ct-tile{position:relative;}
.nn-box{position:relative;}

/* Optimization Indicators column tiles. Stretch to share the card height
   evenly so the column covers the full page. */
.opt-tile{background:linear-gradient(145deg,#f4f5f7,#fafbfc);border:1px solid rgba(30,41,59,.08);border-radius:12px;padding:14px 10px;text-align:center;display:flex;flex-direction:column;align-items:center;justify-content:center;flex:1;min-height:0;}
.opt-tile .opt-v{font-size:24px;font-weight:800;color:#7054ff;line-height:1;letter-spacing:-0.02em;}
.opt-tile .opt-l{font-size:13px;font-weight:700;color:#1e293b;margin-top:6px;letter-spacing:-0.01em;}
/* Red override when value exceeds threshold (added via ct-bad helper class). */
.opt-tile.ct-bad .opt-v{color:#dc2626 !important;}
.bs-col.ct-bad .bs-val{color:#dc2626 !important;}
.opt-list{display:flex;flex-direction:column;gap:8px;margin-top:8px;flex:1;min-height:0;}

/* Element Composition: compact chip-style legend instead of bars. */
.eb-chip{display:flex;align-items:center;gap:10px;padding:10px 12px;border-radius:10px;background:#f8fafc;border:1px solid #eef1f5;margin-bottom:8px;}
/* Per-chip threshold pill: sits inside the chip in the top-right
   corner, with chip padding-top expanded just enough to host it without
   colliding with the count / name / percentage flex line. Stays inside
   the chip's bounds so no parent overflow:hidden can clip it. Empty
   pill is hidden so chips without a configured per-chip goal stay
   visually identical to before. */
.eb-chip{padding-top:18px !important;padding-bottom:8px !important;}
.eb-thresh{position:absolute;top:3px;right:8px;font-size:9px;font-weight:700;color:#475569;background:#ffffff;padding:1px 7px;border-radius:999px;letter-spacing:.2px;line-height:1.4;border:1px solid #e2e8f0;box-shadow:0 1px 2px rgba(15,23,42,.06);pointer-events:none;z-index:2;white-space:nowrap;}
.eb-thresh:empty{display:none;}
/* Inline goal next to the count number. Stays neutral grey even when
   the count itself goes red (per-user request 2026-06-10). */
.eb-inline-goal{font-size:12px;font-weight:700;color:#64748b !important;margin-left:2px;}
.eb-inline-goal:empty{display:none;}
/* Per-user request 2026-06-05: keep the count / percentage red when a
   per-chip threshold is exceeded, but DO NOT change the chip border or
   add a glow — the brand-coloured pastel border stays untouched. */
.eb-chip.ct-bad .eb-count, .eb-chip.ct-bad .eb-pct{color:#dc2626 !important;}
.eb-chip .eb-dot{width:11px;height:11px;border-radius:3px;flex-shrink:0;}
.eb-chip .eb-count{font-size:14px;font-weight:800;color:#1e293b;letter-spacing:-0.02em;}
.eb-chip .eb-name{font-size:12px;font-weight:600;color:#64748b;flex:1;}
.eb-chip .eb-pct{font-size:12px;font-weight:700;color:#1e293b;letter-spacing:-0.01em;}

/* Shared Coordinates standalone card spacing. */
.sc-list{display:flex;flex-direction:column;gap:8px;}

@media print{body{background:#fff !important;overflow:visible !important;height:auto !important;}.dashboard{height:auto !important;}.main-grid{overflow:visible !important;}.btn-download{display:none !important;}.card{animation:none !important;}}
</style>
</head>
<body>
<div class='dashboard'>
<header class='header'>
  <div class='header-left'>
    <div class='logo'><img src='##LOGO_SRC##' alt='Logo' style='height:36px;width:auto;display:block;'/></div>
    <div class='header-titles'>
      <h1>MODEL HEALTH &amp; COMPLIANCE</h1>
      <span>##PROJECT_NAME## &middot; ##MODEL_NAME_DISP## &middot; Last Sync: ##SYNC_TIME_DISP## ##SYNC_BY_DISP##</span>
    </div>
  </div>
  <div class='header-center'>
    ##HEADER_PILLS##
  </div>
  <div class='header-right'>
    <span class='header-date' id='clock'>--:--</span>
    <button class='btn-download' onclick='dlDash()'><i class='fa-solid fa-download'></i> DOWNLOAD</button>
  </div>
</header>
##UNREG_BANNER##
<main class='main-grid'>
  <!-- COLUMN 1: File & Model Summary / Element Composition / Shared Coordinates -->
  <div class='col left-col'>
    <div class='card statics-card'>
      <h2 class='card-title'>File &amp; Model Summary</h2>
      <div class='stat-box' style='margin-bottom:8px;flex-shrink:0;background:#e3e8f3;'>
        <div class='stat-icon blue-icon'><i class='fa-regular fa-file'></i></div>
        <div class='stat-info'>
          <div class='stat-label' style='white-space:nowrap;'>FILE SIZE <span style='font-size:9px;color:#94a3b8;font-weight:500;'>/ ##FS_GOAL_DISP## Threshold</span></div>
          <div class='stat-value ##FS_VAL_CLS##'>##FILE_SIZE##</div>
        </div>
      </div>
      <div class='lgr-row' title='Levels / Grids / Linked Revit'>
        <div class='lgr-tile lgr-purple ##LVL_BAD_CLS##'><span class='thresh-pill'>##LVL_PILL##</span><div class='lgr-v' data-count='##LEVELS##'>-</div><div class='lgr-l'>Levels</div></div>
        <div class='lgr-tile lgr-yellow ##GRD_BAD_CLS##'><span class='thresh-pill'>##GRD_PILL##</span><div class='lgr-v' data-count='##GRIDS##'>-</div><div class='lgr-l'>Grids</div></div>
        <div class='lgr-tile lgr-pink ##LREV_BAD_CLS##'><span class='thresh-pill'>##LREV_PILL##</span><div class='lgr-v' data-count='##LINKED_REVIT##'>-</div><div class='lgr-l'>Linked Revit</div></div>
      </div>
    </div>
    <div class='card element-breakdown ##EB_BAD_CLS##' style='position:relative;'>
      <span class='thresh-pill'>##EB_PILL##</span>
      <div class='card-title'>Element Composition</div>
      <div class='eb-wrap flex-grow' style='flex-direction:row;align-items:center;gap:12px;'>
        <div class='eb-donut-area' style='flex-shrink:0;'>
          <div class='donut-chart' style='width:150px;height:150px;'>
            <div class='donut'></div>
            <div class='donut-inner'>
              <span class='dt-label'>TOTAL</span>
              <span class='dt-val ##EB_VAL_CLS##'>##TOTAL_EL_FMT##</span>
            </div>
          </div>
        </div>
        <div class='eb-cards' style='flex:1;'>
          <div class='eb-chip ##EB_MOD_BAD_CLS##' style='background:#efebff;border:##EB_MOD_BORDER##;position:relative;'><span class='eb-thresh'>##EB_MOD_PILL##</span><div class='eb-dot' style='background:#7054ff;'></div><div class='eb-count ##EB_MOD_VAL_CLS##'>##MODEL_EL_FMT##<span class='eb-inline-goal'>##EB_MOD_GOAL_INL##</span></div><div class='eb-name'>Model Elements</div><div class='eb-pct'>##EB_MOD_PCT_SLOT##</div></div>
          <div class='eb-chip ##EB_ANN_BAD_CLS##' style='background:#fff5e0;border:##EB_ANN_BORDER##;position:relative;'><span class='eb-thresh'>##EB_ANN_PILL##</span><div class='eb-dot' style='background:#ffb61d;'></div><div class='eb-count ##EB_ANN_VAL_CLS##'>##ANNOT_EL_FMT##<span class='eb-inline-goal'>##EB_ANN_GOAL_INL##</span></div><div class='eb-name'>Annotative Elements</div><div class='eb-pct'>##EB_ANN_PCT_SLOT##</div></div>
          <div class='eb-chip ##EB_OTH_BAD_CLS##' style='background:#ffe3f3;border:##EB_OTH_BORDER##;position:relative;'><span class='eb-thresh'>##EB_OTH_PILL##</span><div class='eb-dot' style='background:#ff3ec9;'></div><div class='eb-count ##EB_OTH_VAL_CLS##'>##OTHER_EL_FMT##<span class='eb-inline-goal'>##EB_OTH_GOAL_INL##</span></div><div class='eb-name'>Other Elements</div><div class='eb-pct'>##EB_OTH_PCT_SLOT##</div></div>
        </div>
      </div>
    </div>
    <div class='card sc-card'>
      <h2 class='card-title'>Shared Coordinates</h2>
      <div class='sc-list flex-grow'>
        <div class='coord-item'>
          <div class='coord-left'><div class='c-icon' style='background:#7054ff;'><i class='fa-solid fa-arrow-up'></i></div><span>NORTH</span></div>
          <strong>##SHARED_N## <span style='font-size:10px;font-weight:500;color:#94a3b8;'>##COORD_UNIT##</span></strong>
        </div>
        <div class='coord-item'>
          <div class='coord-left'><div class='c-icon' style='background:#ffb61d;'><i class='fa-solid fa-arrow-right'></i></div><span>EAST</span></div>
          <strong>##SHARED_E## <span style='font-size:10px;font-weight:500;color:#94a3b8;'>##COORD_UNIT##</span></strong>
        </div>
        <div class='coord-item'>
          <div class='coord-left'><div class='c-icon' style='background:#ff3ec9;'><i class='fa-solid fa-arrows-up-down'></i></div><span>ELEVATION</span></div>
          <strong>##SHARED_ELEV## <span style='font-size:10px;font-weight:500;color:#94a3b8;'>##COORD_UNIT##</span></strong>
        </div>
      </div>
    </div>
  </div>
  <!-- COLUMN 2: Views & Sheet Distribution / Worksets & Families / Disconnected Elements -->
  <div class='center-col'>
    <div class='card vs-card vs-combined'>
      <div class='card-title'>Views &amp; Sheet Distribution</div>
      <div class='vs-horiz flex-grow' style='gap:14px;'>
        <div class='vs-hrow' title='Views: ##VIEWS##'>
          <span class='vs-hlabel'>Views</span>
          <div class='vs-hbar-wrap'><div class='vs-bar ##VW_BAR_CLS##' style='width:##VW_BAR_W##%'></div></div><span class='vs-hval ##VW_VAL_CLS##'><span data-count='##VIEWS##'>-</span>##VIEWS_SUF##</span>
        </div>
        <div class='vs-hrow' title='Sheets: ##SHEETS##'>
          <span class='vs-hlabel'>Sheets</span>
          <div class='vs-hbar-wrap'><div class='vs-bar ##SH_BAR_CLS##' style='width:##SH_BAR_W##%'></div></div><span class='vs-hval ##SH_VAL_CLS##'><span data-count='##SHEETS##'>-</span>##SHEETS_SUF##</span>
        </div>
        <div class='vs-hrow' title='Views Not on Sheets: ##VNS## (goal &lt;= ##VNS_GOAL##, total views ##VIEWS##)'>
          <span class='vs-hlabel'>Not on Sheet</span>
          <div class='vs-hbar-wrap'><div class='vs-bar vs-bar-red' style='width:##VNS_BAR_W##%'></div></div><span class='vs-hval ##VNS_VAL_CLS##'><span data-count='##VNS##'>-</span>##VNS_SUF##</span>
        </div>
      </div>
    </div>
    <div class='card vs-card worksets-families'>
      <div class='card-title'>Worksets &amp; Families</div>
      <div class='vs-horiz flex-grow' style='gap:14px;'>
        <div class='vs-hrow' title='Worksets: ##WORKSETS##'>
          <span class='vs-hlabel'>Worksets</span>
          <div class='vs-hbar-wrap'><div class='vs-bar ##WK_BAR_CLS##' style='width:##WK_BAR_H##%'></div></div><span class='vs-hval ##WK_VAL_CLS##'><span data-count='##WORKSETS##'>-</span>##WK_SUF##</span>
        </div>
        <div class='vs-hrow' title='Families: ##TOTAL_FAM##'>
          <span class='vs-hlabel'>Families</span>
          <div class='vs-hbar-wrap'><div class='vs-bar ##FM_BAR_CLS##' style='width:##FM_BAR_H##%'></div></div><span class='vs-hval ##FM_VAL_CLS##'><span data-count='##TOTAL_FAM##'>-</span>##FM_SUF##</span>
        </div>
      </div>
    </div>
    <div class='card disconnects'>
      <div class='card-title'>Disconnected Elements</div>
      <div class='dc-meter-wrap flex-grow'>
        <div class='dc-meter-row'>
          <div class='dc-meter-head'>
            <div class='dc-meter-ico ##DC_WALL_ICLS##'><i class='fa-solid fa-layer-group'></i></div>
            <span class='dc-meter-name'>Walls</span>
            <span class='dc-meter-count ##DC_WALL_VCLS##'>##WALLS_NC##<span class='dc-goal-suf'>##WALL_DC_SUF##</span></span>
          </div>
          <div class='dc-meter-track'>
            <div class='dc-meter-fill ##DC_WALL_FCLS##' style='width:##WALL_METER_W##%'></div>
          </div>
        </div>
        <div class='dc-meter-row'>
          <div class='dc-meter-head'>
            <div class='dc-meter-ico ##DC_PIPE_ICLS##'><i class='fa-solid fa-bars'></i></div>
            <span class='dc-meter-name'>Pipes</span>
            <span class='dc-meter-count ##DC_PIPE_VCLS##'>##PIPES_NC##<span class='dc-goal-suf'>##PIPE_DC_SUF##</span></span>
          </div>
          <div class='dc-meter-track'>
            <div class='dc-meter-fill ##DC_PIPE_FCLS##' style='width:##PIPE_METER_W##%'></div>
          </div>
        </div>
        <div class='dc-meter-row'>
          <div class='dc-meter-head'>
            <div class='dc-meter-ico ##DC_DUCT_ICLS##'><i class='fa-solid fa-wind'></i></div>
            <span class='dc-meter-name'>Ducts</span>
            <span class='dc-meter-count ##DC_DUCT_VCLS##'>##DUCTS_NC##<span class='dc-goal-suf'>##DUCT_DC_SUF##</span></span>
          </div>
          <div class='dc-meter-track'>
            <div class='dc-meter-fill ##DC_DUCT_FCLS##' style='width:##DUCT_METER_W##%'></div>
          </div>
        </div>
      </div>
    </div>
  </div>
  <!-- COLUMN 3: Key Performance Indicators / Cleanup Recommendations / Model Integrity Scan -->
  <div class='kpi-col'>
    <div class='card crit-card' style='flex:1.6;'>
      <div class='card-title'>Key Performance Indicators</div>
      <div class='crit-3'>
        <div class='ct-tile ##CT_WARN_CLS##' title='Warnings: ##WARNINGS## / Threshold: ##WARN_GOAL##'>
          <span class='thresh-pill'>##WARN_PILL##</span>
          <div class='ct-tile-icon'><i class='fa-solid fa-triangle-exclamation'></i></div>
          <div class='ctv ##CT_WARN_VCLS##'>##WARNINGS##</div>
          <div class='ctl'>Warnings</div>
        </div>
        <div class='ct-tile ##CT_DUP_CLS##' title='Duplicate Elements: ##DUP_ELEMS## / Threshold: ##DUP_GOAL##'>
          <span class='thresh-pill'>##DUP_PILL##</span>
          <div class='ct-tile-icon'><i class='fa-solid fa-clone'></i></div>
          <div class='ctv ##CT_DUP_VCLS##'>##DUP_ELEMS##</div>
          <div class='ctl'>Duplicates</div>
        </div>
        <div class='ct-tile ##CT_DWG_CLS##' title='Imported DWG: ##IMPORTED_DWG## / Threshold: ##DWG_GOAL##'>
          <span class='thresh-pill'>##DWG_PILL##</span>
          <div class='ct-tile-icon'><i class='fa-solid fa-file-import'></i></div>
          <div class='ctv ##CT_DWG_VCLS##'>##IMPORTED_DWG##</div>
          <div class='ctl'>Imported DWG</div>
        </div>
        <div class='ct-tile ##CT_NN_CLS##' title='Non-Native Styles: ##NON_NATIVE## / Threshold: ##NN_GOAL##'>
          <span class='thresh-pill'>##NN_PILL##</span>
          <div class='ct-tile-icon'><i class='fa-solid fa-palette'></i></div>
          <div class='ctv ##CT_NN_VCLS##'>##NON_NATIVE##</div>
          <div class='ctl'>Non-Native Styles</div>
        </div>
      </div>
    </div>
    <div class='card' style='flex:0.9;'>
      <div class='card-title'>Cleanup Recommendations</div>
      <div class='nn-boxes' style='margin-top:8px;'>
        <div class='nn-box ##NN_OVER_BORDER##'>
          <span class='thresh-pill'>##OVER_PILL##</span>
          <div class='nn-val ##NN_OVER_VCOL##'>##OVERSIZED##</div>
          <div class='nn-label'>Oversized Families</div>
        </div>
        <div class='nn-box ##NN_PURG_BORDER##'>
          <span class='thresh-pill'>##PURG_PILL##</span>
          <div class='nn-val ##NN_PURG_VCOL##'>##PURGEABLE##</div>
          <div class='nn-label'>Purgeable Elements</div>
        </div>
      </div>
    </div>
    <div class='card quality-scan'>
      <div class='card-title'>Model Integrity Scan</div>
      <div class='bs-list flex-grow' style='margin-top:8px;'>
        <div class='bs-col ##INP_BAD_CLS##' style='position:relative;'>
          <span class='thresh-pill'>##INP_PILL##</span>
          <div class='bs-val'>##INPLACE##</div>
          <div class='bs-label'>In-Place<br>Families</div>
        </div>
        <div class='bs-col ##UNP_BAD_CLS##' style='position:relative;'>
          <span class='thresh-pill'>##UNP_PILL##</span>
          <div class='bs-val'>##UNPLACED_ROOMS##</div>
          <div class='bs-label'>Unplaced<br>Rooms</div>
        </div>
        <div class='bs-col ##UNE_BAD_CLS##' style='position:relative;'>
          <span class='thresh-pill'>##UNE_PILL##</span>
          <div class='bs-val'>##UNENCLOSED_ROOMS##</div>
          <div class='bs-label'>Unenclosed<br>Rooms</div>
        </div>
      </div>
    </div>
  </div>
  <!-- COLUMN 4: Optimization Indicators (renamed Standard Model Compliance) -->
  <div class='opt-col'>
    <div class='card'>
      <div class='card-title'>Optimization Indicators</div>
      <div class='opt-list'>
        <div class='opt-tile ##OPT_MGRP_CLS##' style='position:relative;'><span class='thresh-pill'>##MGRP_PILL##</span><div class='opt-v'>##MODEL_GROUPS##</div><div class='opt-l'>Model Groups</div></div>
        <div class='opt-tile ##OPT_DGRP_CLS##' style='position:relative;'><span class='thresh-pill'>##DGRP_PILL##</span><div class='opt-v'>##DETAIL_GROUPS##</div><div class='opt-l'>Detail Groups</div></div>
        <div class='opt-tile ##OPT_LDWG_CLS##' style='position:relative;'><span class='thresh-pill'>##LDWG_PILL##</span><div class='opt-v'>##LINKED_DWGS##</div><div class='opt-l'>Linked DWGs</div></div>
        <div class='opt-tile ##OPT_RAST_CLS##' style='position:relative;'><span class='thresh-pill'>##RAST_PILL##</span><div class='opt-v'>##RASTER##</div><div class='opt-l'>Raster Images</div></div>
        <div class='opt-tile ##OPT_VT_CLS##' style='position:relative;'><span class='thresh-pill'>##VT_PILL##</span><div class='opt-v'>##VIEW_TEMPLATES##</div><div class='opt-l'>View Templates</div></div>
        <div class='opt-tile ##OPT_DES_CLS##' style='position:relative;'><span class='thresh-pill'>##DES_PILL##</span><div class='opt-v'>##DESIGNS##</div><div class='opt-l'>Design Options</div></div>
      </div>
    </div>
  </div>
</main>
</div>
<script>
(function(){
var m=['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
var p=function(v){return String(v).padStart(2,'0');};
function tick(){var n=new Date();document.getElementById('clock').textContent=n.getDate()+' '+m[n.getMonth()]+' '+n.getFullYear()+' \u00b7 '+p(n.getHours())+':'+p(n.getMinutes());}
tick();setInterval(tick,60000);
})();
(function(){
  function animateCount(el,target,dur){
    var start=0,startTime=null;
    function step(ts){
      if(!startTime)startTime=ts;
      var p=Math.min((ts-startTime)/dur,1);
      var eased=1-Math.pow(1-p,3);
      el.textContent=Math.floor(eased*target).toLocaleString();
      if(p<1)requestAnimationFrame(step);
      else el.textContent=target.toLocaleString();
    }
    requestAnimationFrame(step);
  }
  setTimeout(function(){
    document.querySelectorAll('[data-count]').forEach(function(el){
      var v=parseInt(el.getAttribute('data-count'),10);
      if(!isNaN(v))animateCount(el,v,800);
    });
    var fsEl=document.querySelector('.stat-value');
    if(fsEl){
      var fsTxt=fsEl.textContent.trim();
      var match=fsTxt.match(/^([\d.]+)\s*(.*)$/);
      if(match){
        var num=parseFloat(match[1]),unit=match[2],startTime=null;
        function stepFS(ts){
          if(!startTime)startTime=ts;
          var p=Math.min((ts-startTime)/1000,1);
          var eased=1-Math.pow(1-p,3);
          fsEl.innerHTML=(eased*num).toFixed(num%1===0?0:1)+' <span>'+unit+'</span>';
          if(p<1)requestAnimationFrame(stepFS);
          else fsEl.innerHTML=match[1]+' <span>'+unit+'</span>';
        }
        fsEl.innerHTML='0 <span>'+unit+'</span>';
        requestAnimationFrame(stepFS);
      }
    }
  },400);
})();
function renderDonutCanvas(targetDonut){
  // Read percentages from the eb-chip .eb-pct elements (chip-refactor renamed
  // the old .eb-card-pct class). Fall back to even thirds if not found.
  var pctEls=document.querySelectorAll('.eb-chip .eb-pct');
  var p1=33.3,p2=33.3,p3=33.3;
  if(pctEls.length>=3){
    p1=parseFloat(pctEls[0].textContent)||0;
    p2=parseFloat(pctEls[1].textContent)||0;
    p3=parseFloat(pctEls[2].textContent)||0;
  }
  // Match the canvas size to the donut's ACTUAL rendered box (the donut div
  // is sized inline to 110px in the current layout — previously hard-coded
  // 140px here caused the canvas to overflow and overlap the chips below).
  var sz=targetDonut.offsetWidth||targetDonut.clientWidth||110;
  var cx=sz/2,cy=sz/2,r=sz/2,ir=sz/2-Math.round(sz*0.17);
  var cv=document.createElement('canvas');
  cv.width=sz*2;cv.height=sz*2;
  cv.style.cssText='width:'+sz+'px;height:'+sz+'px;position:absolute;top:0;left:0;border-radius:50%;';
  var ctx=cv.getContext('2d');ctx.scale(2,2);
  var segs=[{p:p1,c:'#7054ff'},{p:p2,c:'#ffb61d'},{p:p3,c:'#ff3ec9'}];
  var start=-Math.PI/2;
  for(var i=0;i<segs.length;i++){
    var angle=segs[i].p/100*Math.PI*2;
    ctx.beginPath();ctx.moveTo(cx,cy);
    ctx.arc(cx,cy,r,start,start+angle);
    ctx.closePath();ctx.fillStyle=segs[i].c;ctx.fill();
    start+=angle;
  }
  ctx.beginPath();ctx.arc(cx,cy,ir,0,Math.PI*2);ctx.fillStyle='#ffffff';ctx.fill();
  targetDonut.style.background='none';
  targetDonut.appendChild(cv);
}
function dlDash(){
  var d=document.querySelector('.dashboard');
  var btn=document.querySelector('.btn-download');
  if(btn)btn.style.display='none';
  // Pre-render donut as canvas on the LIVE DOM before cloning
  var origDonut=document.querySelector('.donut');
  var hadCanvas=origDonut&&origDonut.querySelector('canvas');
  if(origDonut&&!hadCanvas){
    renderDonutCanvas(origDonut);
  }
  var w=d.offsetWidth,h=d.offsetHeight;
  html2canvas(d,{
    scale:2,useCORS:true,allowTaint:true,backgroundColor:'#f0f4f8',
    width:w,height:h,windowWidth:w,windowHeight:h,scrollX:0,scrollY:0,
    onclone:function(doc,el){
      el.style.width=w+'px';el.style.height=h+'px';
      var s=doc.createElement('style');
      s.textContent='*{overflow:visible!important;animation:none!important;transition:none!important;}body{width:'+w+'px!important;height:'+h+'px!important;overflow:visible!important;}';
      doc.head.appendChild(s);
    }
  }).then(function(c){
    if(btn)btn.style.display='';
    // Restore original donut (remove canvas, restore conic-gradient)
    if(origDonut&&!hadCanvas){
      var cv=origDonut.querySelector('canvas');
      if(cv)origDonut.removeChild(cv);
      origDonut.style.background='';
    }
    try{
      var jsPDFCtor=(window.jspdf&&window.jspdf.jsPDF)||window.jsPDF;
      if(!jsPDFCtor){
        var a=document.createElement('a');a.download='##REPORT_FILENAME##'.replace(/\.pdf$/i,'.png');a.href=c.toDataURL('image/png');a.click();
        return;
      }
      var imgW=c.width,imgH=c.height;
      var orientation=imgW>=imgH?'landscape':'portrait';
      var pdf=new jsPDFCtor({orientation:orientation,unit:'pt',format:[imgW,imgH]});
      pdf.addImage(c.toDataURL('image/png'),'PNG',0,0,imgW,imgH,undefined,'FAST');
      var fn='##REPORT_FILENAME##';
      if(!/\.pdf$/i.test(fn))fn=fn.replace(/\.(png|jpg|jpeg)$/i,'')+'.pdf';
      pdf.save(fn);
    }catch(e){
      var a=document.createElement('a');a.download='##REPORT_FILENAME##'.replace(/\.pdf$/i,'.png');a.href=c.toDataURL('image/png');a.click();
    }
  }).catch(function(){
    if(btn)btn.style.display='';
    if(origDonut&&!hadCanvas){
      var cv=origDonut.querySelector('canvas');
      if(cv)origDonut.removeChild(cv);
      origDonut.style.background='';
    }
  });
}
</script>
</body>
</html>";

            // Map workset bar class to new naming
            string wkBarClsNew = wkBarCls == "bg-grey" ? "bar-grey" : "bar-navy";
            // Critical parameter tile classes — turn red only once the metric actually hits
            // (or exceeds) its configured goal, so the red background matches what users
            // think "crossing the threshold" means. Soft warning state (50%+) is conveyed
            // by the amber/orange header pill via warnTile/warnBdg, not by the red tile.
            // Logo color theme: green when 0, logo-orange (#F47B20) when nonzero but
            // under the server-configured goal, red when value >= goal. Matches the
            // user's "goal limit reach after the color was red" requirement for the
            // 4 Performance Impacts tiles.
            // 2-tier value-color classifier per user spec:
            //   value >  threshold        -> red ("ct-bad")
            //   value <= threshold OR no threshold OR value == 0 -> purple (default)
            string Tier(int value, int goal) =>
                goal > 0 && value > goal ? "ct-bad" : "";
            string ctWarnCls = Tier(warnings, warnGoal);
            string ctWarnVcls = "";
            string ctDupCls = Tier(dupElems, dupGoal);
            string ctDupVcls = "";
            string ctDwgCls = Tier(importedDwg, dwgGoal);
            string ctDwgVcls = "";
            string ctNnCls = Tier(nonNative, nonNativeGoal);
            string ctNnVcls = "";
            // Disconnect status class
            string dcStatusCls = allConnected ? "ok" : "warn";

            // Inline unregistered banner — replaces the old blocking "Model Not Registered"
            // popup. When the dashboard opens for a model that isn't in registered_models,
            // we still show all locally-computed metrics and nudge the user to register
            // (server-backed goals/sync will stay empty until they do).
            string unregBanner = _isRegistered
                ? ""
                : "<div class='unreg-banner'><span class='unreg-icon'>&#9888;</span>"
                    + "<div class='unreg-text'><b>This model is not registered with ZeManage.</b>"
                    + " Locally-computed metrics are shown below; server-backed goals and sync are unavailable."
                    + " Use <b>Register Model</b> on the ribbon to enable full features.</div></div>";

            html = html
                .Replace("##FULL_GUID##", E(_modelGuid ?? ""))
                .Replace("##SHORT_GUID##", E(shortGuid))
                .Replace("##UNREG_BANNER##", unregBanner)
                .Replace("##MODEL_INFO##", E(modelInfo))
                .Replace("##SYNC_TIME_DISP##", E(syncTimeDisp))
                .Replace("##SYNC_BY_DISP##", !string.IsNullOrEmpty(syncByDisp) ? "by " + E(syncByDisp) : "")
                .Replace("##HEADER_PILLS##", BuildHeaderPills(metrics))
                .Replace("##REPORT_FILENAME##", $"ZeManage_Health_{E(modelNameDisp).Replace(" ", "_")}_{DateTime.UtcNow:yyyyMMdd_HHmm}.pdf")
                .Replace("##WARNINGS##", NR(warnings, hasSync))
                .Replace("##WARN_GOAL##", warnGoal.ToString("N0"))
                .Replace("##DUP_ELEMS##", NR(dupElems, hasSync))
                .Replace("##DUP_GOAL##", dupGoal.ToString("N0"))
                .Replace("##INPLACE##", NR(inplace, hasPer))
                .Replace("##INP_GOAL##", inpGoal.ToString("N0"))
                .Replace("##VNS##", NR(vns, hasPer))
                .Replace("##VNS_GOAL##", vnsGoal.ToString("N0"))
                .Replace("##FS_PILL_CLS##", fsPct >= 0.8 ? "pill-red" : fsPct >= 0.5 ? "pill-yellow" : "pill-green")
                .Replace("##FS_DOT_CLS##", fsPct >= 0.8 ? "red-dot" : fsPct >= 0.5 ? "dot" : "green-dot")
                // FILE SIZE tile box + icon classes â€” turn red when the goal is actually breached.
                // fsPct is clamped to [0,1] so use raw bytes/goal comparison for the breach check.
                .Replace("##FS_BOX_CLS##", (rawGoalMB > 0 && fsBytes >= fsGoal) ? "light-red-box" : "light-blue-box")
                .Replace("##FS_ICON_CLS##", (rawGoalMB > 0 && fsBytes >= fsGoal) ? "red-icon" : "blue-icon")
                .Replace("##FS_GOAL_DISP##", goalLabel)
                .Replace("##FILE_SIZE##", E(fileSize))
                .Replace("##LINKED_REVIT##", NR(linkedRevit, hasSync))
                .Replace("##LVL_STAT_W##", lvlStatW.ToString())
                .Replace("##GRD_STAT_W##", grdStatW.ToString())
                .Replace("##LREV_STAT_W##", lrevStatW.ToString())
                .Replace("##PROJECT_NAME##", E(projectName))
                .Replace("##MODEL_NAME_DISP##", E(modelNameDisp))
                .Replace("##SHARED_N##", sharedN)
                .Replace("##SHARED_E##", sharedE)
                .Replace("##SHARED_ELEV##", sharedElev)
                .Replace("##COORD_UNIT##", ss?.SharedCoordUnit ?? "mm")
                .Replace("##CG_ARC_PATH##", cgArcPath)
                .Replace("##CG_STROKE_CLS##", cgStrokeCls)
                .Replace("##CG_DASH_OFF##", cgDashOff)
                .Replace("##HEALTH_PCT##", F0(healthPct))
                .Replace("##HEALTH_LABEL##", healthLabel)
                .Replace("##TOTAL_EL_FMT##", NR(totalEl, hasPer))
                .Replace("##MOD_PCT_DISP##", F1(modPct * 100))
                .Replace("##MOD_ANN_END##", modAnnEnd)
                .Replace("##MODEL_EL_FMT##", NR(modelEl, hasPer))
                .Replace("##OTH_PCT_DISP##", F1(othPct * 100))
                .Replace("##OTHER_EL_FMT##", NR(otherEl, hasPer))
                .Replace("##ANN_PCT_DISP##", F1(annPct * 100))
                .Replace("##ANNOT_EL_FMT##", NR(annotEl, hasPer))
                .Replace("##LEVELS##", NR(levels, hasSync))
                .Replace("##GRIDS##", NR(grids, hasSync))
                .Replace("##LVL_BAR_H##", lvlBarH2)
                .Replace("##GRD_BAR_H##", grdBarH2)
                .Replace("##VIEWS##", NR(totalViews, hasSync))
                .Replace("##SHEETS##", NR(sheets, hasSync))
                // Threshold suffix for Views & Sheets rows. Views uses
                // MaxTotalViewsCount when set; Sheets has no dedicated threshold
                // (shows 0) — both still render as "value/threshold" to match
                // Not on Sheet's format.
                .Replace("##VIEWS_THR##", (_healthGoals?.MaxTotalViewsCount ?? 0).ToString("N0"))
                .Replace("##SHEETS_THR##", "0")
                .Replace("##VW_BAR_W##", vwBarW2)
                .Replace("##SH_BAR_W##", shBarW2)
                .Replace("##WORKSETS##", NR(worksets, hasSync))
                .Replace("##TOTAL_FAM##", NR(totalFam, hasSync))
                .Replace("##WK_BAR_H##", wkBarH2)
                .Replace("##FM_BAR_H##", fmBarH2)
                .Replace("##WK_BAR_CLS##", wkBarClsNew)
                .Replace("##MODEL_GROUPS##", NR(modelGroups, hasSync))
                .Replace("##DETAIL_GROUPS##", NR(detailGroups, hasSync))
                .Replace("##GROUPS##", NR(groups, hasSync))
                .Replace("##LINKED_DWGS##", NR(linkedDwgs, hasSync))
                .Replace("##RASTER##", NR(rasterImgs, hasSync))
                .Replace("##DESIGNS##", NR(designs, hasSync))
                .Replace("##NON_NATIVE##", NR(nonNative, hasSync))
                .Replace("##BP_MGRP_ARC##", bpMGrpArc)
                .Replace("##BP_MGRP_CLR##", bpMGrpClr)
                .Replace("##BP_MGRP_SCR##", bpMGrpScore.ToString())
                .Replace("##BP_DGRP_ARC##", bpDGrpArc)
                .Replace("##BP_DGRP_CLR##", bpDGrpClr)
                .Replace("##BP_DGRP_SCR##", bpDGrpScore.ToString())
                .Replace("##BP_GRP_ARC##", bpGrpArc)
                .Replace("##BP_GRP_CLR##", bpGrpClr)
                .Replace("##BP_GRP_SCR##", bpGrpScore.ToString())
                .Replace("##BP_LDWG_ARC##", bpLdwgArc)
                .Replace("##BP_LDWG_CLR##", bpLdwgClr)
                .Replace("##BP_LDWG_SCR##", bpLdwgScore.ToString())
                .Replace("##BP_RAST_ARC##", bpRastArc)
                .Replace("##BP_RAST_CLR##", bpRastClr)
                .Replace("##BP_RAST_SCR##", bpRastScore.ToString())
                .Replace("##BP_DES_ARC##", bpDesArc)
                .Replace("##BP_DES_CLR##", bpDesClr)
                .Replace("##BP_DES_SCR##", bpDesScore.ToString())
                .Replace("##VIEW_TEMPLATES##", NR(viewTemplates, hasSync))
                .Replace("##BP_VT_ARC##", bpVtArc)
                .Replace("##BP_VT_CLR##", bpVtClr)
                .Replace("##BP_VT_SCR##", bpVtScore.ToString())
                .Replace("##WALLS_NC##", wallsN.HasValue ? wallsN.Value.ToString("N0") : "-")
                .Replace("##PIPES_NC##", pipesN.HasValue ? pipesN.Value.ToString("N0") : "-")
                .Replace("##DUCTS_NC##", ductsN.HasValue ? ductsN.Value.ToString("N0") : "-")
                // Per-row brand colors when no disconnects (Walls=1, Pipes=2, Ducts=3);
                // fall back to the existing red "bad" classes when the count is nonzero.
                .Replace("##DC_WALL_ICLS##", walls > 0 ? "dc-ico-bad" : "dc-ico-walls")
                .Replace("##DC_PIPE_ICLS##", pipes > 0 ? "dc-ico-bad" : "dc-ico-pipes")
                .Replace("##DC_DUCT_ICLS##", ducts > 0 ? "dc-ico-bad" : "dc-ico-ducts")
                // Disconnect tile colour rule:
                //   • threshold configured (> 0) → red ONLY when count >= threshold
                //   • threshold NOT configured  → keep historical "any disconnect = red"
                //     behaviour so we don't regress installs that never set a goal.
                .Replace("##DC_WALL_VCLS##", ((_healthGoals?.MaxWallsNotConnectedCount ?? 0) > 0
                    ? (walls >= _healthGoals!.MaxWallsNotConnectedCount!.Value ? "dc-cnt-bad" : "dc-cnt-walls")
                    : (walls > 0 ? "dc-cnt-bad" : "dc-cnt-walls")))
                .Replace("##DC_PIPE_VCLS##", ((_healthGoals?.MaxPipesNotConnectedCount ?? 0) > 0
                    ? (pipes >= _healthGoals!.MaxPipesNotConnectedCount!.Value ? "dc-cnt-bad" : "dc-cnt-pipes")
                    : (pipes > 0 ? "dc-cnt-bad" : "dc-cnt-pipes")))
                .Replace("##DC_DUCT_VCLS##", ((_healthGoals?.MaxDuctsNotConnectedCount ?? 0) > 0
                    ? (ducts >= _healthGoals!.MaxDuctsNotConnectedCount!.Value ? "dc-cnt-bad" : "dc-cnt-ducts")
                    : (ducts > 0 ? "dc-cnt-bad" : "dc-cnt-ducts")))
                // Inline "/N" suffix per row — empty when the admin hasn't set a
                // threshold for that row, so .dc-goal-suf:empty hides the wrapper
                // and the layout matches the original count-only rendering.
                .Replace("##WALL_DC_SUF##", (_healthGoals?.MaxWallsNotConnectedCount ?? 0) > 0 ? $"/{_healthGoals!.MaxWallsNotConnectedCount!.Value:N0}" : "")
                .Replace("##PIPE_DC_SUF##", (_healthGoals?.MaxPipesNotConnectedCount ?? 0) > 0 ? $"/{_healthGoals!.MaxPipesNotConnectedCount!.Value:N0}" : "")
                .Replace("##DUCT_DC_SUF##", (_healthGoals?.MaxDuctsNotConnectedCount ?? 0) > 0 ? $"/{_healthGoals!.MaxDuctsNotConnectedCount!.Value:N0}" : "")
                // Element Composition card — top-right "Threshold: N" pill + red
                // ring/total when the configured limit on Total Elements is met.
                // Per-user request 2026-06-10: card-level overall Element
                // Composition Threshold pill is removed. The TOTAL donut
                // colour rule (EB_VAL_CLS / EB_BAD_CLS) is preserved.
                .Replace("##EB_PILL##",    "")
                .Replace("##EB_BAD_CLS##", (_healthGoals?.MaxTotalElementsCount ?? 0) > 0 && totalEl >= _healthGoals!.MaxTotalElementsCount!.Value ? "ct-bad" : "")
                .Replace("##EB_VAL_CLS##", (_healthGoals?.MaxTotalElementsCount ?? 0) > 0 && totalEl >= _healthGoals!.MaxTotalElementsCount!.Value ? "color-red" : "")
                // Per-user request 2026-06-10: the per-chip Threshold pill is
                // intentionally suppressed since the inline " / N" next to
                // the count now carries that information. Keeping these
                // replacements as constants empty also future-proofs against
                // duplicate threshold visuals if anyone later adds new chip
                // CSS that re-shows .eb-thresh.
                .Replace("##EB_MOD_PILL##",    "")
                .Replace("##EB_ANN_PILL##",    "")
                .Replace("##EB_OTH_PILL##",    "")
                .Replace("##EB_MOD_BAD_CLS##", (_healthGoals?.MaxModelElementsCount      ?? 0) > 0 && modelEl >= _healthGoals!.MaxModelElementsCount!.Value      ? "ct-bad" : "")
                .Replace("##EB_ANN_BAD_CLS##", (_healthGoals?.MaxAnnotativeElementsCount ?? 0) > 0 && annotEl >= _healthGoals!.MaxAnnotativeElementsCount!.Value ? "ct-bad" : "")
                .Replace("##EB_OTH_BAD_CLS##", (_healthGoals?.MaxOtherElementsCount      ?? 0) > 0 && otherEl >= _healthGoals!.MaxOtherElementsCount!.Value      ? "ct-bad" : "")
                .Replace("##EB_MOD_VAL_CLS##", (_healthGoals?.MaxModelElementsCount      ?? 0) > 0 && modelEl >= _healthGoals!.MaxModelElementsCount!.Value      ? "color-red" : "")
                .Replace("##EB_ANN_VAL_CLS##", (_healthGoals?.MaxAnnotativeElementsCount ?? 0) > 0 && annotEl >= _healthGoals!.MaxAnnotativeElementsCount!.Value ? "color-red" : "")
                .Replace("##EB_OTH_VAL_CLS##", (_healthGoals?.MaxOtherElementsCount      ?? 0) > 0 && otherEl >= _healthGoals!.MaxOtherElementsCount!.Value      ? "color-red" : "")
                // Per-user request 2026-06-05: border stays the chip's pastel
                // brand colour regardless of threshold. Only the count / pct
                // text turns red (handled via EB_*_VAL_CLS + the .ct-bad CSS
                // rule above). Border placeholders are constants now.
                .Replace("##EB_MOD_BORDER##", "1px solid #d9d2ff")
                .Replace("##EB_ANN_BORDER##", "1px solid #ffe4b3")
                .Replace("##EB_OTH_BORDER##", "1px solid #ffc1e6")
                // Per-user request 2026-06-10: show the threshold inline next
                // to the count ("72,354 / 10"). The previous separate Threshold
                // pill at the top-right corner AND the right-edge "/N" suffix
                // are intentionally removed in favour of a single inline goal
                // beside the number — the user marked the redundant copies
                // crossed-out on their screenshot. The eb-inline-goal span
                // inherits the same red-when-over-threshold class as the count.
                .Replace("##EB_MOD_GOAL_INL##", (_healthGoals?.MaxModelElementsCount      ?? 0) > 0 ? $" / {_healthGoals!.MaxModelElementsCount!.Value:N0}"      : "")
                .Replace("##EB_ANN_GOAL_INL##", (_healthGoals?.MaxAnnotativeElementsCount ?? 0) > 0 ? $" / {_healthGoals!.MaxAnnotativeElementsCount!.Value:N0}" : "")
                .Replace("##EB_OTH_GOAL_INL##", (_healthGoals?.MaxOtherElementsCount      ?? 0) > 0 ? $" / {_healthGoals!.MaxOtherElementsCount!.Value:N0}"      : "")
                // The top-right Threshold pill and the right-edge percentage
                // slot are both forced empty: pill is redundant once the
                // inline /N is shown; the percentage was struck out by the
                // user on the same screenshot. .thresh-pill:empty + .eb-pct
                // with no content collapse cleanly to zero visual width.
                .Replace("##EB_MOD_PCT_SLOT##", "")
                .Replace("##EB_ANN_PCT_SLOT##", "")
                .Replace("##EB_OTH_PCT_SLOT##", "")
                // Disconnect bar fill — same threshold rule as the count text:
                //   • goal configured → red ONLY when count >= goal
                //   • goal NOT configured → preserve historical "any disconnect = red"
                .Replace("##DC_WALL_FCLS##", ((_healthGoals?.MaxWallsNotConnectedCount ?? 0) > 0
                    ? (walls >= _healthGoals!.MaxWallsNotConnectedCount!.Value ? "dc-fill-bad" : "dc-fill-walls")
                    : (walls > 0 ? "dc-fill-bad" : "dc-fill-walls")))
                .Replace("##DC_PIPE_FCLS##", ((_healthGoals?.MaxPipesNotConnectedCount ?? 0) > 0
                    ? (pipes >= _healthGoals!.MaxPipesNotConnectedCount!.Value ? "dc-fill-bad" : "dc-fill-pipes")
                    : (pipes > 0 ? "dc-fill-bad" : "dc-fill-pipes")))
                .Replace("##DC_DUCT_FCLS##", ((_healthGoals?.MaxDuctsNotConnectedCount ?? 0) > 0
                    ? (ducts >= _healthGoals!.MaxDuctsNotConnectedCount!.Value ? "dc-fill-bad" : "dc-fill-ducts")
                    : (ducts > 0 ? "dc-fill-bad" : "dc-fill-ducts")))
                .Replace("##WALL_METER_W##", dcMaxVal > 0 ? Math.Max(8, (int)(100.0 * walls / dcMaxVal)).ToString() : "100")
                .Replace("##PIPE_METER_W##", dcMaxVal > 0 ? Math.Max(8, (int)(100.0 * pipes / dcMaxVal)).ToString() : "100")
                .Replace("##DUCT_METER_W##", dcMaxVal > 0 ? Math.Max(8, (int)(100.0 * ducts / dcMaxVal)).ToString() : "100")
                .Replace("##DC_STATUS_CLS##", dcStatusCls)
                .Replace("##CN_STATUS_ICON##", cnStatusIcon)
                .Replace("##CN_STATUS##", cnStatus)
                .Replace("##GOAL_PILL_CLS##", goalPillCls)
                .Replace("##Y_AXIS_LABELS##", yAxisLabels)
                .Replace("##TREND_AREA##", trendAreaPoly)
                .Replace("##TREND_POLYLINE##", trendPolyline)
                .Replace("##TREND_DOTS##", trendDotsStr)
                .Replace("##GOAL_LINE_Y##", goalLineYStr)
                .Replace("##CT_WARN_CLS##", ctWarnCls)
                .Replace("##CT_WARN_VCLS##", ctWarnVcls)
                .Replace("##CT_DUP_CLS##", ctDupCls)
                .Replace("##CT_DUP_VCLS##", ctDupVcls)
                .Replace("##DUP_PCT_DISP##", dupPctDisp)
                .Replace("##CT_DWG_CLS##", ctDwgCls)
                .Replace("##CT_DWG_VCLS##", ctDwgVcls)
                .Replace("##DWG_GOAL##", dwgGoal.ToString())
                .Replace("##NN_GOAL##", nonNativeGoal.ToString())
                .Replace("##IMPORTED_DWG##", NR(importedDwg, hasSync))
                .Replace("##CT_NN_CLS##", ctNnCls)
                .Replace("##CT_NN_VCLS##", ctNnVcls)
                .Replace("##NN_OVER_BORDER##", nnOverBorder)
                .Replace("##NN_OVER_VCOL##", nnOverVcol2)
                .Replace("##NN_OVER_GOAL##", nnOverGoalDisp)
                .Replace("##NN_PURG_GOAL##", nnPurgGoalDisp)
                .Replace("##OVERSIZED##", NR(oversized, hasMan))
                .Replace("##NN_PURG_BORDER##", nnPurgBorder)
                .Replace("##NN_PURG_VCOL##", nnPurgVcol2)
                .Replace("##PURGEABLE##", NR(purgeable, hasMan))
                .Replace("##VNS_BAR_W##", vnsBarW.ToString())
                .Replace("##VNS_BAR_CLS##", vns >= vnsGoal ? "vs-bar-red" : vns > 0 ? "vs-bar-amber" : "vs-bar-green")
                .Replace("##VNS_VAL_CLS##", vns >= vnsGoal ? "color-red" : "")
                .Replace("##VNS_VCOL##", vnsVcol2)
                .Replace("##INP_VCOL##", inpVcol2)
                .Replace("##VNS_SPARK##", vnsSpark)
                .Replace("##INP_SPARK##", inpSpark)
                .Replace("##ROOM_TOTAL##", NR(roomTotal, hasPer))
                .Replace("##ROOM_SUB_TXT2##", roomSubTxt2)
                .Replace("##UNPLACED_ROOMS##", NR(unplaced, hasPer))
                .Replace("##UNENCLOSED_ROOMS##", NR(unenclosed, hasPer))
                .Replace("##UNPLACED_CLS##", unplaced > 0 ? "" : "")
                .Replace("##UNENCLOSED_CLS##", unenclosed > 0 ? "" : "")
                // === Threshold pills (per user: show only when the server returned
                // a value > 0 for that parameterCode; otherwise hide via .thresh-pill:empty)
                // Helper returns "Threshold: N" when val>0 else "". Each card is paired
                // with its parameterCode → HealthMonitorProtection field.
                // KPI tiles:
                .Replace("##WARN_PILL##", warnGoal > 0 ? $"Threshold: {warnGoal:N0}" : "")
                .Replace("##DUP_PILL##",  dupGoal  > 0 ? $"Threshold: {dupGoal:N0}"  : "")
                .Replace("##DWG_PILL##",  dwgGoal  > 0 ? $"Threshold: {dwgGoal:N0}"  : "")
                .Replace("##NN_PILL##",   nonNativeGoal > 0 ? $"Threshold: {nonNativeGoal:N0}" : "")
                // Cleanup tiles:
                .Replace("##OVER_PILL##", oversizedGoalApi > 0 ? $"Threshold: {oversizedGoalApi:N0}" : "")
                .Replace("##PURG_PILL##", purgeGoalApi    > 0 ? $"Threshold: {purgeGoalApi:N0}"    : "")
                // Model Integrity Scan tiles (parameterCode → field):
                //   InplaceFamiliesCount  -> MaxInPlaceFamilyCount
                //   UnplacedRoomsCount    -> (no mapped field)
                //   UnenclosedRoomsCount  -> (no mapped field)
                .Replace("##INP_PILL##", inpGoal > 0 ? $"Threshold: {inpGoal:N0}" : "")
                .Replace("##UNP_PILL##", (_healthGoals?.MaxUnplacedRoomsCount   ?? 0) > 0 ? $"Threshold: {_healthGoals!.MaxUnplacedRoomsCount!.Value:N0}"   : "")
                .Replace("##UNE_PILL##", (_healthGoals?.MaxUnenclosedRoomsCount ?? 0) > 0 ? $"Threshold: {_healthGoals!.MaxUnenclosedRoomsCount!.Value:N0}" : "")
                .Replace("##INP_BAD_CLS##", inpGoal > 0 && inplace > inpGoal ? "ct-bad" : "")
                .Replace("##UNP_BAD_CLS##", (_healthGoals?.MaxUnplacedRoomsCount   ?? 0) > 0 && unplaced  >= _healthGoals!.MaxUnplacedRoomsCount!.Value   ? "ct-bad" : "")
                .Replace("##UNE_BAD_CLS##", (_healthGoals?.MaxUnenclosedRoomsCount ?? 0) > 0 && unenclosed >= _healthGoals!.MaxUnenclosedRoomsCount!.Value ? "ct-bad" : "")
                // Optimization Indicators tiles (parameterCode → field):
                //   ModelGroupsCount      -> (none)
                //   DetailGroupsCount     -> (none)
                //   LinkedDwgCount        -> MaxLinkedDwgCount
                //   RasterImagesCount     -> MaxRasterImagesCount
                //   ViewTemplatesCount    -> (none)
                //   DesignOptionsCount    -> (none)
                .Replace("##MGRP_PILL##", (_healthGoals?.MaxModelGroupsCount    ?? 0) > 0 ? $"Threshold: {_healthGoals!.MaxModelGroupsCount!.Value:N0}"    : "")
                .Replace("##DGRP_PILL##", (_healthGoals?.MaxDetailGroupsCount   ?? 0) > 0 ? $"Threshold: {_healthGoals!.MaxDetailGroupsCount!.Value:N0}"   : "")
                .Replace("##LDWG_PILL##", (_healthGoals?.MaxLinkedDwgCount      ?? 0) > 0 ? $"Threshold: {_healthGoals!.MaxLinkedDwgCount!.Value:N0}"      : "")
                .Replace("##RAST_PILL##", (_healthGoals?.MaxRasterImagesCount   ?? 0) > 0 ? $"Threshold: {_healthGoals!.MaxRasterImagesCount!.Value:N0}"   : "")
                .Replace("##VT_PILL##",   (_healthGoals?.MaxViewTemplatesCount  ?? 0) > 0 ? $"Threshold: {_healthGoals!.MaxViewTemplatesCount!.Value:N0}"  : "")
                .Replace("##DES_PILL##",  (_healthGoals?.MaxDesignOptionsCount  ?? 0) > 0 ? $"Threshold: {_healthGoals!.MaxDesignOptionsCount!.Value:N0}"  : "")
                .Replace("##OPT_MGRP_CLS##", (_healthGoals?.MaxModelGroupsCount   ?? 0) > 0 && modelGroups   >= _healthGoals!.MaxModelGroupsCount!.Value   ? "ct-bad" : "")
                .Replace("##OPT_DGRP_CLS##", (_healthGoals?.MaxDetailGroupsCount  ?? 0) > 0 && detailGroups  >= _healthGoals!.MaxDetailGroupsCount!.Value  ? "ct-bad" : "")
                .Replace("##OPT_LDWG_CLS##", (_healthGoals?.MaxLinkedDwgCount    ?? 0) > 0 && linkedDwgs    >= _healthGoals!.MaxLinkedDwgCount!.Value    ? "ct-bad" : "")
                .Replace("##OPT_RAST_CLS##", (_healthGoals?.MaxRasterImagesCount ?? 0) > 0 && rasterImgs    >= _healthGoals!.MaxRasterImagesCount!.Value ? "ct-bad" : "")
                .Replace("##OPT_VT_CLS##",   (_healthGoals?.MaxViewTemplatesCount ?? 0) > 0 && viewTemplates >= _healthGoals!.MaxViewTemplatesCount!.Value ? "ct-bad" : "")
                .Replace("##OPT_DES_CLS##",  (_healthGoals?.MaxDesignOptionsCount ?? 0) > 0 && designs       >= _healthGoals!.MaxDesignOptionsCount!.Value ? "ct-bad" : "")
                // File & Model Summary mini-tiles (Levels / Grids / Linked Revit).
                // These three goals come from the per-model goals endpoint via
                // MergeGoals (StaticModelInfo doesn't carry them); when the admin
                // hasn't set a threshold the pill stays empty and CSS hides it.
                // ct-bad turns the tile red when the count reaches the threshold.
                // Per-user request 2026-06-05: Levels / Grids / Linked Revit
                // tiles must NEVER show a threshold pill on the dashboard, even
                // when the admin has configured a threshold on the web. We
                // force the placeholders empty so .thresh-pill:empty hides the
                // span entirely. The underlying MaxLevelsCount /
                // MaxGridsCount / MaxLinkedRevitCount fields stay populated by
                // MapFromParameters so the detailed report and any other
                // consumer can still read them — only the dashboard tile is
                // intentionally silent.
                .Replace("##LVL_PILL##",  "")
                .Replace("##GRD_PILL##",  "")
                .Replace("##LREV_PILL##", "")
                // Per-user request 2026-06-09: Levels / Grids / Linked Revit
                // tiles must NEVER change colour even when the admin's
                // threshold is exceeded — same intent as the empty pill
                // replacements above. Forcing ct-bad empty keeps the tiles
                // on their original purple / yellow / pink gradients at all
                // times. The underlying MaxLevelsCount / MaxGridsCount /
                // MaxLinkedRevitCount fields stay populated by
                // MapFromParameters so the detailed report and any other
                // consumer can still read them — only the dashboard tile is
                // intentionally silent.
                .Replace("##LVL_BAD_CLS##",  "")
                .Replace("##GRD_BAD_CLS##",  "")
                .Replace("##LREV_BAD_CLS##", "")
                // File Size value: turn the "268.1 MB" text red when the file
                // exceeds the admin-configured size limit. Mirrors the same
                // colour-red semantics other tiles use; the existing ring colour
                // gradient stays untouched.
                .Replace("##FS_VAL_CLS##", fsPct >= 1.0 ? "color-red" : "")
                // Sheets / Worksets / Families: per-row "/N" goal suffix and red
                // value colour when the count reaches or exceeds the configured
                // threshold. Goals come from MaxSheetsCount /
                // MaxTotalWorksetsCount / MaxTotalFamiliesCount on the per-model
                // goals endpoint (merged into _healthGoals via MergeGoals).
                .Replace("##SHEETS_SUF##", (_healthGoals?.MaxSheetsCount          ?? 0) > 0 ? $"<span style='font-size:10px;color:#94a3b8;font-weight:600;'>/{_healthGoals!.MaxSheetsCount!.Value:N0}</span>"          : "")
                .Replace("##SH_VAL_CLS##", (_healthGoals?.MaxSheetsCount          ?? 0) > 0 && sheets   >= _healthGoals!.MaxSheetsCount!.Value          ? "color-red" : "")
                .Replace("##WK_SUF##",     (_healthGoals?.MaxTotalWorksetsCount   ?? 0) > 0 ? $"<span style='font-size:10px;color:#94a3b8;font-weight:600;'>/{_healthGoals!.MaxTotalWorksetsCount!.Value:N0}</span>"   : "")
                .Replace("##WK_VAL_CLS##", (_healthGoals?.MaxTotalWorksetsCount   ?? 0) > 0 && worksets >= _healthGoals!.MaxTotalWorksetsCount!.Value   ? "color-red" : "")
                .Replace("##FM_SUF##",     (_healthGoals?.MaxTotalFamiliesCount   ?? 0) > 0 ? $"<span style='font-size:10px;color:#94a3b8;font-weight:600;'>/{_healthGoals!.MaxTotalFamiliesCount!.Value:N0}</span>"   : "")
                .Replace("##FM_VAL_CLS##", (_healthGoals?.MaxTotalFamiliesCount   ?? 0) > 0 && totalFam >= _healthGoals!.MaxTotalFamiliesCount!.Value   ? "color-red" : "")
                // Bar colour class — flips the gradient bar (not just the value
                // number) to red when the row's threshold is configured AND
                // reached. Default class preserves each row's original colour
                // (Views/Worksets: navy purple; Sheets/Families: amber yellow).
                .Replace("##VW_BAR_CLS##", (_healthGoals?.MaxTotalViewsCount     ?? 0) > 0 && totalViews >= _healthGoals!.MaxTotalViewsCount!.Value     ? "vs-bar-red" : "vs-bar-navy")
                .Replace("##SH_BAR_CLS##", (_healthGoals?.MaxSheetsCount          ?? 0) > 0 && sheets     >= _healthGoals!.MaxSheetsCount!.Value          ? "vs-bar-red" : "vs-bar-amber")
                .Replace("##WK_BAR_CLS##", (_healthGoals?.MaxTotalWorksetsCount   ?? 0) > 0 && worksets   >= _healthGoals!.MaxTotalWorksetsCount!.Value   ? "vs-bar-red" : "vs-bar-navy")
                .Replace("##FM_BAR_CLS##", (_healthGoals?.MaxTotalFamiliesCount   ?? 0) > 0 && totalFam   >= _healthGoals!.MaxTotalFamiliesCount!.Value   ? "vs-bar-red" : "vs-bar-amber")
                // Views value text: turn red alongside the bar when over goal.
                .Replace("##VW_VAL_CLS##", (_healthGoals?.MaxTotalViewsCount     ?? 0) > 0 && totalViews >= _healthGoals!.MaxTotalViewsCount!.Value     ? "color-red" : "")
                // Views / Sheets / Not on Sheet "/N" suffixes — only render when
                // the server actually returned a threshold for that parameterCode.
                .Replace("##VIEWS_SUF##",  (_healthGoals?.MaxTotalViewsCount ?? 0) > 0 ? $"<span style='font-size:10px;color:#94a3b8;font-weight:600;'>/{_healthGoals!.MaxTotalViewsCount!.Value:N0}</span>" : "")
                .Replace("##SHEETS_SUF##", "")
                .Replace("##VNS_SUF##",    vnsGoal > 0 ? $"<span style='font-size:10px;color:#94a3b8;font-weight:600;'>/{vnsGoal:N0}</span>" : "")
                .Replace("##UNPLACED_SPARK##", unplacedSpark)
                .Replace("##UNENCLOSED_SPARK##", unenclosedSpark)
                .Replace("##VNS_GOAL_BAR_H##", totalViews > 0 ? Math.Max(8, (int)(100.0 * vnsGoal / totalViews)).ToString() : "20");

            return html;

        }
        #endregion

        #region Helpers
        private static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        private static long EstBytes(string fmt)
        {
            if (string.IsNullOrEmpty(fmt)) return 0;
            fmt = fmt.Trim().ToUpperInvariant(); int i = 0;
            while (i < fmt.Length && (char.IsDigit(fmt[i]) || fmt[i] == '.' || fmt[i] == ',')) i++;
            if (i == 0) return 0;
            if (!double.TryParse(fmt.Substring(0, i).Replace(",", ""), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double n)) return 0;
            string s = fmt.Substring(i).Trim();
            if (s.StartsWith("GB")) return (long)(n * 1024 * 1024 * 1024);
            if (s.StartsWith("MB")) return (long)(n * 1024 * 1024);
            if (s.StartsWith("KB")) return (long)(n * 1024);
            return (long)n;
        }
        #endregion
    }
}
