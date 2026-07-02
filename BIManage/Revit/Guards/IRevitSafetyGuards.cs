using Autodesk.Revit.DB;

namespace BIManage.Revit.Guards
{
    /// <summary>
    ///     Interface for basic runtime safety checks for Revit API usage.
    /// </summary>
    public interface IRevitSafetyGuards
    {
        /// <summary>
        ///     Ensures a document is modifiable before performing an action.
        /// </summary>
        /// <param name="document">The document to check</param>
        /// <param name="actionDescription">Description of the action being attempted</param>
        /// <returns>True if the document is modifiable, false otherwise</returns>
        bool EnsureDocumentIsModifiable(Document? document, string actionDescription);
    }
}
