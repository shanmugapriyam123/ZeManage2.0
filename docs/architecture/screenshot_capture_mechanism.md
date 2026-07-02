# The legacy product Screenshot Capture Mechanism

## Executive Summary

the legacy product captures **before** and **after** screenshots during command execution using Win32 `PrintWindow` API. The capture timing and behavior varies by protection mode (**Monitor**, **Guide**, **Prevent**).

---

## 📸 Core Screenshot Implementation

### **Screenshot Capture Method**

```csharp
// Location: Soap\Utils\UIUtils.cs Line 354
public static byte[] captureScreenshot_new()
{
    // Get Revit main window handle
    IntPtr mainWindowHandle = RevitParentWindow.GetRevitMainWindowHandle();
    
    // Get window dimensions
    Rect lpRect;
    Functions.GetWindowRect(mainWindowHandle, out lpRect);
    
    // Create bitmap matching window size
    Bitmap bitmap = new Bitmap(
        lpRect.Right - lpRect.Left, 
        lpRect.Bottom - lpRect.Top
    );
    
    try
    {
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            IntPtr hdc = graphics.GetHdc();
            
            // 🎯 Capture window to HDC using Win32
            Functions.PrintWindow(mainWindowHandle, hdc, 0);
            
            graphics.ReleaseHdc(hdc);
        }
        
        // Convert to PNG bytes
        using (MemoryStream memoryStream = new MemoryStream())
        {
            bitmap.Save(memoryStream, ImageFormat.Png);
            return memoryStream.ToArray();
        }
    }
    finally
    {
        bitmap.Dispose();
    }
}
```

**Key Points:**
- ✅ Uses Win32 `PrintWindow` API (not screen capture)
- ✅ Captures **entire Revit window** (not just active view)
- ✅ Returns PNG image as byte array
- ✅ Converts to Base64 string for API transmission

---

## 🔄 Screenshot Capture Flow

### **High-Level Flow**

```
User Action (e.g., Delete command)
         ↓
BeforeExecuted Event
         ↓
CaptureBeforeData() → Captures BEFORE screenshot
         ↓
[Mode-specific intervention: Monitor/Guide/Prevent]
         ↓
Command Executes (or Cancelled)
         ↓
DocumentChanged Event
         ↓
CaptureAfterData() → Captures AFTER screenshot
         ↓
Upload to Server via API
```

---

## 🎨 Mode-Specific Behavior

### **1️⃣ MONITOR Mode**

**Purpose:** Passive tracking without user intervention

```
BeforeExecuted Handler
  ↓
CaptureBeforeData(document, commandSettings)
  │
  ├─ UserEventData.BeforeSelectedElementInfoList = GetSelection()
  ├─ UserEventData.BeforeImageBytes = TakeScreenshot() ✅
  └─ Store in memory
  ↓
LogToUserEventAndSendEmail() // No dialog shown
  ↓
WatchFor(document, commandSettings) // Set watch flag
  ↓
Command executes normally (no cancellation)
  ↓
DocumentChanged Event
  ↓
CaptureAfterData(document, activeCommand, userEventData)
  │
  ├─ UserEventData.AfterSelectedElementInfoList = GetSelection()
  ├─ UserEventData.AfterImageBytes = TakeScreenshot() ✅
  └─ Store in memory
  ↓
LogCompletedCommand()
  ↓
Upload UserEventData to API (async)
  │
  ├─ BeforeImageBytes (Base64)
  ├─ AfterImageBytes (Base64)
  ├─ BeforeSelectedElementInfoList
  ├─ AfterSelectedElementInfoList
  └─ Metadata (user, command, timestamp)
  ↓
CleanupBeforeAndAfterScreenshotData()
  ↓
UserEventData.BeforeImageBytes = null
UserEventData.AfterImageBytes = null
```

**Key Characteristics:**
- ✅ Screenshots captured for **audit trail**
- ✅ **No user dialog** shown
- ✅ Command **never cancelled**
- ✅ Before & After screenshots always captured (if enabled in config)

---

### **2️⃣ GUIDE Mode**

**Purpose:** Show informational dialog, allow user to proceed or cancel

```
BeforeExecuted Handler
  ↓
CaptureBeforeData(document, commandSettings)
  │
  ├─ UserEventData.BeforeSelectedElementInfoList = GetSelection()
  ├─ UserEventData.BeforeImageBytes = TakeScreenshot() ✅
  └─ Store in memory
  ↓
Show the legacy productInterventionView Dialog
  │
  ├─ Display rule message
  ├─ Show before screenshot (optional)
  ├─ Request user comment (if required)
  └─ Buttons: [Proceed] [Cancel]
  ↓
User Clicks "Proceed"?
  ├─ YES → WatchFor() → Command executes
  └─ NO  → LogCancelledCommand() → e.Cancel = true
  ↓
[If Proceeded]
DocumentChanged Event
  ↓
CaptureAfterData(document, activeCommand, userEventData)
  │
  ├─ UserEventData.AfterSelectedElementInfoList = GetSelection()
  ├─ UserEventData.AfterImageBytes = TakeScreenshot() ✅
  └─ Store in memory
  ↓
LogCompletedCommand()
  ↓
Upload UserEventData to API
  ↓
CleanupBeforeAndAfterScreenshotData()
```

**Key Characteristics:**
- ✅ Screenshots captured for **evidence**
- ✅ **Dialog shown** with guidance message
- ✅ User **can proceed or cancel**
- ✅ If cancelled: Only before screenshot captured
- ✅ If proceeded: Both before & after screenshots captured

---

### **3️⃣ PREVENT Mode**

**Purpose:** Block command execution, force cancellation

```
BeforeExecuted Handler
  ↓
CaptureBeforeData(document, commandSettings)
  │
  ├─ UserEventData.BeforeSelectedElementInfoList = GetSelection()
  ├─ UserEventData.BeforeImageBytes = TakeScreenshot() ✅
  └─ Store in memory
  ↓
Show the legacy productInterventionView Dialog (blocking)
  │
  ├─ Display rule message (stern warning)
  ├─ Show before screenshot
  ├─ Request mandatory comment
  └─ Buttons: [OK] (cannot proceed)
  ↓
User Clicks "OK"
  ↓
LogCancelledCommand()
  ↓
e.Cancel = true ❌ // Command blocked
  ↓
Upload UserEventData to API
  │
  ├─ BeforeImageBytes (Base64) ✅
  ├─ AfterImageBytes = null ❌ (command never executed)
  ├─ BeforeSelectedElementInfoList
  ├─ Status: "Cancelled"
  └─ User comment (if provided)
  ↓
CleanupBeforeAndAfterScreenshotData()
  ↓
Command does NOT execute
```

**Key Characteristics:**
- ✅ **Only before screenshot** captured (command never executes)
- ✅ **Dialog shown** as warning
- ✅ Command **always cancelled** (`e.Cancel = true`)
- ❌ **No after screenshot** (nothing changed)
- ✅ User comment typically required for audit trail

---

## 🔧 Configuration Flags

Screenshots are controlled by **per-command configuration**:

```csharp
// Location: SessionLightCacheSync.Instance.BasicCache

// Before screenshot enabled?
bool captureBeforeScreenshot = 
    basicCache.GetCaptureBeforeScreenshot(commandSettings);

// After screenshot enabled?
bool captureAfterScreenshot = 
    basicCache.GetCaptureAfterScreenshot(commandSettings);

// Before selection enabled?
bool captureBeforeSelection = 
    basicCache.GetCaptureBeforeSelectedElement(commandSettings);

// After selection enabled?
bool captureAfterSelection = 
    basicCache.GetCaptureAfterSelectedElement(commandSettings);
```

**Global Screenshot Setting:**

```csharp
// Location: UserInteractionSettings

// Master switch for screenshots
bool allowScreenshots = 
    applicableUserInteractionSettings?.AllowScreenshots ?? false;

// If false, TakeScreenshotIfEnabled() returns null
```

---

## 📊 Comparison Matrix

| Aspect | Monitor | Guide | Prevent |
|--------|---------|-------|---------|
| **Before Screenshot** | ✅ Always | ✅ Always | ✅ Always |
| **After Screenshot** | ✅ Always | ✅ If proceeded | ❌ Never |
| **User Dialog** | ❌ No | ✅ Yes (optional) | ✅ Yes (forced) |
| **Command Execution** | ✅ Always | ✅ If user approves | ❌ Never |
| **Cancel Event** | ❌ No | ⚠️ If user cancels | ✅ Always |
| **Upload Timing** | After execution | After execution | Immediately |
| **User Comment** | ❌ No | ⚠️ Optional | ✅ Usually required |

---

## 🎯 Code Trace Example: Delete Command in PREVENT Mode

### **Step-by-Step Execution**

**1. User selects walls and presses Delete**

**2. BeforeExecuted Event Fires**
```csharp
// CommandIntervention.cs Line 2550
private void Binding_BeforeExecuted(...)
{
    // Get selected elements
    IEnumerable<Element> elements = GetSelectedElements(activeDocument);
    
    // Evaluate delete protection rules
    bool shouldProceed = PresentSpecialCommand_AdditionalOccurance_RuleEvaluation(
        "32778", // Delete command
        activeDocument,
        NestedConditionFeatureSetTypes.DeleteProtection,
        orderedRulesList,
        ...
    );
    
    if (!shouldProceed) // Rule matched!
    {
        e.Cancel = true; // Block the delete
        LogCancelledCommand();
    }
}
```

**3. Inside Rule Evaluation - Capture Before Data**
```csharp
// CommandMonitor.cs Line 2600
CommandMonitor.Instance.CaptureBeforeData(
    activeDocument, 
    commandSettings
);

// CommandMonitor.cs Line 53
public void CaptureBeforeData(...)
{
    // Create user event data
    UserEventData = RequestOptionsLogUserEventWithAdditionalInfo.Create_Command(document);
    
    // Capture selected elements
    UserEventData.BeforeSelectedElementInfoList = 
        uiDocument.Selection.GetElementIds()
                  .Select(x => ElementInfo.Create(document, x))
                  .ToList();
    
    // 📸 Capture BEFORE screenshot
    if (basicCache.GetCaptureBeforeScreenshot(command))
    {
        UserEventData.BeforeImageBytes = 
            UIUtils.TakeScreenshotIfEnabled(document, interactionSettings);
    }
}
```

**4. TakeScreenshotIfEnabled**
```csharp
// UIUtils.cs Line 264
public static string TakeScreenshotIfEnabled(...)
{
    // Check if screenshots globally enabled
    if (applicableUserInteractionSettings?.AllowScreenshots ?? false)
    {
        return TakeScreenshotConvertToBase64String(document);
    }
    return null;
}

// UIUtils.cs Line 271
public static string TakeScreenshotConvertToBase64String(...)
{
    byte[] imageBytes = TakeScreenshot(document);
    return Convert.ToBase64String(imageBytes); // PNG → Base64
}

// UIUtils.cs Line 283
public static byte[] TakeScreenshot(Document document)
{
    // Get Revit drawing area
    Rectangle drawingArea = new UIDocument(document)
                                .Application
                                .DrawingAreaExtents;
    
    return TakeScreenshot(drawingArea);
}

// UIUtils.cs Line 354
public static byte[] captureScreenshot_new()
{
    IntPtr mainWindowHandle = RevitParentWindow.GetRevitMainWindowHandle();
    
    // Get window bounds
    Rect rect;
    Functions.GetWindowRect(mainWindowHandle, out rect);
    
    // Create bitmap
    Bitmap bitmap = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
    
    using (Graphics graphics = Graphics.FromImage(bitmap))
    {
        IntPtr hdc = graphics.GetHdc();
        
        // 🎯 WIN32 API: Capture window to device context
        Functions.PrintWindow(mainWindowHandle, hdc, 0);
        
        graphics.ReleaseHdc(hdc);
    }
    
    // Convert to PNG bytes
    using (MemoryStream ms = new MemoryStream())
    {
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
```

**5. Show Intervention Dialog (PREVENT mode)**
```csharp
// CommandIntervention.cs Line 2635
the legacy productInterventionView interventionView = new the legacy productInterventionView();
the legacy productInterventionViewModel viewModel = 
    the legacy productInterventionViewModel.CreateForMasterPostableCommand(...);

interventionView.DataContext = viewModel;

// Dialog shows:
// - Rule message: "Critical structural elements cannot be deleted"
// - Before screenshot (showing selected walls in red highlight)
// - Required comment field
// - Only [OK] button (no proceed option)

bool? dialogResult = interventionView.ShowDialog();

// User must click OK (cannot proceed with delete)
```

**6. Cancel Command**
```csharp
// CommandIntervention.cs Line 2615
if (!shouldProceed && e.Cancellable)
{
    e.Cancel = true; // ❌ Block the delete command
    
    LogCancelledCommand(commandSettings);
}
```

**7. LogCancelledCommand**
```csharp
// CommandMonitor.cs Line 298
public void LogCancelledCommand(PostableCommandSettings commandSetting)
{
    // Prepare user event data
    UserEventData.IsCommandCancelled = true;
    UserEventData.Status = "Cancelled";
    UserEventData.DialogValue = "Prevent";
    
    // 📤 Upload to server immediately
    Task.Run(() => 
        SoapAPIAppHelper.Instance.LogUserEventAsync(UserEventData)
    );
    
    // UserEventData contains:
    // - BeforeImageBytes: "iVBORw0KGgoAAAANS..." (Base64 PNG)
    // - AfterImageBytes: null (command cancelled)
    // - BeforeSelectedElementInfoList: [Wall id 123, Wall id 456]
    // - UserComment: "Accidentally selected load-bearing walls"
    // - RuleName: "Protect Structural Elements"
    // - Status: "Cancelled"
}
```

**8. Cleanup**
```csharp
// CommandMonitor.cs Line 446
public void CleanupBeforeAndAfterScreenshotData()
{
    // Free memory (images already uploaded)
    UserEventData.BeforeImageBytes = null;
    UserEventData.AfterImageBytes = null;
}
```

**9. Delete Command Blocked**
- Walls remain in the model (not deleted)
- The legacy product logs event with before screenshot
- BIM manager can review on web dashboard

---

## 🔑 Key Implementation Details

### **1. Screenshot Timing**

| Event | When Captured | What's Visible |
|-------|--------------|----------------|
| **Before** | `BeforeExecuted` handler | Model state BEFORE command runs |
| **After** | `DocumentChanged` event | Model state AFTER command completes |

### **2. Thread Safety**

Screenshots are captured on the **Revit UI thread** (synchronously):
- `BeforeExecuted` runs on UI thread → Safe
- `DocumentChanged` runs on UI thread → Safe
- Win32 `PrintWindow` requires window handle → Must be UI thread

### **3. Performance Considerations**

```csharp
// Screenshot is BLOCKING
byte[] screenshot = TakeScreenshot(document); // ~50-200ms

// Upload is ASYNC (non-blocking)
Task.Run(() => SoapAPIAppHelper.Instance.LogUserEventAsync(data));
```

- **Capture time:** 50-200ms (depends on screen resolution)
- **PNG compression:** Built-in .NET (fast)
- **Base64 encoding:** Negligible (<5ms)
- **Upload:** Async (doesn't block Revit UI)

### **4. Screenshot Scope**

the legacy product captures the **entire Revit window**, including:
- ✅ Canvas (3D/2D views)
- ✅ Ribbon
- ✅ Project Browser
- ✅ Properties panel
- ❌ Other windows (dialogs, external apps)

**Alternative approach (not used):** Could capture only active view using `UIDocument.Application.DrawingAreaExtents`, but the legacy product captures full window for context.

---

## 💡 Recommendations for CBOX Manage

### **Should You Replicate This?**

**✅ Replicate:**
1. **Win32 PrintWindow API** - Reliable, non-invasive
2. **Before/After capture timing** - Proven pattern
3. **Mode-specific behavior** - Clear separation of concerns
4. **Async upload** - Don't block Revit UI
5. **Base64 encoding** - Web-compatible format

**⚠️ Consider Improving:**
1. **Capture only active view** - Smaller file sizes
2. **JPEG instead of PNG** - 5-10x smaller (acceptable quality loss)
3. **Client-side compression** - Reduce upload bandwidth
4. **Local SQLite storage** - Offline resilience (hybrid approach)
5. **Lazy upload queue** - Batch uploads, retry on failure

### **Hybrid Approach for CBOX Manage**

```
Screenshot Captured
         ↓
Save to SQLite (BLOB)
         ↓
         ├─ If Online → Upload to server + mark uploaded
         └─ If Offline → Queue for later upload
         ↓
After 7 days → Delete local copy (keep server copy)
```

**Benefits:**
- ✅ Works offline
- ✅ Evidence preserved locally
- ✅ Upload queue with retry
- ✅ Reduced server dependency

---

## 📝 Summary

**the legacy product's screenshot mechanism:**

1. **Capture Method:** Win32 `PrintWindow` API
2. **Format:** PNG → Base64 string
3. **Timing:** Before in `BeforeExecuted`, After in `DocumentChanged`
4. **Storage:** Temporary in-memory, uploaded to API immediately
5. **Scope:** Full Revit window
6. **Configuration:** Per-command flags + global enable/disable

**Mode Behavior:**
- **Monitor:** Both screenshots, no dialog, always execute
- **Guide:** Both screenshots (if approved), optional dialog, user decides
- **Prevent:** Only before screenshot, mandatory dialog, never execute

**For CBOX Manage:** Use similar pattern but add SQLite persistence for offline capability and evidence retention compliance.
