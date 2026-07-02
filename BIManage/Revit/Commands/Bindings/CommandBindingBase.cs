using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.Commands.Bindings
{
    /// <summary>
    /// Abstract base class for command bindings using the individual binding pattern
    /// Each command type gets its own dedicated binding based on its nature
    /// </summary>
    public abstract class CommandBindingBase
    {
        /// <summary>
        /// Static registry of all command IDs that have individual bindings registered.
        /// Used to prevent CommandProtectionBinding from registering duplicate handlers.
        /// </summary>
        private static readonly HashSet<string> _registeredCommandIds = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _registryLock = new();

        /// <summary>
        /// Check if a command ID already has an individual binding registered.
        /// </summary>
        public static bool HasIndividualBinding(string commandIdName)
        {
            lock (_registryLock)
            {
                return _registeredCommandIds.Contains(commandIdName);
            }
        }

        /// <summary>
        /// Register a command ID as having an individual binding.
        /// </summary>
        protected static void MarkAsRegistered(string commandIdName)
        {
            lock (_registryLock)
            {
                _registeredCommandIds.Add(commandIdName);
            }
        }

        /// <summary>
        /// Unregister a command ID.
        /// </summary>
        protected static void MarkAsUnregistered(string commandIdName)
        {
            lock (_registryLock)
            {
                _registeredCommandIds.Remove(commandIdName);
            }
        }

        protected UIApplication UIApp { get; }
        protected RevitCommandId CommandId { get; set; }
        protected ILogger Logger { get; }

        protected CommandBindingBase(UIApplication uiApp, ILogger logger)
        {
            UIApp = uiApp ?? throw new ArgumentNullException(nameof(uiApp));
            Logger = logger;
        }

        /// <summary>
        /// Register using BeforeExecuted event (for commands that need pre-execution interception)
        /// </summary>
        public abstract void RegisterWithBeforeExecute();

        /// <summary>
        /// Register using Executed event (for commands that need post-execution handling)
        /// </summary>
        public abstract void Register();

        /// <summary>
        /// Unregister all event handlers
        /// </summary>
        public abstract void Unregister();

        /// <summary>
        /// Check if command can have a binding
        /// </summary>
        protected bool CanRegister()
        {
            if (CommandId == null)
            {
                Logger?.LogWarning("CommandId is null, cannot register binding");
                return false;
            }

            if (!CommandId.CanHaveBinding)
            {
                Logger?.LogWarning($"Command {CommandId.Name} cannot have binding");
                return false;
            }

            return true;
        }
    }
}
