using System;
using Autodesk.Revit.UI;

namespace BIManage.Revit.Events
{
    /// <summary>
    ///     Context information for a command execution
    /// </summary>
    public class CommandExecutionContext
    {
        public RevitCommandId CommandId { get; }
        public string CommandName { get; }
        public DateTime Timestamp { get; }
        public bool IsBeforeExecution { get; }

        public CommandExecutionContext(RevitCommandId commandId, string commandName, bool isBeforeExecution)
        {
            CommandId = commandId ?? throw new ArgumentNullException(nameof(commandId));
            CommandName = commandName ?? commandId.Name ?? "Unknown";
            IsBeforeExecution = isBeforeExecution;
            Timestamp = DateTime.UtcNow;
        }

        public override string ToString() =>
            $"{(IsBeforeExecution ? "Before" : "After")} {CommandName} at {Timestamp:HH:mm:ss.fff}";
    }
}
