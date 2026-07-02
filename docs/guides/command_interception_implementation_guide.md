# Command Interception Implementation Guide for CBOX Manage

## Overview

This guide explains how the legacy product implements command interception for various Revit commands (Pin, Unpin, Move, Mirror, Delete, etc.) and how to replicate this functionality in CBOX Manage. The implementation uses Revit's `AddInCommandBinding` API to intercept commands before and/or after they execute.

---

## Core Concepts

### 1. **AddInCommandBinding System**

Revit provides the `AddInCommandBinding` class that allows plugins to intercept built-in Revit commands:

- **BeforeExecuted**: Event fired before the command executes (allows cancellation)
- **Executed**: Event fired after the command completes
- **RevitCommandId**: Identifier for the Revit command to intercept

### 2. **Two Types of Command Identifiers**

the legacy product uses two ways to identify commands:

1. **PostableCommand Enum** - For commands that have enum values (e.g., `PostableCommand.Pin` = 32997)
2. **String Command IDs** - For commands without enum values (e.g., `"ID_SYM_CLONE"` for Duplicate)

---

## Implementation Pattern

### Step 1: Create a Command Binding Class Structure

the legacy product uses a nested class `CommandBindingIntervention` inside `CommandIntervention`:

```csharp
public class CommandBindingIntervention
{
    private string RevitCommandName;
    private RevitCommandId commandId;
    private PostableCommand PostableCommand;
    private UIControlledApplication App;
    private AddInCommandBinding binding;
    private PostableCommandSettings MasterPostableCommandSetting;
    
    // Constructors
    public CommandBindingIntervention(
        UIControlledApplication app,
        PostableCommand postableCommand,
        string revitCommandName,
        PostableCommandSettings masterPostableCommandSettings)
    {
        this.App = app;
        this.RevitCommandName = revitCommandName;
        this.PostableCommand = postableCommand;
        this.MasterPostableCommandSetting = masterPostableCommandSettings;
    }
    
    // Methods: Register(), RegisterWithBeforeExecute(), Unregister()
}
```

### Step 2: Registration Methods

There are two main registration patterns:

#### **Pattern 1: Register (Executed Event Only)**

Used when you want to intercept AFTER command execution:

```csharp
public void Register(bool bindBeforeExecuted = false)
{
    try
    {
        // Get the RevitCommandId
        this.commandId = string.IsNullOrEmpty(this.RevitCommandName) 
            ? RevitCommandId.LookupPostableCommandId(this.PostableCommand) 
            : RevitCommandId.LookupCommandId(this.RevitCommandName);
        
        // Check if binding is allowed
        if (!this.commandId.CanHaveBinding)
            return;
        
        // Create the binding
        this.binding = this.App.CreateAddInCommandBinding(this.commandId);
        
        if (this.binding == null)
            return;
        
        // Subscribe to Executed event
        this.binding.Executed += new EventHandler<ExecutedEventArgs>(this.Binding_Executed);
        
        // Optionally subscribe to BeforeExecuted
        if (bindBeforeExecuted)
            this.binding.BeforeExecuted += new EventHandler<BeforeExecutedEventArgs>(this.Binding_BeforeExecuted);
    }
    catch (Exception ex)
    {
        // Log exception
    }
}
```

#### **Pattern 2: RegisterWithBeforeExecute (BeforeExecuted Event)**

Used when you want to intercept BEFORE command execution (allows cancellation):

```csharp
public void RegisterWithBeforeExecute(bool bindExecuted = false)
{
    try
    {
        // Get the RevitCommandId
        this.commandId = string.IsNullOrEmpty(this.RevitCommandName) 
            ? RevitCommandId.LookupPostableCommandId(this.PostableCommand) 
            : RevitCommandId.LookupCommandId(this.RevitCommandName);
        
        // Check if binding is allowed
        RevitCommandId commandId = this.commandId;
        if ((commandId != null ? (commandId.CanHaveBinding ? 1 : 0) : 0) == 0)
            return;
        
        // Create the binding
        this.binding = this.App.CreateAddInCommandBinding(this.commandId);
        
        // Subscribe to BeforeExecuted event
        this.binding.BeforeExecuted += new EventHandler<BeforeExecutedEventArgs>(this.Binding_BeforeExecuted);
        
        // Optionally subscribe to Executed
        if (bindExecuted)
            this.binding.Executed += new EventHandler<ExecutedEventArgs>(this.Binding_Executed);
    }
    catch (Exception ex)
    {
        // Log exception
    }
}
```

### Step 3: Unregister Method

Always provide a way to remove bindings:

```csharp
public void Unregister(UIApplication uiApplication = null, bool postCommand = false)
{
    try
    {
        if (this.binding == null)
            return;
        
        // Remove the binding
        this.App.RemoveAddInCommandBinding(this.binding.RevitCommandId);
        
        // Optionally re-post the command
        if (((uiApplication == null ? 0 : (this.commandId != null ? 1 : 0)) & (postCommand ? 1 : 0)) != 0)
            uiApplication.PostCommand(this.commandId);
    }
    catch (Exception ex)
    {
        // Swallow exception
    }
}
```

---

## Specific Command Implementations

### 1. **Pin Command** (PostableCommand Enum)

```csharp
// Store the binding
private CommandBindingIntervention CommandBinding_Pin;

// Setup (in SetupCommandBinding method)
if (interactionSettings != null && (interactionSettings.PromptUserForUnpin ?? false))
{
    CommandBinding_Pin = new CommandBindingIntervention(
        this.App, 
        (PostableCommand)32997,  // Pin command enum value
        null, 
        new InterventionSettings() { IsEnabled = new bool?(true) }
    );
}
else
{
    CommandBinding_Pin = null;
}

// Register using Executed event
CommandBinding_Pin?.Register();

// Handler
private void ProcessPinCommand(Document document, IEnumerable<Element> selectedElements)
{
    return this.Showthe legacy productElementPinProtection(document, selectedElements);
}
```

### 2. **Unpin Command** (PostableCommand Enum with Custom Execution)

This is the "workaround" you mentioned - using a custom execution flow:

```csharp
// Store the binding
private CommandBindingIntervention CommandBinding_Unpin;

// Setup
InterventionSettings interventionSettings = new InterventionSettings();
interventionSettings.IsEnabled = new bool?(true);

PostableCommandSettings postableCommandSettings = new PostableCommandSettings()
{
    CommandCode = ((PostableCommand)33001).ToString(),  // Unpin
    CommandName = "Unpin Elements",
    PostableCommandEnumValue = new int?(33001)
};

// Create validation/completion logic
postableCommandSettings.CompletionValidator = CommandCompletionValidator.Create(postableCommandSettings);

CommandBinding_Unpin = new CommandBindingIntervention(
    this.App, 
    (PostableCommand)33001, 
    string.Empty, 
    postableCommandSettings, 
    interventionSettings
);

// Register using Executed event
CommandBinding_Unpin?.Register();
```

### 3. **Move Command** (PostableCommand Enum - BeforeExecuted)

```csharp
// Store the binding
private CommandBindingIntervention CommandBinding_Move;

// Setup
if (interactionSettings != null && (interactionSettings.EnableParameterPrompts ?? false))
{
    CommandBinding_Move = new CommandBindingIntervention(
        this.App, 
        (PostableCommand)33127,  // Move command
        string.Empty, 
        null
    );
    
    // Register with BeforeExecuted event
    CommandBinding_Move?.RegisterWithBeforeExecute();
}
```

### 4. **Mirror Commands** (Multiple PostableCommands)

```csharp
// Store the bindings
private CommandBindingIntervention CommandBinding_MirrorDrawAxis;
private CommandBindingIntervention CommandBinding_MirrorPickAxis;

// Setup
if (interactionSettings?.EnableMirrorFamilyProtection ?? false)
{
    InterventionSettings interventionSettings = new InterventionSettings();
    interventionSettings.IsEnabled = new bool?(true);
    
    // Mirror - Draw Axis
    CommandBinding_MirrorDrawAxis = new CommandBindingIntervention(
        this.App, 
        (PostableCommand)49000,  // Mirror Draw Axis
        null, 
        interventionSettings
    );
    CommandBinding_MirrorDrawAxis?.RegisterWithBeforeExecute();
    
    // Mirror - Pick Axis
    CommandBinding_MirrorPickAxis = new CommandBindingIntervention(
        this.App, 
        (PostableCommand)32936,  // Mirror Pick Axis
        null, 
        interventionSettings
    );
    CommandBinding_MirrorPickAxis?.RegisterWithBeforeExecute();
}
```

### 5. **Duplicate Command** (String Command ID - Separate Class Pattern)

For some commands, the legacy product uses a separate class:

```csharp
public class DuplicateCommandOverride
{
    private static DuplicateCommandOverride _instance;
    private string RevitCommandName;
    private RevitCommandId commandId;
    private UIControlledApplication App;
    private AddInCommandBinding binding;
    
    public bool SkipDuplicateCheck { get; set; }
    
    public DuplicateCommandOverride(UIControlledApplication app)
    {
        this.RevitCommandName = "ID_SYM_CLONE";  // String ID for Duplicate
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
        // Your custom logic here
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

### 6. **Rename Command** (String Command ID)

```csharp
public class RenameCommandOverride
{
    private static RenameCommandOverride _instance;
    private string RevitCommandName;
    private RevitCommandId commandId;
    private UIControlledApplication App;
    private AddInCommandBinding binding;
    
    public bool ExplicitRenameActive { get; set; }
    
    public RenameCommandOverride(UIControlledApplication app)
    {
        this.RevitCommandName = "ID_PRJBROWSER_RENAME";  // String ID for Rename
        this.App = app;
    }
    
    // Similar implementation to DuplicateCommandOverride
}
```

---

## Event Handler Patterns

### BeforeExecuted Event Handler

Used to intercept BEFORE command runs (can cancel):

```csharp
private void Binding_BeforeExecuted(object sender, BeforeExecutedEventArgs e)
{
    try
    {
        // Get document
        Document document = sender is UIApplication application 
            ? application.ActiveUIDocument_safe()?.Document 
            : null;
        
        // Your custom logic
        bool shouldCancel = YourCustomValidationLogic(document);
        
        if (shouldCancel)
        {
            // Cancel the command
            e.Cancel();
        }
    }
    catch (Exception ex)
    {
        // Handle exception
    }
}
```

### Executed Event Handler

Used to intercept AFTER command runs:

```csharp
private void Binding_Executed(object sender, ExecutedEventArgs e)
{
    try
    {
        // Get document
        Document document = sender is UIApplication application 
            ? application.ActiveUIDocument_safe()?.Document 
            : null;
        
        // Your custom post-execution logic
        YourCustomPostExecutionLogic(document);
    }
    catch (Exception ex)
    {
        // Handle exception
    }
}
```

---

## Common Revit Command IDs

Here are the command IDs the legacy product uses:

### PostableCommand Enums
| Command | Enum Value | Usage |
|---------|-----------|--------|
| Pin | 32997 | `(PostableCommand)32997` |
| Unpin | 33001 | `(PostableCommand)33001` |
| Move | 33127 | `(PostableCommand)33127` |
| Mirror - Draw Axis | 49000 | `(PostableCommand)49000` |
| Mirror - Pick Axis | 32936 | `(PostableCommand)32936` |
| Copy | 33129 | `(PostableCommand)33129` |
| Array | 33121 | `(PostableCommand)33121` |
| Copy to Clipboard | 57634 | `(PostableCommand)57634` |
| Delete | 35256 | `(PostableCommand)35256` |
| Create Group | 33305 | `(PostableCommand)33305` |
| Hide Elements in View | 35261 | `(PostableCommand)35261` |

### String Command IDs
| Command | String ID |
|---------|-----------|
| Duplicate | `"ID_SYM_CLONE"` |
| Rename | `"ID_PRJBROWSER_RENAME"` |
| Project Browser Delete | `"ID_PRJBROWSER_DELETE"` |
| Schedule Delete Row | `"ID_DELETE_ROWS"` |
| Wall Opening Modify | `"ID_CREATE_WALL_OPENING"` |
| Full Explode (Context Menu) | `"ID_IMPORT_INSTANCE_EXPLODE"` |
| Partial Explode (Context Menu) | `"ID_IMPORT_INST_PARTIAL_EXPLODE"` |
| Sync to Central | `"ID_FILE_SAVE_TO_CENTRAL"` |
| Sync to Central (Shortcut) | `"ID_FILE_SAVE_TO_CENTRAL_SHORTCUT"` |

---

## Complete Workflow Example

### 1. Initialize During Startup

```csharp
public class MyApplication : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        // Initialize your command intervention manager
        CommandInterventionManager.Instance.Setup(application);
        
        return Result.Succeeded;
    }
}
```

### 2. Setup Command Bindings When Document Opens

```csharp
application.ControlledApplication.DocumentOpened += (sender, e) =>
{
    CommandInterventionManager.Instance.SetupCommandBinding(e.Document);
};
```

### 3. Command Intervention Manager Structure

```csharp
public class CommandInterventionManager
{
    private static CommandInterventionManager instance;
    private UIControlledApplication App;
    
    // Store bindings
    private CommandBindingIntervention CommandBinding_Pin;
    private CommandBindingIntervention CommandBinding_Unpin;
    private CommandBindingIntervention CommandBinding_Move;
    // ... more bindings
    
    public static CommandInterventionManager Instance
    {
        get
        {
            if (instance == null)
                instance = new CommandInterventionManager();
            return instance;
        }
    }
    
    public void Setup(UIControlledApplication app)
    {
        this.App = app;
    }
    
    public void SetupCommandBinding(Document document)
    {
        // Unregister existing
        UnRegisterExistingCommands();
        
        // Get settings
        var settings = GetYourSettings(document);
        
        // Setup Pin
        if (settings.EnablePinProtection)
        {
            CommandBinding_Pin = new CommandBindingIntervention(
                this.App,
                (PostableCommand)32997,
                null,
                new InterventionSettings() { IsEnabled = true }
            );
            CommandBinding_Pin?.Register();
        }
        
        // Setup Unpin
        CommandBinding_Unpin = new CommandBindingIntervention(
            this.App,
            (PostableCommand)33001,
            string.Empty,
            CreateUnpinSettings()
        );
        CommandBinding_Unpin?.Register();
        
        // Setup Move
        if (settings.EnableMoveProtection)
        {
            CommandBinding_Move = new CommandBindingIntervention(
                this.App,
                (PostableCommand)33127,
                string.Empty,
                null
            );
            CommandBinding_Move?.RegisterWithBeforeExecute();
        }
        
        // ... setup other commands
    }
    
    internal void UnRegisterExistingCommands()
    {
        CommandBinding_Pin?.Unregister();
        CommandBinding_Unpin?.Unregister();
        CommandBinding_Move?.Unregister();
        // ... unregister other commands
    }
}
```

---

## Key Differences Between Commands

### When to Use `Register()` vs `RegisterWithBeforeExecute()`

| Method | Use Case | Event | Can Cancel? |
|--------|----------|-------|-------------|
| `Register()` | Post-execution validation, logging, state tracking | `Executed` | ❌ No |
| `RegisterWithBeforeExecute()` | Pre-execution validation, prevent action | `BeforeExecuted` | ✅ Yes |

**Examples:**
- **Pin Command**: Uses `Register()` - tracks what gets pinned after execution
- **Unpin Command**: Uses `Register()` with custom validation logic
- **Move Command**: Uses `RegisterWithBeforeExecute()` - can prevent move before it happens
- **Mirror Commands**: Use `RegisterWithBeforeExecute()` - can prevent mirroring protected families

---

## Best Practices

### 1. **Always Unregister Before Re-registering**

```csharp
public void SetupCommandBinding(Document document)
{
    // Always unregister first
    UnRegisterExistingCommands();
    
    // Then register new bindings
    // ...
}
```

### 2. **Handle Exceptions Gracefully**

```csharp
try
{
    this.binding = this.App.CreateAddInCommandBinding(this.commandId);
    // ...
}
catch (Exception ex)
{
    // Log but don't crash
    DiagnosticLogging.LogException(ex);
}
```

### 3. **Check `CanHaveBinding` Before Creating Binding**

```csharp
if (!this.commandId.CanHaveBinding)
    return;
```

### 4. **Use Singleton Pattern for Manager Classes**

```csharp
private static CommandInterventionManager instance;

public static CommandInterventionManager Instance
{
    get
    {
        if (instance == null)
            instance = new CommandInterventionManager();
        return instance;
    }
}
```

### 5. **Separate Concerns**

For complex commands like Duplicate and Rename, create separate override classes rather than putting everything in one manager class.

---

## Document Change Tracking Integration

the legacy product integrates command interception with document change tracking:

```csharp
public static bool IsSkipDuplicateCheckOn(
    DocumentChangedEventArgs e,
    SoapInterceptedDocChangeEventStorage storage)
{
    if (e.GetTransactionNames().Count == 1 
        && string.Equals(e.GetTransactionNames().FirstOrDefault(), "Modify Type Attribute")
        && storage?.EventChangeset?.AddedElementCount.GetValueOrDefault() == 1
        && storage?.EventChangeset?.ModifiedElementCount.GetValueOrDefault() == 0
        && storage?.EventChangeset?.DeletedElementCount.GetValueOrDefault() == 0)
    {
        DuplicateCommandOverride instance = DuplicateCommandOverride.GetInstanceIfExists();
        if (instance != null && instance.SkipDuplicateCheck)
            return true;
    }
    return false;
}
```

This allows you to correlate command execution with document changes.

---

## Troubleshooting

### Issue: Binding Not Working

**Check:**
1. Is `CanHaveBinding` true for the command?
2. Is the command ID correct?
3. Are you unregistering and re-registering correctly?
4. Is UIControlledApplication available?

### Issue: Command Fires Multiple Times

**Solution:** Ensure you're unregistering existing bindings before registering new ones.

### Issue: Event Handler Not Called

**Check:**
1. Is the binding actually registered?
2. Is the event handler correctly subscribed?
3. Are you handling exceptions that might suppress the handler?

---

## Summary

To implement command interception for each command:

1. **Create a CommandBindingIntervention instance** with the appropriate `PostableCommand` enum or string command ID
2. **Choose registration method**:
   - Use `Register()` for post-execution (Executed event)
   - Use `RegisterWithBeforeExecute()` for pre-execution validation (BeforeExecuted event)
3. **Implement event handlers** with your custom logic
4. **Always unregister** before re-registering or on document close
5. **Handle exceptions** gracefully to prevent crashes

This pattern is highly flexible and can be applied to any Revit command that supports `AddInCommandBinding`.
