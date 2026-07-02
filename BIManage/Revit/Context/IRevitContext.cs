using System;
using Autodesk.Revit.ApplicationServices;

namespace BIManage.Revit.Context
{
    /// <summary>
    ///     Interface for lightweight container of Revit-specific state exposed to services.
    /// </summary>
    public interface IRevitContext
    {
        /// <summary>
        ///     The Revit ControlledApplication instance.
        /// </summary>
        ControlledApplication ControlledApplication { get; }

        /// <summary>
        ///     Unique identifier for this Revit session.
        /// </summary>
        Guid SessionId { get; }
    }
}
