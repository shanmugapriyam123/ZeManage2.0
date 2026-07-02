namespace ZeManage.Agent.Core.Services;

public sealed class AgentOptions
{
    public string DatabasePath { get; set; } = "";
    public string BackendBaseUrl { get; set; } = "https://localhost:7116";
    public string BackendEmail { get; set; } = "agent@conservesolution.com";
    public string BackendPassword { get; set; } = "AgentSvc@12345";
    public bool AllowInsecureSsl { get; set; } = false;
    public int IdleThresholdSeconds { get; set; } = 120;
    public int ActivityTickSeconds { get; set; } = 5;
    public int HardwareIntervalSeconds { get; set; } = 60;
    public int NetworkIntervalSeconds { get; set; } = 300;
    public int ProcessScanIntervalSeconds { get; set; } = 10;
    public int SyncIntervalSeconds { get; set; } = 60;
    public bool EnableSpeedTest { get; set; } = false;

    // V2: SignalR realtime
    public bool EnableSignalR { get; set; } = true;
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    // V2: Screenshot capture
    public bool EnableScreenshots { get; set; } = true;
    public int ScreenshotIntervalMinutes { get; set; } = 5;
    public int ScreenshotJpegQuality { get; set; } = 50;
    public bool ScreenshotOnAttendanceStart { get; set; } = true;
    public bool ScreenshotOnIdleReturn { get; set; } = false;
}
