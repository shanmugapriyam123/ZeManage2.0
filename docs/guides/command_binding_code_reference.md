# The legacy product Command Binding - Code Reference

## Executive Summary

This document shows the **actual the legacy product code** for creating and managing Revit command bindings, proving that bindings **intercept** commands without replacing them.

---

## 🔧 Step 1: Creating Command Bindings

### **Location:** `CommandIntervention.cs` Line 370-490

```csharp
public void SetupCommandBinding(Document document, bool force = false)
{
    // Get user interaction settings from config
    UserInteractionSettings interactionSettings = 
        SoapCommonUtils.GetApplicableUserInteractionSettings(soapDocumentCookie, true);
    
    // Initialize command binding dictionary
    if (this.CommandBinding_MasterPostableCommands == null)
        this.CommandBinding_MasterPostableCommands = 
            new Dictionary<string, CommandBindingIntervention>();
    
    // Loop through all configured commands to intercept
    foreach (PostableCommandSettings commandSettings in configuredCommands)
    {
        // LINE 449: Create binding wrapper for this command
        if (CommandBinding_MasterPostableCommands.ContainsKey(commandSettings.CommandCode))
        {
            // Update existing binding
            CommandBinding_MasterPostableCommands[commandSettings.CommandCode] = 
                new CommandBindingIntervention(
                    this.App,                                    // UIApplication
                    (PostableCommand)commandSettings.PostableCommandEnumValue.Value,  // e.g., PostableCommand.Delete
                    null,                                        // Optional command name
                    commandSettings                              // Settings (Monitor/Guide/Prevent)
                );
        }
        else
        {
            // LINE 451: Add new binding
            CommandBinding_MasterPostableCommands.Add(
                commandSettings.CommandCode, 
                new CommandBindingIntervention(
                    this.App, 
                    (PostableCommand)commandSettings.PostableCommandEnumValue.Value,
                    null, 
                    commandSettings
                )
            );
        }
        
        // LINE 473-482: Register the binding with Revit
        // Different commands need different registration methods
        
        if (commandSettings.CommandCode == "33305") // Group command
        {
            bool enableGroupProtection = 
                interactionSettings?.EnableGroupProtection ?? false;
            
            // Register with BeforeExecuted handler
            CommandBinding_MasterPostableCommands[commandSettings.CommandCode]
                .Register(bindBeforeExecuted: enableGroupProtection);
        }
        else if (commandSettings.CommandCode == "57607" || 
                 commandSettings.CommandCode == "35256" || 
                 commandSettings.CommandCode == "4700")
        {
            // LINE 482: Register with BeforeExecute ONLY (no Executed handler)
            CommandBinding_MasterPostableCommands[commandSettings.CommandCode]
                .RegisterWithBeforeExecute();
        }
        else
        {
            // LINE 477: Standard registration (Executed handler only by default)
            CommandBinding_MasterPostableCommands[commandSettings.CommandCode]
                .Register();
        }
    }
}
```

**What this does:**
1. Creates a `CommandBindingIntervention` object for each command to intercept
2. Stores them in a dictionary keyed by command code (e.g., "32778" for Delete)
3. Calls `.Register()` or `.RegisterWithBeforeExecute()` to attach to Revit

---

## 🎯 Step 2: Registering Bindings with Revit

### **Location:** `CommandIntervention.cs` Line 1974-2008

```csharp
// Standard registration: Executed handler (for monitoring after execution)
public void Register(bool bindBeforeExecuted = false)
{
    try
    {
        // LINE 1978: Look up Revit's command ID
        this.commandId = string.IsNullOrEmpty(this.RevitCommandName) 
            ? RevitCommandId.LookupPostableCommandId(this.PostableCommand)  // e.g., PostableCommand.Delete
            : RevitCommandId.LookupCommandId(this.RevitCommandName);       // e.g., "ID_DELETE"
        
        // Check if this command supports bindings
        if (!this.commandId.CanHaveBinding)
            return;
        
        try
        {
            // LINE 1983: 🔑 CREATE THE BINDING (attach to Revit's command)
            this.binding = this.App.CreateAddInCommandBinding(this.commandId);
            
            if (this.binding == null)
                return;
            
            // LINE 1989: Attach "Executed" handler (fires AFTER command runs)
            if (this.isForCompanyLevelCommand)
                this.binding.Executed += this.Binding_Executed_companyLevel;
            else
                this.binding.Executed += this.Binding_Executed;
            
            // LINE 1990-1997: Optionally attach "BeforeExecuted" handler
            if (!bindBeforeExecuted)
                return;
            
            if (this.MasterPostableCommandSetting != null)
                this.MasterPostableCommandSetting.IsCommandRegisteredForCustomInteraction = true;
            
            // LINE 1995: Attach "BeforeExecuted" handler (fires BEFORE command runs)
            if (this.isForCompanyLevelCommand)
                this.binding.BeforeExecuted += this.Binding_BeforeExecuted_companyLevel;
            else
                this.binding.BeforeExecuted += this.Binding_BeforeExecuted;
        }
        catch (Exception ex)
        {
            DiagnosticLogging.Instance.LogException(ex, ...);
        }
    }
    catch (Exception ex)
    {
        DiagnosticLogging.Instance.LogException(ex, ...);
    }
}
```

---

### **Alternative Registration:** BeforeExecute Priority

**Location:** `CommandIntervention.cs` Line 2024-2055

```csharp
// For commands that need BeforeExecuted FIRST
public void RegisterWithBeforeExecute(bool bindExecuted = false)
{
    try
    {
        // LINE 2028: Look up Revit's command ID
        this.commandId = string.IsNullOrEmpty(this.RevitCommandName) 
            ? RevitCommandId.LookupPostableCommandId(this.PostableCommand)
            : RevitCommandId.LookupCommandId(this.RevitCommandName);
        
        if (!this.commandId?.CanHaveBinding ?? false)
            return;
        
        try
        {
            // LINE 2034: 🔑 CREATE THE BINDING
            this.binding = this.App.CreateAddInCommandBinding(this.commandId);
            
            // LINE 2036: Attach BeforeExecuted FIRST (priority)
            if (this.isForCompanyLevelCommand)
                this.binding.BeforeExecuted += this.Binding_BeforeExecuted_companyLevel;
            else
                this.binding.BeforeExecuted += this.Binding_BeforeExecuted;
            
            // LINE 2039-2044: Optionally attach Executed handler
            if (!bindExecuted)
                return;
            
            if (this.isForCompanyLevelCommand)
                this.binding.Executed += this.Binding_Executed_companyLevel;
            else
                this.binding.Executed += this.Binding_Executed;
        }
        catch (Exception ex)
        {
            DiagnosticLogging.Instance.LogException(ex, ...);
        }
    }
    catch (Exception ex)
    {
        DiagnosticLogging.Instance.LogException(ex, ...);
    }
}
```

---

## 🔍 Key API Calls

### **1. Looking Up Command IDs**

```csharp
// By PostableCommand enum (preferred)
RevitCommandId commandId = RevitCommandId.LookupPostableCommandId(PostableCommand.Delete);
// Returns: CommandId for Revit's built-in Delete command

// By command name string
RevitCommandId commandId = RevitCommandId.LookupCommandId("ID_DELETE");
// Returns: Same CommandId
```

**What this does:**
- Gets a reference to Revit's **existing** command
- Does NOT create a new command
- Does NOT replace the existing command

---

### **2. Creating the Binding**

```csharp
// UIApplication instance (passed from SoapApplication)
UIApplication app = ...;

// Create binding for the command
AddInCommandBinding binding = app.CreateAddInCommandBinding(commandId);
```

**What `CreateAddInCommandBinding` does (from Revit API):**
- Creates a "hook" into the command's lifecycle
- Returns an `AddInCommandBinding` object
- Does NOT modify or replace the command itself
- Command still runs with its original Revit logic

---

### **3. Attaching Event Handlers**

```csharp
// Fires BEFORE Revit executes the command
binding.BeforeExecuted += Binding_BeforeExecuted;

// Fires AFTER Revit executes the command
binding.Executed += Binding_Executed;
```

**What these do:**
- `BeforeExecuted`: The legacy product can inspect, log, or **cancel** before Revit runs
- `Executed`: The legacy product can inspect, log after Revit runs
- Revit's original command logic runs **between** these two events

---

## 🎯 Example: Delete Command Binding

### **Creating the Binding**

```csharp
// From SetupCommandBinding() - Line 449-451
CommandBinding_MasterPostableCommands.Add(
    "32778",  // Command code for Delete
    new CommandBindingIntervention(
        this.App,                    // UIApplication
        PostableCommand.Delete,      // Revit's Delete command
        null,                        // No custom name
        deleteProtectionSettings     // Monitor/Guide/Prevent config
    )
);

// Register with Revit - Line 477
CommandBinding_MasterPostableCommands["32778"].Register(bindBeforeExecuted: true);
```

### **Inside Register() - Line 1978-1997**

```csharp
// Look up Delete command ID
this.commandId = RevitCommandId.LookupPostableCommandId(PostableCommand.Delete);
// Returns: Revit's built-in Delete command ID

// Create binding
this.binding = this.App.CreateAddInCommandBinding(this.commandId);
// Attaches to Revit's Delete button, keyboard shortcut, context menu, etc.

// Attach our handlers
this.binding.BeforeExecuted += this.Binding_BeforeExecuted;
this.binding.Executed += this.Binding_Executed;
```

**Result:**
```
User clicks Delete button
         ↓
Revit: "Delete command invoked"
         ↓
Revit: "Check for BeforeExecuted handlers..."
         ↓
the legacy product's Binding_BeforeExecuted() fires ✅
         ↓
the legacy product: Evaluate rules, show dialog, decide to cancel or allow
         ↓
If e.Cancel = false:
         ↓
Revit: "Run my ORIGINAL Delete logic" ✅
         ↓
Revit: Delete elements using built-in code
         ↓
Revit: "Check for Executed handlers..."
         ↓
the legacy product's Binding_Executed() fires ✅
         ↓
the legacy product: Capture after screenshot, log event
         ↓
Delete complete!
```

---

## 📊 Command Binding Dictionary

the legacy product stores all bindings in a dictionary:

```csharp
// Field in CommandIntervention class
private Dictionary<string, CommandBindingIntervention> CommandBinding_MasterPostableCommands;

// Example contents:
{
    "32778" -> CommandBindingIntervention(PostableCommand.Delete, settings),
    "32936" -> CommandBindingIntervention(PostableCommand.Mirror, settings),
    "33129" -> CommandBindingIntervention(PostableCommand.Copy, settings),
    "33305" -> CommandBindingIntervention(PostableCommand.Group, settings),
    // ... dozens more
}
```

**Access pattern:**

```csharp
// Get binding for Delete command
var deleteBinding = CommandBinding_MasterPostableCommands["32778"];

// Unregister (remove handler)
deleteBinding.Unregister();

// Re-register with different settings
deleteBinding.Register(bindBeforeExecuted: true);
```

---

## 🔄 Binding Lifecycle

### **1. Startup - Register All Bindings**

```csharp
// Called from SoapApplication.OnStartup()
CommandIntervention.Instance.SetupCommandBinding(document);
    ↓
foreach (command in configuredCommands)
{
    new CommandBindingIntervention(...);
    binding.Register();
}
```

### **2. Project Change - Revise Bindings**

```csharp
// Called when switching documents
CommandIntervention.Instance.ReviseCommandBinding(document);
    ↓
UnRegisterExistingCommands(); // Remove old bindings
    ↓
SetupCommandBinding(document); // Create new bindings for this project
```

### **3. Shutdown - Cleanup**

```csharp
// Called from SoapApplication.OnShutdown()
foreach (var binding in CommandBinding_MasterPostableCommands.Values)
{
    binding.Unregister(); // Detach event handlers
}
CommandBinding_MasterPostableCommands.Clear();
```

---

## 💡 Proof: Bindings Don't Replace Commands

**Evidence from code:**

1. **Line 1978:** Uses `LookupPostableCommandId()` - looks up **existing** command
   ```csharp
   RevitCommandId.LookupPostableCommandId(PostableCommand.Delete)
   // Returns Revit's EXISTING Delete command, doesn't create new one
   ```

2. **Line 1983:** Uses `CreateAddInCommandBinding()` - **attaches to** existing command
   ```csharp
   this.binding = this.App.CreateAddInCommandBinding(this.commandId);
   // Creates BINDING (hook), not a new command
   ```

3. **Line 1995:** Uses `+=` operator - **adds** handler, doesn't replace
   ```csharp
   this.binding.BeforeExecuted += this.Binding_BeforeExecuted;
   // ADDS our handler to the event, doesn't replace Revit's logic
   ```

4. **Line 2615:** Uses `e.Cancel = true` - **blocks** command conditionally
   ```csharp
   if (!shouldProceed && e.Cancellable)
       e.Cancel = true;  // Tells Revit "don't run", doesn't replace logic
   ```

**If the legacy product replaced commands:**
- Would use `RevitCommandId.Create()` - create new command ❌
- Would implement `IExternalCommand` - custom command logic ❌
- Would replace ribbon buttons with custom buttons ❌
- Would need to reimplement all Delete logic ❌

**None of this happens!** The legacy product simply hooks into existing commands.

---

## 📋 Summary

**Command binding process:**

1. **Lookup** existing Revit command → `LookupPostableCommandId()`
2. **Create binding** to that command → `CreateAddInCommandBinding()`
3. **Attach handlers** to events → `binding.BeforeExecuted +=`
4. **Command runs:** BeforeExecuted → Revit's Original Logic → Executed
5. **the legacy product decides:** Set `e.Cancel = true` to block, or leave default to allow

**Key insight:** The legacy product is a **middleware layer** between user action and Revit's command execution, not a command replacement.
