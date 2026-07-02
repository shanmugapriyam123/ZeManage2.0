using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Identity;

namespace BIManage.Revit.Commands.Availability
{
    /// <summary>
    /// Command availability class that restricts commands to Project Administrators (or Company Admins).
    /// Company Admins have implicit Project Admin rights for all projects.
    ///
    /// Reads role flags from RoleFlagStore (AppDomain data) so the result is consistent across
    /// AssemblyLoadContexts — see AvailableOnlyToAdmins for the dual-ALC rationale.
    /// </summary>
    public class AvailableOnlyToProjectAdmins : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication uiApp, CategorySet selectedCategories)
        {
            try
            {
                // Both Company Admins and Project Admins satisfy this gate.
                return RoleFlagStore.HasAdminPrivileges;
            }
            catch
            {
                return false;
            }
        }
    }
}
