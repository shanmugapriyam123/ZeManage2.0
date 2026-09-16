namespace ZeManage.AgentService.Services;

/// <summary>Known-app display-name map + a system-process exclusion list, trimmed from the
/// reference Agent's copy. Anything not in either list still gets tracked generically (using its
/// own process name as the display name) as long as it owns a visible window — this just improves
/// display names and keeps obvious OS noise (svchost, dwm, ...) out of the data.</summary>
public static class TrackedApplications
{
    public sealed record AppDef(string ProcessName, string DisplayName, string Category);

    public static readonly IReadOnlyDictionary<string, AppDef> Map =
        new Dictionary<string, AppDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["Revit"]           = new("Revit", "Autodesk Revit", "BIM"),
            ["acad"]            = new("acad", "Autodesk AutoCAD", "CAD"),
            ["EXCEL"]           = new("EXCEL", "Microsoft Excel", "Office"),
            ["WINWORD"]         = new("WINWORD", "Microsoft Word", "Office"),
            ["POWERPNT"]        = new("POWERPNT", "Microsoft PowerPoint", "Office"),
            ["OUTLOOK"]         = new("OUTLOOK", "Microsoft Outlook", "Office"),
            ["ONENOTE"]         = new("ONENOTE", "Microsoft OneNote", "Office"),
            ["notepad"]         = new("notepad", "Notepad", "Office"),
            ["notepad++"]       = new("notepad++", "Notepad++", "Office"),
            ["Teams"]           = new("Teams", "Microsoft Teams", "Collab"),
            ["ms-teams"]        = new("ms-teams", "Microsoft Teams", "Collab"),
            ["Zoom"]            = new("Zoom", "Zoom", "Collab"),
            ["slack"]           = new("slack", "Slack", "Collab"),
            ["chrome"]          = new("chrome", "Google Chrome", "Browser"),
            ["msedge"]          = new("msedge", "Microsoft Edge", "Browser"),
            ["firefox"]         = new("firefox", "Mozilla Firefox", "Browser"),
            ["devenv"]          = new("devenv", "Visual Studio", "Dev"),
            ["Code"]            = new("Code", "Visual Studio Code", "Dev"),
            ["WindowsTerminal"] = new("WindowsTerminal", "Windows Terminal", "Dev"),
            ["powershell"]      = new("powershell", "PowerShell", "Dev"),
        };

    private static readonly HashSet<string> SystemExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost","System","Idle","Registry","smss","csrss","wininit","winlogon","services",
        "lsass","lsm","dwm","fontdrvhost","sihost","taskhostw","ctfmon","SearchHost",
        "ShellExperienceHost","StartMenuExperienceHost","RuntimeBroker","ApplicationFrameHost",
        "TextInputHost","MicrosoftEdgeUpdate","WmiPrvSE","dllhost","conhost","consent",
        "spoolsv","SearchIndexer","SgrmBroker","SecurityHealthService","MsMpEng","NisSrv",
        "audiodg","WUDFHost","backgroundTaskHost","PerfWatson2","WerFault","crashpad_handler",
        "ZeManage.Agent","ZeManage.Agent.Watchdog","ZeManage.AgentService"
    };

    public static AppDef? ResolveOrGeneric(string processName, bool hasWindow)
    {
        if (Map.TryGetValue(processName, out var def)) return def;
        if (SystemExclusions.Contains(processName)) return null;
        if (!hasWindow) return null;
        return new AppDef(processName, processName, "Other");
    }
}
