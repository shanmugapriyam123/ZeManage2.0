# BIManage Background Sync Architecture: Non-Interruptive Pattern
How BIManage performs heavy document operations (Sync, Save, Relinquish) without disrupting active users.

---

## 1. The "Safety Valve": Revit's Idling Event
The most fundamental technique is the use of the **`Idling` event**. 
- **Built-in Protection**: By Revit API design, the `Idling` event **only fires** when Revit is "quiescent" — meaning NO commands are active, NO dialog boxes are open, and NO mouse clicks are currently being processed.
- **Result**: The background sync never has to worry about interrupting a user in the middle of drawing a wall; Revit simply won't let the sync code run until the user has finished their action.

## 2. Idle Tracking (Win32 API)
Even if Revit is technically idle (no command active), a user might be thinking or navigating. The background sync uses the Windows API to detect genuine inactivity.

```csharp
[DllImport("user32.dll")]
private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
```

- **Mechanism**: Every 1 second, a background thread checks the time since the last mouse/keyboard input.
- **Threshold**: The engine waits until the user has been system-level idle for the duration specified in the settings (e.g., 15 minutes).
- **Foreground Smart Check**: It uses `GetForegroundWindow` to check if Revit is even the active application. If the user is currently in a different app (e.g., Outlook or Chrome), the engine considers this a safe "Pause" time to execute.

## 3. The "Silent Poke" (WM_NULL)
Normally, the `Idling` event fires less frequently when the user isn't moving their mouse. To ensure sync happens exactly when needed, the engine uses a "Silent Poke":

```csharp
Application.PostMessage(revitWindowHandle, 0U, 0U, 0U); // 0U = WM_NULL
```

- **Why**: Sending `WM_NULL` to Revit's message queue tells Windows to wake up the Revit process. 
- **The Trick**: Unlike `SetFocus` or other pokes, `WM_NULL` does **not** bring Revit to the front or steal the cursor. It silently triggers an `Idling` event within Revit, allowing the background sync logic to start without the user ever seeing a window flicker.

## 4. Dialog Suppression (FailuresProcessing)
A major risk of background sync is a Revit Warning popup (e.g., "Elements have been deleted in central"). This would block Revit and force the user to click "OK". The engine bypasses this by "wrapping" the sync in a failure handler:

```csharp
// 1. Hook the failure processor just before sync
uiApp.Application.FailuresProcessing += Application.OnFailuresProcessing;

try {
    // 2. Execute the sync
    doc.SynchronizeWithCentral(...);
} finally {
    // 3. Unhook immediately after
    uiApp.Application.FailuresProcessing -= Application.OnFailuresProcessing;
}
```

**Inside `OnFailuresProcessing`**:
The handler automatically sets the `ProcessingResult` to `Continue` or `Discard`. This tells Revit to "ignore/resolve the warning automatically" because an automated process is running. This effectively **silences all Revit popups** during the background operation.

## 5. Non-Modal Feedback (Status Bar)
Instead of using `TaskDialog.Show()` (which is modal and stops everything), the engine uses a custom helper to write to the Revit Status bar (bottom left corner):

```csharp
public static void SetStatusText(IntPtr mainWindow, string text) {
    // Uses FindWindowEx to find the Revit Status Bar element
    // and sends WM_SETTEXT to update it
}
```
This provides "passive" feedback—a user glances down and sees "Background Synchronizing..." but their workflow is never interrupted by a popup.

## 6. Document State Validation
Before starting a sync, the engine performs two final "Are you busy?" checks:
1. **`IsBackgroundCalculationInProgress()`**: Ensures Revit isn't currently crunching numbers for color fills or background views.
2. **`IsModified`**: If the doc hasn't changed (and the settings say skip if no changes), it exits immediately to save resources.

## Summary Table: Foreground Protection

| Potential Disruption | How the Background Sync Prevents It |
|----------------------|-------------------------------------|
| Interrupted Command  | Only runs during `Idling` (no active command allowed) |
| Modal Popup/Error    | Uses `FailuresProcessing` to auto-resolve warnings |
| Focus Stealing       | Wakes Revit with `WM_NULL` (silent) instead of focus |
| Performance Lag      | Checks `IsBackgroundCalculationInProgress()` before start |
| Visual Disruption    | Passive Status Bar updates instead of Dialogs |
| Multiple Syncs       | Uses `ruleinuse` flag to prevent concurrent runs |
