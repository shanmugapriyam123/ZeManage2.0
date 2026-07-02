using Autodesk.Revit.DB;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.Guards
{
    /// <summary>
    ///     Basic runtime safety checks for Revit API usage.
    /// </summary>
    public class RevitSafetyGuards : IRevitSafetyGuards
    {
        private readonly ILogger? _logger;

        public RevitSafetyGuards(ILogger? logger)
        {
            _logger = logger;
        }

        public bool EnsureDocumentIsModifiable(Document? document, string actionDescription)
        {
            if (document == null)
            {
                _logger?.LogWarning($"Cannot {actionDescription}: document is null.");
                return false;
            }

            if (document.IsModifiable) return true;

            _logger?.LogWarning($"Cannot {actionDescription}: document is not modifiable.");
            return false;
        }
    }
}
