using System.Reflection;
using System.Resources;

[assembly: AssemblyTitle("ZeManage")]
[assembly: AssemblyDescription("ZeManage - BIM Management Plugin for Revit")]
[assembly: AssemblyCompany("ZestineTech")]
[assembly: AssemblyProduct("ZeManage")]
[assembly: AssemblyCopyright("Copyright © ZestineTech 2026")]

// Pin the satellite-resource culture so WPF's MarkupCompilePass1 doesn't auto-emit a
// [NeutralResourcesLanguageAttribute] reference and fail to resolve the type out of the
// facade System.Runtime.dll (MC1000 "Could not find type
// 'System.Resources.NeutralResourcesLanguageAttribute'"). Root cause: MarkupCompilePass1
// looks up the attribute in the netstandard facade System.Runtime.dll (empty on net48)
// instead of mscorlib where it actually lives. Declaring the attribute explicitly here
// prevents the implicit lookup and bypasses the bug. See
// https://github.com/dotnet/windowsdesktop/issues/3878. Safe to remove if/when the SDK
// fixes the underlying issue.
[assembly: NeutralResourcesLanguage("en-US")]

// Version is managed in BIManageRevit.csproj (Version, AssemblyVersion, FileVersion)
