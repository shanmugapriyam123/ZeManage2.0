namespace ZeManage.Agent.Core.Monitors;

public static class TrackedApplications
{
    public sealed record AppDef(string ProcessName, string DisplayName, string Category);

    public static readonly IReadOnlyDictionary<string, AppDef> Map =
        new Dictionary<string, AppDef>(StringComparer.OrdinalIgnoreCase)
        {
            // BIM / CAD
            ["Revit"]              = new("Revit",              "Autodesk Revit",          "BIM"),
            ["acad"]               = new("acad",               "Autodesk AutoCAD",         "CAD"),
            ["accoreconsole"]      = new("accoreconsole",      "AutoCAD Core Console",     "CAD"),
            ["civil3d"]            = new("civil3d",            "Autodesk Civil 3D",        "CAD"),
            ["Roamer"]             = new("Roamer",             "Autodesk Navisworks",      "BIM"),
            ["DynamoSandbox"]      = new("DynamoSandbox",      "Autodesk Dynamo",          "BIM"),
            ["ustation"]           = new("ustation",           "Bentley MicroStation",     "CAD"),
            ["udesign"]            = new("udesign",            "Bentley OpenBuildings",    "BIM"),
            ["uloadsim"]           = new("uloadsim",           "Bentley SACS",             "CAD"),
            ["TeklaStructures"]    = new("TeklaStructures",    "Tekla Structures",         "BIM"),
            ["ArchiCAD"]           = new("ArchiCAD",           "Graphisoft ArchiCAD",      "BIM"),
            ["archicad"]           = new("archicad",           "Graphisoft ArchiCAD",      "BIM"),
            ["Rhino"]              = new("Rhino",              "McNeel Rhino",             "CAD"),
            ["rhino"]              = new("rhino",              "McNeel Rhino",             "CAD"),
            ["grasshopper"]        = new("grasshopper",        "McNeel Grasshopper",       "BIM"),
            ["SketchUp"]           = new("SketchUp",           "Trimble SketchUp",         "CAD"),
            ["sketchup"]           = new("sketchup",           "Trimble SketchUp",         "CAD"),
            ["Enscape"]            = new("Enscape",            "Enscape Renderer",         "BIM"),
            ["enscape"]            = new("enscape",            "Enscape Renderer",         "BIM"),
            ["Lumion"]             = new("Lumion",             "Lumion",                   "BIM"),
            ["D5Render"]           = new("D5Render",           "D5 Render",                "BIM"),
            ["TrimbleConnect"]     = new("TrimbleConnect",     "Trimble Connect",          "BIM"),
            ["RecorpCCB"]          = new("RecorpCCB",          "Trimble Connect",          "BIM"),

            // Review / Markup
            ["Revit.Bluebeam"]     = new("Revit.Bluebeam",    "Bluebeam Revu",            "Review"),
            ["bluebeam"]           = new("bluebeam",           "Bluebeam Revu",            "Review"),
            ["revu"]               = new("revu",               "Bluebeam Revu",            "Review"),
            ["AcroRd32"]           = new("AcroRd32",           "Adobe Acrobat Reader",     "Review"),
            ["Acrobat"]            = new("Acrobat",            "Adobe Acrobat",            "Review"),

            // Office / Productivity
            ["EXCEL"]              = new("EXCEL",              "Microsoft Excel",          "Office"),
            ["WINWORD"]            = new("WINWORD",            "Microsoft Word",           "Office"),
            ["POWERPNT"]           = new("POWERPNT",           "Microsoft PowerPoint",     "Office"),
            ["OUTLOOK"]            = new("OUTLOOK",            "Microsoft Outlook",        "Office"),
            ["ONENOTE"]            = new("ONENOTE",            "Microsoft OneNote",        "Office"),
            ["MSACCESS"]           = new("MSACCESS",           "Microsoft Access",         "Office"),
            ["notepad"]            = new("notepad",            "Notepad",                  "Office"),
            ["notepad++"]          = new("notepad++",          "Notepad++",                "Office"),

            // Collaboration / Comms
            ["Teams"]              = new("Teams",              "Microsoft Teams",          "Collab"),
            ["ms-teams"]           = new("ms-teams",           "Microsoft Teams",          "Collab"),
            ["Zoom"]               = new("Zoom",               "Zoom",                     "Collab"),
            ["slack"]              = new("slack",              "Slack",                    "Collab"),
            ["Webex"]              = new("Webex",              "Cisco Webex",              "Collab"),

            // Browsers
            ["chrome"]             = new("chrome",             "Google Chrome",            "Browser"),
            ["msedge"]             = new("msedge",             "Microsoft Edge",           "Browser"),
            ["firefox"]            = new("firefox",            "Mozilla Firefox",          "Browser"),
            ["brave"]              = new("brave",              "Brave Browser",            "Browser"),

            // Dev / Script
            ["devenv"]             = new("devenv",             "Visual Studio",            "Dev"),
            ["Code"]               = new("Code",               "Visual Studio Code",       "Dev"),
            ["WindowsTerminal"]    = new("WindowsTerminal",    "Windows Terminal",         "Dev"),
            ["powershell"]         = new("powershell",         "PowerShell",               "Dev"),
        };

    private static readonly HashSet<string> SystemExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost","System","Idle","Registry","smss","csrss","wininit","winlogon","services",
        "lsass","lsm","dwm","fontdrvhost","sihost","taskhostw","ctfmon","SearchHost",
        "ShellExperienceHost","StartMenuExperienceHost","RuntimeBroker","ApplicationFrameHost",
        "TextInputHost","MicrosoftEdgeUpdate","WmiPrvSE","dllhost","conhost","consent",
        "spoolsv","SearchIndexer","SgrmBroker","SecurityHealthService","MsMpEng","NisSrv",
        "audiodg","WUDFHost","TabTip","TabTip32","InputPersonalization","UserOOBEBroker",
        "backgroundTaskHost","PerfWatson2","WerFault","crashpad_handler","ZeManage.Agent"
    };

    public static AppDef? Resolve(string processName)
    {
        if (Map.TryGetValue(processName, out var def)) return def;
        return null;
    }

    public static AppDef? ResolveOrGeneric(string processName, bool hasWindow)
    {
        if (Map.TryGetValue(processName, out var def)) return def;
        if (SystemExclusions.Contains(processName)) return null;
        if (!hasWindow) return null;
        return new AppDef(processName, processName, "Other");
    }
}
