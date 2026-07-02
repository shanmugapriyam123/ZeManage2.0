using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Identity;

namespace BIManage.Revit.Commands.Availability
{
    /// <summary>
    /// Command availability class that requires:
    /// 1. User is an admin (Company or Project)
    /// 2. Active document is open
    ///
    /// Reads role flags from RoleFlagStore (AppDomain data) so the result is consistent across
    /// AssemblyLoadContexts — see AvailableOnlyToAdmins for the dual-ALC rationale.
    /// </summary>
    public class AvailableToAdminsWithActiveDocument : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication uiApp, CategorySet selectedCategories)
        {
            try
            {
                if (uiApp?.ActiveUIDocument?.Document == null)
                    return false;

                return RoleFlagStore.HasAdminPrivileges;
            }
            catch
            {
                return false;
            }
        }
    }
}
