// Force nullable annotations on for this file so the `?` markers compile
// cleanly under the project's net48 setting (which has <Nullable>disable</Nullable>).
// The ALC duplicate-load problem this dispatcher fixes only manifests on net8.0+,
// but the file is compiled into every TFM and must build cleanly on net48 too.
#nullable enable

using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace BIManage.Common.Helpers
{
    /// <summary>
    /// Cross-AssemblyLoadContext IExternalCommand dispatcher.
    ///
    /// Why this exists
    /// ---------------
    /// On Revit 2025+, Revit's add-in loader produces TWO copies of BIManageRevit.dll —
    /// one in the Default ALC and one in a per-add-in isolation ALC named "BIManageRevit".
    /// Both copies live in the same process; both have identical bits but distinct CLR
    /// type identity, so each ALC's static fields (e.g. Application._instance) are
    /// completely independent.
    ///
    /// Revit invokes IExternalApplication.OnStartup in only ONE of those ALCs, so only
    /// that ALC ends up with Application._instance and _services populated. The other
    /// ALC's Application class stays uninitialized — Application.Instance is null there,
    /// services are null, all reflection-based service lookups return null.
    ///
    /// Ribbon commands are clicked via PushButtonData, which serializes the assembly
    /// path + FullClassName. At click time Revit resolves "BIManageRevit" through the
    /// Default ALC, instantiates the Default-ALC copy of the IExternalCommand class,
    /// and calls Execute on it. If the Default ALC isn't the live ALC, the command's
    /// "Application.Instance" is null and we toast "Unable to access application
    /// services". This affects every command that depends on services — i.e. nearly
    /// all of them.
    ///
    /// We cannot prevent the dual-load: the Revit 2025 manifest schema doesn't
    /// recognize &lt;UseRevitContext&gt; (that tag is R26+ only), so there is no way
    /// to instruct R25's add-in loader to use the Default ALC exclusively.
    ///
    /// What this class does
    /// --------------------
    /// On entry to a command's Execute, the command calls TryForward. TryForward:
    ///   1. Reads Application.Instance in the LOCAL ALC. If non-null, returns null —
    ///      caller proceeds normally.
    ///   2. If null, walks AppDomain.CurrentDomain.GetAssemblies() looking for
    ///      another assembly with the same simple name ("BIManageRevit") — that's
    ///      the OTHER ALC's copy. Reads its Application._instance via reflection.
    ///   3. If a live instance is found in another ALC, invokes that ALC's
    ///      Application.RunCommandByTypeName(...) via reflection. RunCommandByTypeName
    ///      lives in Application.cs; it instantiates the command class from the
    ///      live ALC's assembly and calls Execute on it locally — same code, just
    ///      running with the right static state.
    ///   4. The cross-ALC reflection only passes types that are visible in BOTH
    ///      ALCs: ExternalCommandData, ElementSet, Result, string. All come from
    ///      Default-ALC-shared assemblies (RevitAPIUI.dll, mscorlib), so
    ///      reflection.Invoke on them succeeds despite our two-ALC topology.
    ///
    /// If no live instance is found anywhere (genuine startup failure) TryForward
    /// returns null and the caller proceeds with the existing null-services error
    /// path — same UX as today, no regression.
    /// </summary>
    public static class CrossAlcDispatch
    {
        /// <summary>
        /// Returns the delegated <see cref="Result"/> if we are in a non-live ALC
        /// and successfully forwarded execution to the live ALC. Returns null
        /// when we are already in the live ALC — caller should run normally.
        /// </summary>
        public static Result? TryForward(
            object thisCommand,
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            if (thisCommand == null) return null;

            // Local-ALC fast path: if Application.Instance is non-null, we ARE in
            // the live ALC. Caller proceeds normally.
            var localApp = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
            if (localApp != null) return null;

            // Find the live Application instance from any other ALC's copy.
            var liveApp = FindLiveApplicationInstance();
            if (liveApp == null) return null; // no live instance anywhere — fall through to original error path

            // Invoke RunCommandByTypeName(string, ExternalCommandData, ElementSet, ref string) on the live ALC's Application.
            var runMethod = liveApp.GetType().GetMethod(
                "RunCommandByTypeName",
                BindingFlags.Public | BindingFlags.Instance);
            if (runMethod == null) return null;

            // Reflection.Invoke handles `ref string` by mutating args[index] in place.
            var args = new object?[] { thisCommand.GetType().FullName, commandData, elements, message };
            try
            {
                var resultObj = runMethod.Invoke(liveApp, args);
                message = args[3] as string ?? string.Empty;
                return (Result)resultObj!;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                message = tie.InnerException.Message;
                return Result.Failed;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// Walks all loaded assemblies looking for a copy of BIManageRevit whose
        /// Application._instance static field is non-null. Returns the live
        /// instance as object (cross-ALC reflection target) or null if none found.
        /// </summary>
        private static object? FindLiveApplicationInstance()
        {
            const string appTypeFullName = "BIManageRevit.BIManage.Revit.Applications.Application";
            var thisAsm = typeof(CrossAlcDispatch).Assembly;
            var asmName = thisAsm.GetName().Name;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == thisAsm) continue;
                if (!string.Equals(asm.GetName().Name, asmName, StringComparison.OrdinalIgnoreCase)) continue;

                Type? appType = null;
                try { appType = asm.GetType(appTypeFullName, throwOnError: false); }
                catch { continue; }
                if (appType == null) continue;

                FieldInfo? instanceField = null;
                try { instanceField = appType.GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static); }
                catch { continue; }
                if (instanceField == null) continue;

                object? candidate = null;
                try { candidate = instanceField.GetValue(null); }
                catch { continue; }
                if (candidate != null) return candidate;
            }
            return null;
        }
    }
}
