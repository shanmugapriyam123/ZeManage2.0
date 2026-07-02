namespace ZeManage.Agent.Core.Monitors;

public static class TrackedApplications
{
    public sealed record AppDef(string ProcessName, string DisplayName, string Category, bool IsBim);

    public static readonly IReadOnlyDictionary<string, AppDef> Map =
        new Dictionary<string, AppDef>(StringComparer.OrdinalIgnoreCase)
        {
            // BIM / CAD
            ["Revit"]              = new("Revit",              "Autodesk Revit",          "BIM",     true),
            ["acad"]               = new("acad",               "Autodesk AutoCAD",         "CAD",     true),
            ["accoreconsole"]      = new("accoreconsole",      "AutoCAD Core Console",     "CAD",     true),
            ["civil3d"]            = new("civil3d",            "Autodesk Civil 3D",        "CAD",     true),
            ["Roamer"]             = new("Roamer",             "Autodesk Navisworks",      "BIM",     true),
            ["DynamoSandbox"]      = new("DynamoSandbox",      "Autodesk Dynamo",          "BIM",     true),
            ["ustation"]           = new("ustation",           "Bentley MicroStation",     "CAD",     true),
            ["udesign"]            = new("udesign",            "Bentley OpenBuildings",    "BIM",     true),
            ["uloadsim"]           = new("uloadsim",           "Bentley SACS",             "CAD",     true),
            ["TeklaStructures"]    = new("TeklaStructures",    "Tekla Structures",         "BIM",     true),
            ["ArchiCAD"]           = new("ArchiCAD",           "Graphisoft ArchiCAD",      "BIM",     true),
            ["archicad"]           = new("archicad",           "Graphisoft ArchiCAD",      "BIM",     true),
            ["Rhino"]              = new("Rhino",              "McNeel Rhino",             "CAD",     true),
            ["rhino"]              = new("rhino",              "McNeel Rhino",             "CAD",     true),
            ["grasshopper"]        = new("grasshopper",        "McNeel Grasshopper",       "BIM",     true),
            ["SketchUp"]           = new("SketchUp",           "Trimble SketchUp",         "CAD",     true),
            ["sketchup"]           = new("sketchup",           "Trimble SketchUp",         "CAD",     true),
            ["Enscape"]            = new("Enscape",            "Enscape Renderer",         "BIM",     true),
            ["enscape"]            = new("enscape",            "Enscape Renderer",         "BIM",     true),
            ["Lumion"]             = new("Lumion",             "Lumion",                   "BIM",     true),
            ["D5Render"]           = new("D5Render",           "D5 Render",                "BIM",     true),
            ["TrimbleConnect"]     = new("TrimbleConnect",     "Trimble Connect",          "BIM",     true),
            ["RecorpCCB"]          = new("RecorpCCB",          "Trimble Connect",          "BIM",     true),

            // Review / Markup
            ["Revit.Bluebeam"]     = new("Revit.Bluebeam",    "Bluebeam Revu",            "Review",  true),
            ["bluebeam"]           = new("bluebeam",           "Bluebeam Revu",            "Review",  true),
            ["revu"]               = new("revu",               "Bluebeam Revu",            "Review",  true),
            ["AcroRd32"]           = new("AcroRd32",           "Adobe Acrobat Reader",     "Review",  false),
            ["Acrobat"]            = new("Acrobat",            "Adobe Acrobat",            "Review",  false),

            // Office / Productivity
            ["EXCEL"]              = new("EXCEL",              "Microsoft Excel",          "Office",  false),
            ["WINWORD"]            = new("WINWORD",            "Microsoft Word",           "Office",  false),
            ["POWERPNT"]           = new("POWERPNT",           "Microsoft PowerPoint",     "Office",  false),
            ["OUTLOOK"]            = new("OUTLOOK",            "Microsoft Outlook",        "Office",  false),
            ["ONENOTE"]            = new("ONENOTE",            "Microsoft OneNote",        "Office",  false),
            ["MSACCESS"]           = new("MSACCESS",           "Microsoft Access",         "Office",  false),
            ["notepad"]            = new("notepad",            "Notepad",                  "Office",  false),
            ["notepad++"]          = new("notepad++",          "Notepad++",                "Office",  false),

            // Collaboration / Comms
            ["Teams"]              = new("Teams",              "Microsoft Teams",          "Collab",  false),
            ["ms-teams"]           = new("ms-teams",           "Microsoft Teams",          "Collab",  false),
            ["Zoom"]               = new("Zoom",               "Zoom",                     "Collab",  false),
            ["slack"]              = new("slack",              "Slack",                    "Collab",  false),
            ["Webex"]              = new("Webex",              "Cisco Webex",              "Collab",  false),

            // Browsers
            ["chrome"]             = new("chrome",             "Google Chrome",            "Browser", false),
            ["msedge"]             = new("msedge",             "Microsoft Edge",           "Browser", false),
            ["firefox"]            = new("firefox",            "Mozilla Firefox",          "Browser", false),
            ["brave"]              = new("brave",              "Brave Browser",            "Browser", false),

            // Dev / Script
            ["devenv"]             = new("devenv",             "Visual Studio",            "Dev",     false),
            ["Code"]               = new("Code",               "Visual Studio Code",       "Dev",     false),
            ["WindowsTerminal"]    = new("WindowsTerminal",    "Windows Terminal",         "Dev",     false),
            ["powershell"]         = new("powershell",         "PowerShell",               "Dev",     false),
        };

    // System processes with no user activity — never tracked
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

    // Returns a tracked def for ANY user-facing process (has visible window).
    // Known apps get their proper DisplayName/Category; unknown apps are logged as "Other".
    public static AppDef ResolveOrGeneric(string processName, bool hasWindow)
    {
        if (Map.TryGetValue(processName, out var def)) return def;
        if (SystemExclusions.Contains(processName)) return null!;
        if (!hasWindow) return null!;
        return new AppDef(processName, processName, "Other", false);
    }
}
