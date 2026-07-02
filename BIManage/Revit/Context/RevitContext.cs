using System;
using Autodesk.Revit.ApplicationServices;

namespace BIManage.Revit.Context
{
    /// <summary>
    ///     Lightweight container for Revit-specific state exposed to services.
    /// </summary>
    public class RevitContext : IRevitContext
    {
        public ControlledApplication ControlledApplication { get; }
        public Guid SessionId { get; }

        public RevitContext(ControlledApplication controlledApplication, Guid sessionId)
        {
            ControlledApplication = controlledApplication;
            SessionId = sessionId;
        }
    }
}
