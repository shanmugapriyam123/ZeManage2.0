# Command Interception Refactoring - The legacy product Architecture

Complete implementation plan to refactor CBOX Manage's command interception to exactly match the legacy product's proven patterns.

---

## Overview

the legacy product uses a **decentralized, individual command binding** approach where each command has its own field, registration method, and event handlers. This plan migrates CBOX Manage from a centralized registry pattern to the legacy product's architecture.

---

## Proposed Changes

### Core Architecture Components

#### 1. CommandBindingIntervention Nested Class

Create a nested class inside your command manager (similar to the legacy product's `CommandIntervention.CommandBindingIntervention`).

**Location:** `CommandInterceptionService.cs` (or create new `CommandInterceptionManager.cs`)

```csharp
public class CommandInterceptionManager
{
    // Existing code...
    
    /// <summary>
    /// Nested class that wraps a single command binding
    /// </summary>
    public class CommandBindingIntervention
    {
        private string RevitCommandName;
        private RevitCommandId commandId;
        private PostableCommand PostableCommand;
        private UIControlledApplication App;
        private AddInCommandBinding binding;
        private InterventionSettings InterventionSettings;
        private PostableCommandSettings MasterPostableCommandSetting;
        private bool isForCompanyLevelCommand;

        // Constructor 1: PostableCommand with settings
        public CommandBindingIntervention(
            UIControlledApplication app,
            PostableCommand postableCommand,
            string revitCommandName,
            PostableCommandSettings masterPostableCommandSettings,
            InterventionSettings interventionSettings = null)
        {
            this.App = app;
            this.RevitCommandName = revitCommandName;
            this.PostableCommand = postableCommand;
            this.MasterPostableCommandSetting = masterPostableCommandSettings;
            this.InterventionSettings = interventionSettings;
        }

        // Constructor 2: PostableCommand with intervention settings only
        public CommandBindingIntervention(
            UIControlledApplication app,
            PostableCommand postableCommand,
            string revitCommandName,
            InterventionSettings interventionSettings)
        {
            this.App = app;
            this.RevitCommandName = revitCommandName;
            this.PostableCommand = postableCommand;
            this.InterventionSettings = interventionSettings;
        }

        // Constructor 3: String command ID
        public CommandBindingIntervention(
            UIControlledApplication app,
            string revitCommandName,
            PostableCommandSettings masterPostableCommandSetting,
            bool forCompanyLevelCommand = false)
        {
            this.App = app;
            this.RevitCommandName = revitCommandName;
            this.MasterPostableCommandSetting = masterPostableCommandSetting;
            this.isForCompanyLevelCommand = forCompanyLevelCommand;
        }

        /// <summary>
        /// Register with Executed event (post-execution)
        /// </summary>
        public void Register(bool bindBeforeExecuted = false)
        {
            try
            {
                this.commandId = string.IsNullOrEmpty(this.RevitCommandName) 
                    ? RevitCommandId.LookupPostableCommandId(this.PostableCommand) 
                    : RevitCommandId.LookupCommandId(this.RevitCommandName);
                
                if (!this.commandId.CanHaveBinding)
                    return;
                
                try
                {
                    this.binding = this.App.CreateAddInCommandBinding(this.commandId);
                    
                    if (this.binding == null)
                        return;
                    
                    // Subscribe to Executed event
                    this.binding.Executed += Binding_Executed;
                    
                    // Optionally subscribe to BeforeExecuted
                    if (bindBeforeExecuted)
                        this.binding.BeforeExecuted += Binding_BeforeExecuted;
                }
                catch (Exception ex)
                {
                    LogException(ex, "CommandBindingFailed", this.RevitCommandName);
                }
            }
            catch (Exception ex)
            {
                LogException(ex, "CommandBindingFailed2", this.RevitCommandName);
            }
        }

        /// <summary>
        /// Register with BeforeExecuted event (pre-execution, can cancel)
        /// </summary>
        public void RegisterWithBeforeExecute(bool bindExecuted = false)
        {
            try
            {
                this.commandId = string.IsNullOrEmpty(this.RevitCommandName) 
                    ? RevitCommandId.LookupPostableCommandId(this.PostableCommand) 
                    : RevitCommandId.LookupCommandId(this.RevitCommandName);
                
                RevitCommandId commandId = this.commandId;
                if ((commandId != null ? (commandId.CanHaveBinding ? 1 : 0) : 0) == 0)
                    return;
                
                try
                {
                    this.binding = this.App.CreateAddInCommandBinding(this.commandId);
                    
                    // Subscribe to BeforeExecuted event
                    this.binding.BeforeExecuted += Binding_BeforeExecuted;
                    
                    // Optionally subscribe to Executed
                    if (bindExecuted)
                        this.binding.Executed += Binding_Executed;
                }
                catch (Exception ex)
                {
                    LogException(ex, "CommandBindingFailed", this.RevitCommandName);
                }
            }
            catch (Exception ex)
            {
                LogException(ex, "CommandBindingFailed2", this.RevitCommandName);
            }
        }

        /// <summary>
        /// Unregister the command binding
        /// </summary>
        public void Unregister(UIApplication uiApplication = null, bool postCommand = false)
        {
            try
            {
                if (this.binding == null)
                    return;
                
                this.App.RemoveAddInCommandBinding(this.binding.RevitCommandId);
                
                if (((uiApplication == null ? 0 : (this.commandId != null ? 1 : 0)) & (postCommand ? 1 : 0)) != 0)
                    uiApplication.PostCommand(this.commandId);
            }
            catch (Exception ex)
            {
                // Swallow exception
            }
        }

        /// <summary>
        /// Check if command is currently registered
        /// </summary>
        public bool IsRegistered()
        {
            try
            {
                this.commandId = string.IsNullOrEmpty(this.RevitCommandName) 
                    ? RevitCommandId.LookupPostableCommandId(this.PostableCommand) 
                    : RevitCommandId.LookupCommandId(this.RevitCommandName);
                return this.commandId.HasBinding;
            }
            catch (Exception ex)
            {
                LogException(ex, "CommandBindingFailed2", this.RevitCommandName);
            }
            return false;
        }

        /// <summary>
        /// Get the RevitCommandId for this binding
        /// </summary>
        public RevitCommandId GetRevitCommandId()
        {
            return !string.IsNullOrEmpty(this.RevitCommandName) 
                ? RevitCommandId.LookupCommandId(this.RevitCommandName) 
                : RevitCommandId.LookupPostableCommandId(this.PostableCommand);
        }

        private void Binding_BeforeExecuted(object sender, BeforeExecutedEventArgs e)
        {
            // Get parent instance to delegate to command-specific handler
            CommandInterceptionManager.Instance.HandleBeforeExecuted(sender, e, this);
        }

        private void Binding_Executed(object sender, ExecutedEventArgs e)
        {
            // Get parent instance to delegate to command-specific handler
            CommandInterceptionManager.Instance.HandleExecuted(sender, e, this);
        }

        private void LogException(Exception ex, string context, string commandName)
        {
            // Your logging implementation
        }
    }
}
```

---

#### 2. Command Manager Structure

**Refactor CommandInterceptionService/Manager:**

```csharp
public class CommandInterceptionManager
{
    private static CommandInterceptionManager instance;
    private UIControlledApplication App;
    
    // Individual command bindings (The legacy product pattern)
    private CommandBindingIntervention CommandBinding_Pin;
    private CommandBindingIntervention CommandBinding_Unpin;
    private CommandBindingIntervention CommandBinding_Move;
    private CommandBindingIntervention CommandBinding_MirrorDrawAxis;
    private CommandBindingIntervention CommandBinding_MirrorPickAxis;
    private CommandBindingIntervention CommandBinding_CopyInPlaceFamily;
    private CommandBindingIntervention CommandBinding_CopyToClipboardInPlaceFamily;
    private CommandBindingIntervention CommandBinding_ArrayInPlaceFamily;
    private CommandBindingIntervention CommandBinding_CreateGroupRestrictedElement;
    private CommandBindingIntervention CommandBinding_Delete;
    private CommandBindingIntervention CommandBinding_ProjectBrowserDelete;
    private CommandBindingIntervention CommandBinding_ScheduleRowDelete;
    private CommandBindingIntervention CommandBinding_HideElementsInView;
    
    // Special override classes
    private DuplicateCommandOverride CommandBinding_Duplicate;
    private RenameCommandOverride CommandBinding_Rename;
    
    // Master dictionaries for rule-based commands
    internal Dictionary<string, CommandBindingIntervention> CommandBinding_MasterPostableCommands;
    
    public static CommandInterceptionManager Instance
    {
        get
        {
            if (instance == null)
                instance = new CommandInterceptionManager();
            return instance;
        }
    }
    
    public void Setup(UIControlledApplication app)
    {
        this.App = app;
    }
    
    // Main setup method called when document opens
    public void SetupCommandBinding(Document document)
    {
        // 1. Unregister all existing bindings
        UnRegisterExistingCommands();
        
        // 2. Get settings for this document
        var settings = GetApplicableSettings(document);
        
        if (settings == null || !settings.IsEnabled)
            return;
        
        // 3. Setup Pin/Unpin
        SetupPinUnpinCommands(settings);
        
        // 4. Setup Move/Mirror/Copy
        SetupTransformCommands(settings);
        
        // 5. Setup Delete commands
        SetupDeleteCommands(settings);
        
        // 6. Setup special commands (Duplicate, Rename)
        SetupSpecialCommands(settings);
        
        // 7. Setup rule-based commands
        SetupRuleBasedCommands(document, settings);
    }
    
    private void SetupPinUnpinCommands(ProtectionSettings settings)
    {
        // Pin Command (if enabled in settings)
        if (settings?.PromptUserForUnpin ?? false)
        {
            CommandBinding_Pin = new CommandBindingIntervention(
                this.App,
                (PostableCommand)32997, // Pin
                null,
                new InterventionSettings() { IsEnabled = true }
            );
            CommandBinding_Pin?.Register(); // Use Executed event
        }
        else
        {
            CommandBinding_Pin = null;
        }
        
        // Unpin Command (always enabled with custom validator)
        InterventionSettings interventionSettings = new InterventionSettings();
        interventionSettings.IsEnabled = true;
        
        PostableCommandSettings unpinSettings = new PostableCommandSettings()
        {
            CommandCode = ((PostableCommand)33001).ToString(),
            CommandName = "Unpin Elements",
            PostableCommandEnumValue = 33001
        };
        unpinSettings.CompletionValidator = CommandCompletionValidator.Create(unpinSettings);
        
        CommandBinding_Unpin = new CommandBindingIntervention(
            this.App,
            (PostableCommand)33001, // Unpin
            string.Empty,
            unpinSettings,
            interventionSettings
        );
        CommandBinding_Unpin?.Register(); // Use Executed event
    }
    
    private void SetupTransformCommands(ProtectionSettings settings)
    {
        // Move Command
        if (settings?.EnableParameterPrompts ?? false)
        {
            CommandBinding_Move = new CommandBindingIntervention(
                this.App,
                (PostableCommand)33127, // Move
                string.Empty,
                null
            );
            CommandBinding_Move?.RegisterWithBeforeExecute(); // Can cancel before execution
        }
        
        // Mirror Commands
        if (settings?.EnableMirrorFamilyProtection ?? false)
        {
            InterventionSettings interventionSettings = new InterventionSettings();
            interventionSettings.IsEnabled = true;
            
            CommandBinding_MirrorDrawAxis = new CommandBindingIntervention(
                this.App,
                (PostableCommand)49000, // Mirror - Draw Axis
                null,
                interventionSettings
            );
            CommandBinding_MirrorDrawAxis?.RegisterWithBeforeExecute();
            
            CommandBinding_MirrorPickAxis = new CommandBindingIntervention(
                this.App,
                (PostableCommand)32936, // Mirror - Pick Axis
                null,
                interventionSettings
            );
            CommandBinding_MirrorPickAxis?.RegisterWithBeforeExecute();
        }
        
        // Copy/Array Commands
        if (settings?.PromptUserForCopyInPlaceFamily ?? false)
        {
            CommandBinding_CopyInPlaceFamily = new CommandBindingIntervention(
                this.App,
                (PostableCommand)33129, // Copy
                null,
                new InterventionSettings() { IsEnabled = true }
            );
            CommandBinding_CopyInPlaceFamily?.RegisterWithBeforeExecute();
            
            CommandBinding_CopyToClipboardInPlaceFamily = new CommandBindingIntervention(
                this.App,
                (PostableCommand)57634, // Copy to Clipboard
                null,
                new InterventionSettings() { IsEnabled = true }
            );
            CommandBinding_CopyToClipboardInPlaceFamily?.RegisterWithBeforeExecute();
            
            CommandBinding_ArrayInPlaceFamily = new CommandBindingIntervention(
                this.App,
                (PostableCommand)33121, // Array
                null,
                new InterventionSettings() { IsEnabled = true }
            );
            CommandBinding_ArrayInPlaceFamily?.RegisterWithBeforeExecute();
        }
    }
    
    private void SetupDeleteCommands(ProtectionSettings settings)
    {
        if (settings?.PromptUserForDeleteWithDependents ?? false)
        {
            // Implementation similar to the legacy product's SetCommandBindingForDeleteWithDependents
            // Create bindings for Delete, ProjectBrowserDelete, ScheduleRowDelete
        }
    }
    
    private void SetupSpecialCommands(ProtectionSettings settings)
    {
        // Duplicate Command (using separate override class)
        CommandBinding_Duplicate = DuplicateCommandOverride.GetInstance(this.App);
        CommandBinding_Duplicate?.RegisterWithBeforeExecute();
        
        // Rename Command (using separate override class)
        CommandBinding_Rename = RenameCommandOverride.GetInstance(this.App);
        CommandBinding_Rename?.RegisterWithBeforeExecute();
    }
    
    private void SetupRuleBasedCommands(Document document, ProtectionSettings settings)
    {
        // Get rules from your configuration
        var rules = GetRulesForDocument(document);
        
        if (rules == null || !rules.Any())
            return;
        
        if (CommandBinding_MasterPostableCommands == null)
            CommandBinding_MasterPostableCommands = new Dictionary<string, CommandBindingIntervention>();
        
        foreach (var rule in rules)
        {
            foreach (var commandId in rule.CommandIds)
            {
                string commandKey = commandId.ToString();
                
                PostableCommandSettings commandSettings = new PostableCommandSettings()
                {
                    CommandCode = commandKey,
                    CommandName = GetCommandName(commandId),
                    PostableCommandEnumValue = commandId,
                    // Map rule properties to settings
                    Message = rule.Message,
                    Mode = (int)rule.InterventionMode,
                    // etc.
                };
                
                if (CommandBinding_MasterPostableCommands.ContainsKey(commandKey))
                {
                    CommandBinding_MasterPostableCommands[commandKey] = new CommandBindingIntervention(
                        this.App,
                        (PostableCommand)commandId,
                        null,
                        commandSettings
                    );
                }
                else
                {
                    CommandBinding_MasterPostableCommands.Add(commandKey, new CommandBindingIntervention(
                        this.App,
                        (PostableCommand)commandId,
                        null,
                        commandSettings
                    ));
                }
                
                // Register based on whether we need BeforeExecuted
                if (rule.RequiresPreExecutionValidation)
                    CommandBinding_MasterPostableCommands[commandKey].RegisterWithBeforeExecute();
                else
                    CommandBinding_MasterPostableCommands[commandKey].Register();
            }
        }
    }
    
    internal void UnRegisterExistingCommands()
    {
        // Unregister individual command bindings
        CommandBinding_Pin?.Unregister();
        CommandBinding_Unpin?.Unregister();
        CommandBinding_Move?.Unregister();
        CommandBinding_MirrorDrawAxis?.Unregister();
        CommandBinding_MirrorPickAxis?.Unregister();
        CommandBinding_CopyInPlaceFamily?.Unregister();
        CommandBinding_CopyToClipboardInPlaceFamily?.Unregister();
        CommandBinding_ArrayInPlaceFamily?.Unregister();
        CommandBinding_Delete?.Unregister();
        CommandBinding_ProjectBrowserDelete?.Unregister();
        CommandBinding_ScheduleRowDelete?.Unregister();
        CommandBinding_HideElementsInView?.Unregister();
        
        // Unregister special commands
        CommandBinding_Duplicate?.Unregister();
        CommandBinding_Rename?.Unregister();
        
        // Unregister master commands
        if (CommandBinding_MasterPostableCommands != null)
        {
            foreach (string key in CommandBinding_MasterPostableCommands.Keys)
            {
                CommandBinding_MasterPostableCommands[key]?.Unregister();
            }
        }
    }
    
    // Event handler delegation
    public void HandleBeforeExecuted(object sender, BeforeExecutedEventArgs e, CommandBindingIntervention binding)
    {
        Document document = sender is UIApplication application 
            ? application.ActiveUIDocument?.Document 
            : null;
        
        if (document == null)
            return;
        
        // Determine which command was invoked
        RevitCommandId commandId = binding.GetRevitCommandId();
        
        // Route to appropriate handler based on command
        if (commandId.Name == "ID_OBJECTS_MOVE" || commandId.Id == 33127)
        {
            HandleMoveBeforeExecuted(document, e);
        }
        else if (commandId.Id == 49000 || commandId.Id == 32936) // Mirror commands
        {
            HandleMirrorBeforeExecuted(document, e);
        }
        // Add more routing as needed
    }
    
    public void HandleExecuted(object sender, ExecutedEventArgs e, CommandBindingIntervention binding)
    {
        Document document = sender is UIApplication application 
            ? application.ActiveUIDocument?.Document 
            : null;
        
        if (document == null)
            return;
        
        RevitCommandId commandId = binding.GetRevitCommandId();
        
        // Route to appropriate handler
        if (commandId.Id == 32997) // Pin
        {
            HandlePinExecuted(document);
        }
        else if (commandId.Id == 33001) // Unpin
        {
            HandleUnpinExecuted(document);
        }
        // Add more routing as needed
    }
    
    // Command-specific handlers
    private void HandleMoveBeforeExecuted(Document document, BeforeExecutedEventArgs e)
    {
        // Your validation logic
        bool shouldCancel = ValidateMoveCommand(document);
        
        if (shouldCancel)
        {
            e.Cancel();
            TaskDialog.Show("Move Prevented", "Movement is restricted by protection rules.");
        }
    }
    
    private void HandlePinExecuted(Document document)
    {
        // Post-execution tracking
        var selectedElements = GetSelectedElements(document);
        ProcessPinCommand(document, selectedElements);
    }
    
    private bool ValidateMoveCommand(Document document)
    {
        // Your rule evaluation logic here
        var selectedElements = GetSelectedElements(document);
        var rules = GetApplicableRules(document);
        
        return EvaluateRules(selectedElements, rules);
    }
}
```

---

#### 3. Separate Override Classes

**Create DuplicateCommandOverride.cs:**

```csharp
public class DuplicateCommandOverride
{
    private static DuplicateCommandOverride _instance;
    private string RevitCommandName;
    private RevitCommandId commandId;
    private UIControlledApplication App;
    private AddInCommandBinding binding;
    
    public bool SkipDuplicateCheck { get; set; }
    
    public static DuplicateCommandOverride GetInstanceIfExists()
    {
        return DuplicateCommandOverride._instance;
    }
    
    public static DuplicateCommandOverride GetInstance(UIControlledApplication app)
    {
        if (DuplicateCommandOverride._instance == null)
            DuplicateCommandOverride._instance = new DuplicateCommandOverride(app);
        return DuplicateCommandOverride._instance;
    }
    
    public DuplicateCommandOverride(UIControlledApplication app)
    {
        this.RevitCommandName = "ID_SYM_CLONE";
        this.App = app;
    }
    
    public void RegisterWithBeforeExecute()
    {
        try
        {
            if (!string.IsNullOrEmpty(this.RevitCommandName))
                this.commandId = RevitCommandId.LookupCommandId(this.RevitCommandName);
            
            RevitCommandId commandId = this.commandId;
            if ((commandId != null ? (commandId.CanHaveBinding ? 1 : 0) : 0) == 0)
                return;
            
            try
            {
                this.binding = this.App.CreateAddInCommandBinding(this.commandId);
                this.binding.BeforeExecuted += new EventHandler<BeforeExecutedEventArgs>(this.Binding_BeforeExecuted);
            }
            catch (Exception ex)
            {
                // Log exception
            }
        }
        catch (Exception ex)
        {
            // Log exception
        }
    }
    
    private void Binding_BeforeExecuted(object sender, BeforeExecutedEventArgs e)
    {
        this.SkipDuplicateCheck = true;
        
        // Coordinate with Rename if needed
        RenameCommandOverride renameInstance = RenameCommandOverride.GetInstanceIfExists();
        if (renameInstance != null)
            renameInstance.ExplicitRenameActive = false;
    }
    
    public void Unregister(UIApplication uiApplication = null, bool postCommand = false)
    {
        try
        {
            this.App.RemoveAddInCommandBinding(this.binding.RevitCommandId);
            if (((uiApplication == null ? 0 : (this.commandId != null ? 1 : 0)) & (postCommand ? 1 : 0)) != 0)
                uiApplication.PostCommand(this.commandId);
        }
        catch
        {
        }
    }
}
```

**Create RenameCommandOverride.cs** (similar pattern)

---

## Verification Plan

### 1. Unit Testing Each Command

Test each command binding individually:

```csharp
[Test]
public void TestPinCommandBinding()
{
    // Setup
    var manager = CommandInterceptionManager.Instance;
    manager.Setup(uiControlledApp);
    
    // Register
    manager.SetupCommandBinding(document);
    
    // Verify binding exists
    Assert.IsNotNull(manager.CommandBinding_Pin);
    Assert.IsTrue(manager.CommandBinding_Pin.IsRegistered());
}
```

### 2. Integration Testing

- Test command execution flow
- Verify BeforeExecuted cancellation works
- Verify Executed event tracking works
- Test with actual Revit commands

### 3. Rule Evaluation Testing

- Ensure rules still evaluate correctly
- Test RBAC integration
- Verify admin override behavior

---

## Migration Steps

### Step 1: Create New Classes (Non-Breaking)

1. Create `CommandBindingIntervention` nested class
2. Create `DuplicateCommandOverride` class
3. Create `RenameCommandOverride` class

### Step 2: Add Individual Command Fields

Add private fields to existing manager:

```csharp
// Add alongside existing code
private CommandBindingIntervention CommandBinding_Pin;
private CommandBindingIntervention CommandBinding_Unpin;
// etc.
```

### Step 3: Implement New Setup Methods

Create new setup methods that don't interfere with existing:

```csharp
public void SetupCommandBinding_the legacy product(Document document)
{
    // New legacy-style setup
}
```

### Step 4: Parallel Testing

Run both systems side-by-side temporarily to verify behavior.

### Step 5: Switch Over

Replace old registration calls with new ones:

```csharp
// Old:
// _commandInterceptionService.RegisterCommandBindings(uiApp);

// New:
CommandInterceptionManager.Instance.SetupCommandBinding(document);
```

### Step 6: Remove Old Code

Once verified working, remove:
- Old centralized registration
- `IsEditModeCommand()` guard (no longer needed)
- Old event registry pattern

---

## Benefits of command interception pattern

1. **Clear Command Ownership**: Each command has its own field and setup
2. **Flexible Event Handling**: Choose BeforeExecuted vs Executed per command
3. **Easier Debugging**: Individual bindings are easier to trace
4. **Better Separation**: Complex commands get their own override classes
5. **Proven Pattern**: The legacy product has used this successfully in production
6. **No Edit Mode Issues**: Proper event handling eliminates the need for guards

---

## Breaking Changes

> [!WARNING]
> This is a significant architectural change. Ensure thorough testing before production deployment.

### Code That Will Change

1. **Document event handlers** - must call new setup methods
2. **Rule evaluation** - may need adjustment for new binding structure
3. **Any code checking command bindings** - use new accessor pattern

### Code That Won't Change

1. Rule definitions (JSON/database)
2. RBAC logic
3. UI components
4. External API contracts
