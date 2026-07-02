# Command Interception Refactoring - command interception pattern

## Objective
Refactor CBOX Manage's command interception to exactly replicate the legacy product's proven architecture.

## Tasks

### Phase 1: Core Architecture Setup
- [ ] Create `CommandBindingIntervention` nested class
  - [ ] Constructor overloads for PostableCommand and string IDs
  - [ ] `Register()` method (Executed event)
  - [ ] `RegisterWithBeforeExecute()` method (BeforeExecuted event)
  - [ ] `Unregister()` method
  - [ ] `GetRevitCommandId()` helper
  - [ ] `IsRegistered()` check method

### Phase 2: Command Manager Refactoring
- [ ] Refactor `CommandInterceptionService` to the legacy product pattern
  - [ ] Add private fields for individual command bindings
  - [ ] Implement `SetupCommandBinding()` method
  - [ ] Implement `UnRegisterExistingCommands()` method
  - [ ] Implement command-specific setup methods

### Phase 3: Basic Commands Implementation
- [ ] Implement Pin command interception
  - [ ] Create `CommandBinding_Pin` field
  - [ ] Set up registration with `Register()`
  - [ ] Create `ProcessPinCommand()` handler
- [ ] Implement Unpin command interception
  - [ ] Create `CommandBinding_Unpin` field
  - [ ] Set up with custom CompletionValidator
  - [ ] Create `ProcessUnpinCommand()` handler
- [ ] Implement Move command interception
  - [ ] Create `CommandBinding_Move` field
  - [ ] Set up registration with `RegisterWithBeforeExecute()`
  - [ ] Create `ProcessMoveCommand()` handler

### Phase 4: Mirror & Copy Commands
- [ ] Implement Mirror commands
  - [ ] `CommandBinding_MirrorDrawAxis`
  - [ ] `CommandBinding_MirrorPickAxis`
  - [ ] Event handlers
- [ ] Implement Copy/Array commands
  - [ ] `CommandBinding_CopyInPlaceFamily`
  - [ ] `CommandBinding_CopyToClipboardInPlaceFamily`
  - [ ] `CommandBinding_ArrayInPlaceFamily`

### Phase 5: Special Commands (Separate Classes)
- [ ] Create `DuplicateCommandOverride` class
  - [ ] Singleton pattern
  - [ ] String ID registration ("ID_SYM_CLONE")
  - [ ] BeforeExecuted handler
- [ ] Create `RenameCommandOverride` class
  - [ ] Singleton pattern
  - [ ] String ID registration ("ID_PRJBROWSER_RENAME")
  - [ ] BeforeExecuted handler

### Phase 6: Delete Commands
- [ ] Implement Delete command binding
- [ ] Implement ProjectBrowserDelete
- [ ] Implement ScheduleRowDelete
- [ ] Create `SetCommandBindingForDeleteWithDependents()`

### Phase 7: Integration & Cleanup
- [ ] Update document event handlers to call new setup methods
- [ ] Remove old centralized registration code
- [ ] Update rule evaluation to work with new architecture
- [ ] Remove `IsEditModeCommand()` guard (no longer needed)

### Phase 8: Testing & Validation
- [ ] Test each command individually
- [ ] Verify BeforeExecuted cancellation works
- [ ] Verify Executed event tracking works
- [ ] Test with RBAC rules
- [ ] Performance testing
