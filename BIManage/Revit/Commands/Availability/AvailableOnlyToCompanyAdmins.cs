using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Identity;

namespace BIManage.Revit.Commands.Availability
{
    /// <summary>
    /// Command availability class that restricts commands to Company Administrators only.
    ///
    /// Reads role flags from RoleFlagStore (AppDomain data) so the result is consistent across
    /// AssemblyLoadContexts — see AvailableOnlyToAdmins for the dual-ALC rationale.
    /// </summary>
    public class AvailableOnlyToCompanyAdmins : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication uiApp, CategorySet selectedCategories)
        {
            try
            {
                return RoleFlagStore.IsCompanyAdmin;
            }
            catch
            {
                return false;
            }
        }
    }
}
