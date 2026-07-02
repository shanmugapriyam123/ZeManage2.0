using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BIManage.Revit.Commands.Availability
{
    /// <summary>
    /// Command availability class that makes a button always enabled,
    /// even when no document is open.
    /// Duplicated in Addons assembly so Revit can resolve it for addon commands.
    /// </summary>
    public class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication uiApp, CategorySet selectedCategories)
        {
            return true;
        }
    }
}
