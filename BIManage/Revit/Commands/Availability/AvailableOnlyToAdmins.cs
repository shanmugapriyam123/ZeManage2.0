using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Identity;

namespace BIManage.Revit.Commands.Availability
{
    /// <summary>
    /// Command availability class that restricts commands to any admin (Company or Project)
    /// Alias for AvailableOnlyToProjectAdmins with clearer naming
    ///
    /// Reads role flags from RoleFlagStore (AppDomain data) so the result is consistent across
    /// AssemblyLoadContexts. Revit 2025 loads BIManageRevit into two ALCs and may instantiate
    /// this class from the ALC where Application.Instance is null — going through the singleton
    /// would intermittently grey out admin buttons. RoleFlagStore is ALC-agnostic.
    /// </summary>
    public class AvailableOnlyToAdmins : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication uiApp, CategorySet selectedCategories)
        {
            try
            {
                return RoleFlagStore.HasAdminPrivileges;
            }
            catch
            {
                return false;
            }
        }
    }
}
