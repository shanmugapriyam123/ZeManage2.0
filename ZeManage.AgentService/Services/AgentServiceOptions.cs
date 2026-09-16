namespace ZeManage.AgentService.Services;

public sealed class AgentServiceOptions
{
    public string DatabasePath { get; set; } = "";
    public string BackendBaseUrl { get; set; } = "http://10.10.40.75:5000";
    public bool AllowInsecureSsl { get; set; } = true;

    // License key entered during installation — required to register/validate this device
    public string? LicenseKey { get; set; }

    public int ProcessScanIntervalSeconds { get; set; } = 10;
    public int ActivityTickSeconds        { get; set; } = 5;
    public int ActiveThresholdSeconds     { get; set; } = 30;
    public int IdleThresholdSeconds       { get; set; } = 300;
    public int NetworkIntervalSeconds     { get; set; } = 300;
    public int SyncIntervalSeconds        { get; set; } = 60;
    public int HeartbeatIntervalSeconds   { get; set; } = 60;

    public bool EnableScreenshots         { get; set; } = true;
    public int ScreenshotIntervalMinutes  { get; set; } = 10;
    public int ScreenshotJpegQuality      { get; set; } = 50;

    public int MaxSyncRetryAttempts       { get; set; } = 3;
}
