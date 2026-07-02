# ADR-001: Hybrid Command Binding Pattern

## Status
**ACCEPTED** - 2026-01-14

## Context
BIManageRevit needs to intercept Revit commands for rule-based protection (Monitor/Guide/Prevent modes). the legacy product (a reference implementation) uses individual `CommandBindingIntervention` instances for every command. However, our implementation faced a choice:

1. **Full command interception pattern** - Individual bindings for all 30+ commands
2. **Centralized Pattern** - Single event handler for all commands (original approach)
3. **Hybrid Pattern** - Individual bindings for critical commands, centralized for others

### The Problem
Move/Rotate/Mirror commands were not working because:
- Centralized loop had guard clause (`IsEditModeCommand`) that blocked these commands
- These commands need `BeforeExecuted` event to cancel before user interaction begins
- Generic handler couldn't distinguish between true edit-mode commands (ID_EDIT_PROFILE) and modification commands (ID_EDIT_MOVE)

### the legacy product's Approach
the legacy product creates individual `CommandBindingIntervention` for each command type:
```csharp
// the legacy product pattern
private CommandBindingIntervention CommandBinding_Move;
private CommandBindingIntervention CommandBinding_Rotate;
private CommandBindingIntervention CommandBinding_Mirror;
private CommandBindingIntervention CommandBinding_Copy;
private CommandBindingIntervention CommandBinding_Delete;
// ... 20+ more fields
```

Each command gets dedicated setup logic, event handlers, and configuration.

## Decision
We chose a **Hybrid Pattern** that combines both approaches:

### Individual Bindings (command interception pattern)
**For commands requiring specialized handling:**
- Move, Rotate, Mirror (2 variants)
- Future: Copy, Array, Delete (if complex validation needed)

**Implementation:**
```csharp
// Separate binding class per command type
public class MoveCommandBinding : CommandBindingBase
{
    private readonly IRuleCommandInterceptor _ruleInterceptor;

    public override void RegisterWithBeforeExecute()
    {
        _binding = UIApp.CreateAddInCommandBinding(CommandId);
        _binding.BeforeExecuted += OnBeforeExecuted;
    }

    private void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e)
    {
        // NO guard clauses - direct delegation
        _ruleInterceptor?.OnBeforeExecuted(sender, e);
    }
}
```

### Centralized Handler (Original Pattern)
**For standard commands:**
- Pin/Unpin
- Delete
- Sync
- Other generic commands (30+ commands)

**Implementation:**
```csharp
// Single handler in CommandInterceptionService
private void OnBeforeCommandExecuted(object sender, BeforeExecutedEventArgs e)
{
    if (IsEditModeCommand(commandId))
    {
        // Skip TRUE edit mode commands (ID_EDIT_PROFILE, etc.)
        return;
    }

    // Delegate to rule interceptor
    _ruleInterceptor?.OnBeforeExecuted(sender, e);
}
```

### Deduplication Logic
```csharp
private void RegisterCommandBindings(UIApplication uiApp)
{
    // 1. Register individual bindings FIRST
    RegisterIndividualBindings(uiApp);

    // 2. Get IDs of individually bound commands
    var skipIds = GetIndividuallyBoundCommandIds(uiApp);

    // 3. Register remaining commands via centralized loop
    foreach (var commandId in coreCommands)
    {
        if (skipIds.Contains((int)commandId.Id))
            continue; // Skip duplicates

        // Centralized registration...
    }
}
```

## Consequences

### Positive
✅ **Pragmatic** - Individual bindings only where truly needed (Move/Rotate/Mirror)
✅ **Maintainable** - Separate classes easier to understand than nested classes
✅ **Testable** - Each binding class can be unit tested independently
✅ **Extensible** - Can incrementally add individual bindings as requirements emerge
✅ **Performance** - No overhead for commands that don't need special handling
✅ **Type-safe** - Separate classes prevent misuse (can't accidentally use wrong constructor)
✅ **Dependency Injection** - Rule interceptor injected, not statically accessed

### Negative
⚠️ **Inconsistent** - Some commands use individual pattern, others centralized
⚠️ **More files** - 4 binding classes vs the legacy product's single nested class
⚠️ **Mixed mental model** - Developers must understand both patterns

### Neutral
🟡 **Different from the legacy product** - Not a 1:1 port, but achieves same goals
🟡 **Less configuration** - No PostableCommandSettings/InterventionSettings (simpler but less flexible)
🟡 **No per-document setup** - Global bindings for entire session (the legacy product has per-document)

## Alternatives Considered

### Alternative 1: Full command interception pattern
**Pros:**
- Perfect 1:1 match with reference implementation
- Every command has individual binding
- Consistent architecture throughout

**Cons:**
- 40-60 hours effort to implement
- 30+ individual binding fields in manager
- Nested class with 3 constructor overloads
- Overkill for commands that don't need special handling

**Verdict:** ❌ Rejected - Over-engineering for MVP

### Alternative 2: Pure Centralized Pattern
**Pros:**
- Simplest approach (single event handler)
- Minimal code duplication

**Cons:**
- ❌ Cannot distinguish edit-mode commands from modification commands
- ❌ Guard clause blocks valid commands (Move/Rotate)
- ❌ All commands must share same event handling logic

**Verdict:** ❌ Rejected - Doesn't solve the Move/Rotate problem

### Alternative 3: Hybrid Pattern (CHOSEN)
**Pros:**
- ✅ Solves immediate problem (Move/Rotate/Mirror working)
- ✅ Keeps simple commands simple (centralized)
- ✅ Allows complex commands to be specialized
- ✅ Incrementally adoptable (can add more individual bindings later)

**Cons:**
- ⚠️ Mixed patterns in same codebase
- ⚠️ Developers must know when to use each approach

**Verdict:** ✅ ACCEPTED - Best balance for MVP

## Decision Criteria

### When to Use Individual Binding
Create a dedicated binding class if **ANY** of these conditions apply:

1. **Pre-execution cancellation required** - Command needs BeforeExecuted to cancel before user interaction
   - Example: Move, Rotate, Mirror

2. **Complex validation logic** - Command needs multi-step validation that's hard to express in centralized handler
   - Example: Copy with "PromptUserForCopyInPlaceFamily" setting

3. **Command-specific state tracking** - Need to track state between BeforeExecuted and Executed
   - Example: Duplicate + Rename coordination (the legacy product pattern)

4. **Special event orchestration** - Command needs to coordinate with other commands
   - Example: DuplicateCommandOverride sets `SkipDuplicateCheck` for RenameCommandOverride

### When to Use Centralized Handler
Use centralized handler if **ALL** of these apply:

1. ✅ Post-execution tracking only (Executed event is sufficient)
2. ✅ Generic rule evaluation (no command-specific logic)
3. ✅ No cross-command coordination
4. ✅ No special state tracking

**Examples:** Pin, Unpin, Delete, Sync, most standard commands

## Implementation Guide

### Adding a New Individual Binding

**Step 1:** Create binding class
```csharp
// BIManage/Revit/Commands/Bindings/CopyCommandBinding.cs
public class CopyCommandBinding : CommandBindingBase
{
    private AddInCommandBinding _binding;
    private readonly IRuleCommandInterceptor _ruleInterceptor;

    public CopyCommandBinding(
        UIApplication uiApp,
        ILogger logger,
        IRuleCommandInterceptor ruleInterceptor)
        : base(uiApp, logger)
    {
        _ruleInterceptor = ruleInterceptor;
        CommandId = RevitCommandId.LookupPostableCommandId(PostableCommand.Copy);
    }

    public override void RegisterWithBeforeExecute()
    {
        if (!CanRegister())
            return;

        _binding = UIApp.CreateAddInCommandBinding(CommandId);
        _binding.BeforeExecuted += OnBeforeExecuted;
        Logger?.LogInfo($"Copy binding registered: {CommandId.Name}");
    }

    private void OnBeforeExecuted(object sender, BeforeExecutedEventArgs e)
    {
        Logger?.LogDebug($"Copy intercepted: {e.CommandId.Name}");
        _ruleInterceptor?.OnBeforeExecuted(sender, e);
    }

    public override void Register()
    {
        throw new NotImplementedException("Copy uses BeforeExecuted. Call RegisterWithBeforeExecute().");
    }

    public override void Unregister()
    {
        if (_binding != null)
        {
            _binding.BeforeExecuted -= OnBeforeExecuted;
        }
    }
}
```

**Step 2:** Add field to CommandInterceptionService
```csharp
private CopyCommandBinding? _copyBinding;
```

**Step 3:** Register in RegisterIndividualBindings()
```csharp
_copyBinding = new CopyCommandBinding(uiApplication, _logger, _ruleInterceptor);
_copyBinding.RegisterWithBeforeExecute();
```

**Step 4:** Add to skip list in GetIndividuallyBoundCommandIds()
```csharp
var copyId = RevitCommandId.LookupPostableCommandId(PostableCommand.Copy);
if (copyId != null) ids.Add((int)copyId.Id);
```

**Step 5:** Unregister in UnregisterCommandBindings()
```csharp
_copyBinding?.Unregister();
```

## Review Schedule
This decision should be reviewed in **Phase 2 (post-MVP)** based on:

1. **Usage patterns** - Which commands are frequently used? Do they need individual bindings?
2. **Bug reports** - Are we getting issues with centralized handler?
3. **Performance** - Is centralized handler a bottleneck?
4. **Maintainability** - Are developers confused by hybrid pattern?

## References
- the legacy product implementation: `BIManage/Common/Helpers/command_interception_implementation_guide.md`
- Full implementation plan: `BIManage/Common/Helpers/implementation_plan.md`
- Closeout analysis: `BIManage/Common/Helpers/command_interception_closeout_analysis.md`

## Authors
- Claude Sonnet 4.5 (Implementation)
- User (Requirements and validation)
