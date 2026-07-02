using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace BIManageRevit.Tests.Helpers
{
    /// <summary>
    /// Resolves Revit API assemblies at runtime for testing outside of Revit.
    /// The NuGet package version (e.g. 25.4.20.0) differs from the installed
    /// Revit assembly version (25.0.0.0), so we redirect at load time.
    /// Also adds the Revit install directory to the DLL search path
    /// for native dependencies used by mixed-mode C++/CLI assemblies.
    /// </summary>
    internal static class RevitAssemblyResolver
    {
        private static readonly string RevitInstallDir =
            @"C:\Program Files\Autodesk\Revit 2025";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string lpPathName);

        [ModuleInitializer]
        internal static void Initialize()
        {
            if (!Directory.Exists(RevitInstallDir))
                return;

            // SetDllDirectory adds the Revit directory to the native DLL search path.
            // This is required for mixed-mode C++/CLI assemblies (RevitAPIFoundation,
            // GeomUtilAPI, etc.) that have unmanaged native dependencies.
            SetDllDirectory(RevitInstallDir);

            // Resolve any managed assembly that exists in the Revit install directory.
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                if (name.Name == null)
                    return null;

                var dllPath = Path.Combine(RevitInstallDir, $"{name.Name}.dll");
                if (File.Exists(dllPath))
                    return context.LoadFromAssemblyPath(dllPath);

                return null;
            };
        }
    }
}
