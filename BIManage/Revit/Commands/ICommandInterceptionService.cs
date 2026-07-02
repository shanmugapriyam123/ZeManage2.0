using System.Collections.Generic;
using Autodesk.Revit.UI;

namespace BIManage.Revit.Commands
{
    /// <summary>
    ///     Service for intercepting and monitoring Revit commands
    ///     Supports both hardcoded core commands and configurable command lists
    /// </summary>
    public interface ICommandInterceptionService
    {
        /// <summary>
        ///     Register command bindings for interception
        /// </summary>
        void RegisterCommandBindings(UIApplication uiApplication);

        /// <summary>
        ///     Unregister all command bindings
        /// </summary>
        void UnregisterCommandBindings();

        /// <summary>
        ///     Check if a command is being monitored
        /// </summary>
        bool IsCommandMonitored(RevitCommandId commandId);

        /// <summary>
        ///     Sets the rule command interceptor to be called before legacy protection logic
        /// </summary>
        void SetRuleInterceptor(IRuleCommandInterceptor ruleInterceptor);

        /// <summary>
        ///     Register additional command bindings for commands referenced in rules
        ///     but not already bound by GetCoreCommands() or individual bindings.
        /// </summary>
        void RegisterRuleCommandBindings(UIApplication uiApplication, IEnumerable<(int CommandId, string CommandName)> ruleCommands);
    }
}
