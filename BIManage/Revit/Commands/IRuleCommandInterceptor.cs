using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Events;

namespace BIManage.Revit.Commands
{
    /// <summary>
    /// Interface for rule-based command interception.
    /// Enables database-driven rule evaluation before and after command execution.
    /// </summary>
    public interface IRuleCommandInterceptor
    {
        /// <summary>
        /// Called before a command is executed. Allows cancellation based on rules.
        /// </summary>
        void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e);

        /// <summary>
        /// Called after a command has been executed. Used for post-execution tracking.
        /// </summary>
        void OnExecuted(object sender, ExecutedEventArgs e);

        /// <summary>
        /// Pure-logic rule evaluation for the bypass detector path (drag/nudge/Ctrl+drag,
        /// property palette). Called from DocumentChanged where BeforeExecutedEventArgs
        /// cannot be fabricated. Returns Allowed = false when the user blocks the action;
        /// caller is responsible for the revert (restore position or delete added elements).
        /// </summary>
        RuleCommandInterceptor.RuleBypassResult EvaluateForBypass(
            int commandId,
            string commandName,
            Document document,
            IList<Element> elements,
            string bypassSource);
    }
}
