using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BIManage.Revit.Commands.Availability
{
    /// <summary>
    /// Command availability class that only requires an active document.
    /// Available to ALL users (no role restriction) when a document is open.
    /// </summary>
    public class AvailableWithActiveDocument : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication uiApp, CategorySet selectedCategories)
        {
            try
            {
                // Simply check if active document exists - no role restriction
                return uiApp?.ActiveUIDocument?.Document != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
