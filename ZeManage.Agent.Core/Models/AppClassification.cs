namespace ZeManage.Agent.Core.Models;

public sealed class AppClassification
{
    public required string ProcessName { get; set; }
    public bool IsProductive { get; set; }
    public DateTime UpdatedAt { get; set; }
}
