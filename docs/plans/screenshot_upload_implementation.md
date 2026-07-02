# the legacy product Screenshot Upload to Central Server - Complete Implementation Guide

## Executive Summary

the legacy product captures screenshots of the Revit window and uploads them to the central server as **Base64-encoded strings** embedded in **JSON payloads** via HTTP POST requests.

**Key Components:**
1. **Screenshot Capture:** `UIUtils.TakeScreenshot()` - Uses Windows `PrintWindow` API
2. **Encoding:** Convert PNG bytes to Base64 string
3. **Transport:** Embed in `RequestOptionsLogUserEventWithAdditionalInfo` JSON model
4. **Upload:** HTTP POST to `api/Analytics/LogUserEvent3/{licenseGuid}/{projectGuid}`

---

## Screenshot Capture Mechanism

### Step 1: Capture Screenshot

the legacy product uses the Windows **PrintWindow** API to capture the Revit window content as a bitmap image.

**Implementation: `UIUtils.TakeScreenshot()`**

```csharp
// File: UIUtils.cs
public static byte[] TakeScreenshot(Document document)
{
    if (document == null) return null;
    
    // Get drawing area extents from Revit UI
    Rectangle drawingAreaExtents = new UIDocument(document).Application.DrawingAreaExtents;
    
    return TakeScreenshot(drawingAreaExtents);
}

public static byte[] TakeScreenshot(Rectangle rectangle)
{
    try
    {
        return captureScreenshot_new();
    }
    catch
    {
        return null;
    }
}

public static byte[] captureScreenshot_new()
{
    // Get Revit main window handle
    IntPtr mainWindowHandle = RevitParentWindow.GetRevitMainWindowHandle();
    
    // Get window rectangle
    Soap.Windows.Win32.Rect lpRect;
    Functions.GetWindowRect(mainWindowHandle, out lpRect);
    
    // Calculate dimensions
    int width = lpRect.Right - lpRect.Left;
    int height = lpRect.Bottom - lpRect.Top;
    
    // Create bitmap
    Bitmap bitmap = new Bitmap(width, height);
    
    try
    {
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            // Get device context
            IntPtr hdc = graphics.GetHdc();
            
            // ✅ CRITICAL: Use PrintWindow to capture window content
            // This works even when window is partially obscured
            Functions.PrintWindow(mainWindowHandle, hdc, 0);
            
            graphics.ReleaseHdc(hdc);
        }
        
        // Convert bitmap to PNG bytes
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

**Windows API Interop:**

```csharp
// Windows.Win32\Functions.cs
public static class Functions
{
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);
    
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, int nFlags);
}

public struct Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}
```

**Key Points:**
- ✅ Uses `PrintWindow` API - Captures window even if partially obscured
- ✅ Captures **drawing area** (canvas) not entire Revit window
- ✅ Returns **PNG bytes** (compressed format)
- ✅ No anti-virus false positives (uses standard Windows API)

---

### Step 2: Convert to Base64 String

the legacy product converts the PNG byte array to a Base64 string for JSON transport.

**Implementation:**

```csharp
public static string TakeScreenshotConvertToBase64String(Document document)
{
    // Capture screenshot as PNG bytes
    byte[] pngBytes = document == null || !document.IsValidObject 
        ? TakeScreenshot()  // Capture main window
        : TakeScreenshot(document);  // Capture drawing area
    
    // Convert to Base64 string
    return pngBytes != null ? Convert.ToBase64String(pngBytes) : null;
}

public static string TakeScreenshotIfEnabled(
    Document document,
    UserInteractionSettings applicableUserInteractionSettings)
{
    // Check if screenshots are enabled for this project
    bool allowScreenshots = applicableUserInteractionSettings?.AllowScreenshots ?? false;
    
    return allowScreenshots 
        ? TakeScreenshotConvertToBase64String(document) 
        : null;
}
```

**Example Base64 Output:**

```
iVBORw0KGgoAAAANSUhEUgAABLAAAASwCAYAAAD... (truncated, ~300KB for 1920x1080)
```

**Size Estimate:**
- **1920x1080 screenshot**: ~300-500 KB (Base64)
- **2560x1440 screenshot**: ~600-800 KB (Base64)
- **PNG compression** reduces size significantly vs raw bitmap

---

### Step 3: Store in User Event Data

the legacy product captures **"Before"** and **"After"** screenshots for each command execution.

**Implementation: `CommandMonitor.cs`**

```csharp
public class CommandMonitor
{
    // Stores event data for current command
    internal RequestOptionsLogUserEventWithAdditionalInfo UserEventData { get; set; }
    
    // Captures Before screenshot when command starts
    public void SwitchToNewActiveCommand(
        Document document,
        string command,
        UserInteractionSettings interactionSettings)
    {
        // Initialize event data
        this.UserEventData = RequestOptionsLogUserEventWithAdditionalInfo.Create_Command(document);
        this.UserEventData.CommandName = command;
        
        // ✅ Capture BEFORE screenshot
        this.UserEventData.BeforeImageBytes = UIUtils.TakeScreenshotIfEnabled(
            document, 
            interactionSettings);
    }
    
    // Captures After screenshot when command completes
    public void CompleteActiveCommand(
        Document document,
        UserInteractionSettings interactionSettings)
    {
        RequestOptionsLogUserEventWithAdditionalInfo userEventData = this.UserEventData;
        
        // ✅ Capture AFTER screenshot
        if (basicCache.GetCaptureAfterScreenshot(activeCommand))
        {
            userEventData.AfterImageBytes = UIUtils.TakeScreenshotIfEnabled(
                document, 
                interactionSettings);
        }
        
        // Upload to server
        AnalyticsStorageHelper.PushToAnalytics(userEventData);
    }
}
```

**Cleanup:**

```csharp
public void CleanupBeforeAndAfterScreenshotData()
{
    // Clear screenshot data to free memory
    this.UserEventData.BeforeImageBytes = null;
    this.UserEventData.AfterImageBytes = null;
}
```

---

## Data Model: RequestOptionsLogUserEventWithAdditionalInfo

the legacy product uses a comprehensive JSON model to log command execution events.

**Model Definition:**

```csharp
public class RequestOptionsLogUserEventWithAdditionalInfo
{
    // Event metadata
    [JsonProperty(PropertyName = "EventCategory")]
    public int? EventCategory { get; set; }  // 100 = Command
    
    [JsonProperty(PropertyName = "EventCaption")]
    public string EventCaption { get; set; }  // "AdminControlledCommandExecuted"
    
    [JsonProperty(PropertyName = "EventDateUtc")]
    public DateTime? EventDateUtc { get; set; }
    
    // User information
    [JsonProperty(PropertyName = "UserName")]
    public string UserName { get; set; }
    
    [JsonProperty(PropertyName = "ProjectName")]
    public string ProjectName { get; set; }
    
    [JsonProperty(PropertyName = "FilePath")]
    public string FilePath { get; set; }
    
    [JsonProperty(PropertyName = "RevitVersion")]
    public string RevitVersion { get; set; }
    
    // Command details
    [JsonProperty(PropertyName = "CommandName")]
    public string CommandName { get; set; }
    
    [JsonProperty(PropertyName = "CmdPrpsCode")]
    public string CmdPrpsCode { get; set; }  // Command properties code
    
    [JsonProperty(PropertyName = "UserActionShortCode")]
    public string UserActionShortCode { get; set; }  // "PROCEEDED", "CANCELLED", etc.
    
    [JsonProperty(PropertyName = "DialogValue")]
    public string DialogValue { get; set; }  // "Monitor", "Guide", "Prevent"
    
    [JsonProperty(PropertyName = "UserComment")]
    public string UserComment { get; set; }
    
    [JsonProperty(PropertyName = "OverrideSource")]
    public string OverrideSource { get; set; }  // "AdminPassword", "UserOverride", etc.
    
    [JsonProperty(PropertyName = "ModeOnOverride")]
    public string ModeOnOverride { get; set; }
    
    // ✅ SCREENSHOTS
    [JsonProperty(PropertyName = "BeforeImageBytes")]
    public string BeforeImageBytes { get; set; }  // Base64 PNG
    
    [JsonProperty(PropertyName = "AfterImageBytes")]
    public string AfterImageBytes { get; set; }  // Base64 PNG
    
    // Element information
    [JsonProperty(PropertyName = "BeforeSelectedElementInfoList")]
    public IList<ElementInfo> BeforeSelectedElementInfoList { get; set; }
    
    [JsonProperty(PropertyName = "AfterSelectedElementInfoList")]
    public IList<ElementInfo> AfterSelectedElementInfoList { get; set; }
    
    [JsonProperty(PropertyName = "AddedElementInfoList")]
    public IList<ElementInfo> AddedElementInfoList { get; set; }
    
    [JsonProperty(PropertyName = "DeletedElementInfoList")]
    public IList<ElementInfo> DeletedElementInfoList { get; set; }
    
    [JsonProperty(PropertyName = "NumberOfAddedElements")]
    public int? NumberOfAddedElements { get; set; }
    
    // Active view
    [JsonProperty(PropertyName = "ActiveViewInfo")]
    public ViewInfo ActiveViewInfo { get; set; }
    
    // Warnings
    [JsonProperty(PropertyName = "WarningInfoList")]
    public IList<WarningInfo> WarningInfoList { get; set; }
    
    // Notification
    [JsonProperty(PropertyName = "IsInstantlyNotify")]
    public bool? IsInstantlyNotify { get; set; }
    
    // Session
    [JsonProperty(PropertyName = "ProjectSessionGuid")]
    public string ProjectSessionGuid { get; set; }
    
    // Sync
    [JsonProperty(PropertyName = "SyncGuid")]
    public string SyncGuid { get; set; }
    
    // Repetition tracking
    [JsonProperty(PropertyName = "IntervalSeqNo")]
    public int? IntervalSeqNo { get; set; }
    
    [JsonProperty(PropertyName = "RepetitionCount")]
    public int? RepetitionCount { get; set; }
    
    // Pin protection
    [JsonProperty(PropertyName = "UnpinnedElements")]
    public string UnpinnedElements { get; set; }
    
    [JsonProperty(PropertyName = "NotifyUnpinElementNames")]
    public string NotifyUnpinElementNames { get; set; }
    
    [JsonProperty(PropertyName = "ProtectedBy")]
    public string ProtectedBy { get; set; }
    
    [JsonProperty(PropertyName = "GroupType")]
    public string GroupType { get; set; }
    
    // Not sent to server (internal tracking)
    [JsonIgnore]
    public string ProjectGuid { get; set; }
}
```

---

## API Upload Endpoint

### Endpoint Specification

**URL:**
```
POST api/Analytics/LogUserEvent3/{licenseGuid}/{projectGuid}
```

**Path Parameters:**
- `licenseGuid` (string) - Company/license identifier
- `projectGuid` (string) - Project/document identifier

**Request Headers:**
```http
Content-Type: application/json; charset=utf-8
Authorization: Bearer <access_token>
```

**Request Body:**
```json
{
  "EventCategory": 100,
  "EventCaption": "AdminControlledCommandExecuted",
  "EventDateUtc": "2026-02-03T08:56:30.123Z",
  "UserName": "john.doe@company.com",
  "ProjectName": "Office Building.rvt",
  "FilePath": "C:\\Projects\\Office Building.rvt",
  "RevitVersion": "2022",
  "CommandName": "Move",
  "CmdPrpsCode": "33127",
  "UserActionShortCode": "PROCEEDED",
  "DialogValue": "Guide",
  "UserComment": "Moving wall to correct location",
  "OverrideSource": "UserOverride",
  "ModeOnOverride": "Guide",
  "BeforeImageBytes": "iVBORw0KGgoAAAANSUhEUgAABLAAAASwCAYAAAD...",
  "AfterImageBytes": "iVBORw0KGgoAAAANSUhEUgAABLAAAASwCAYAAAD...",
  "BeforeSelectedElementInfoList": [
    {
      "ElementId": 123456,
      "ElementGuid": "abc-123-def-456",
      "Category": "Walls",
      "FamilyName": "Basic Wall",
      "TypeName": "Generic - 200mm"
    }
  ],
  "AfterSelectedElementInfoList": [...],
  "AddedElementInfoList": [],
  "DeletedElementInfoList": [],
  "NumberOfAddedElements": 0,
  "ActiveViewInfo": {
    "ViewName": "Level 1",
    "ViewType": "FloorPlan",
    "ViewGuid": "view-guid-123"
  },
  "WarningInfoList": [],
  "IsInstantlyNotify": true,
  "ProjectSessionGuid": "session-guid-789",
  "IntervalSeqNo": 1,
  "RepetitionCount": null
}
```

**Response:**
```json
200 OK
Body: 1234  (integer - event log ID)
```

---

### Implementation: `SoapProdApiApp.LogUserEvent3WithHttpMessagesAsync()`

```csharp
public async Task<HttpOperationResponse<int?>> LogUserEvent3WithHttpMessagesAsync(
    string licenseGuid,
    string projectGuid,
    RequestOptionsLogUserEventWithAdditionalInfo requestOptions,
    Dictionary<string, List<string>> customHeaders = null,
    CancellationToken cancellationToken = default)
{
    // Validate parameters
    if (licenseGuid == null)
        throw new ValidationException(ValidationRules.CannotBeNull, nameof(licenseGuid));
    if (projectGuid == null)
        throw new ValidationException(ValidationRules.CannotBeNull, nameof(projectGuid));
    if (requestOptions == null)
        throw new ValidationException(ValidationRules.CannotBeNull, nameof(requestOptions));
    
    // Build URL
    string baseUri = this.BaseUri.AbsoluteUri;
    string urlTemplate = "api/Analytics/LogUserEvent3/{licenseGuid}/{projectGuid}";
    string url = new Uri(new Uri(baseUri + (baseUri.EndsWith("/") ? "" : "/")), urlTemplate)
        .ToString()
        .Replace("{licenseGuid}", Uri.EscapeDataString(licenseGuid))
        .Replace("{projectGuid}", Uri.EscapeDataString(projectGuid));
    
    // Create HTTP request
    HttpRequestMessage httpRequest = new HttpRequestMessage();
    httpRequest.Method = new HttpMethod("POST");
    httpRequest.RequestUri = new Uri(url);
    
    // Add custom headers
    if (customHeaders != null)
    {
        foreach (var header in customHeaders)
        {
            if (httpRequest.Headers.Contains(header.Key))
                httpRequest.Headers.Remove(header.Key);
            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }
    
    // Serialize request body to JSON
    string requestContent = SafeJsonConvert.SerializeObject(
        requestOptions, 
        this.SerializationSettings);
    
    httpRequest.Content = new StringContent(requestContent, Encoding.UTF8);
    httpRequest.Content.Headers.ContentType = 
        MediaTypeHeaderValue.Parse("application/json; charset=utf-8");
    
    // Add authorization
    if (this.Credentials != null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await this.Credentials.ProcessHttpRequestAsync(httpRequest, cancellationToken)
            .ConfigureAwait(false);
    }
    
    // Send request
    HttpResponseMessage httpResponse = await this.HttpClient.SendAsync(
        httpRequest, 
        cancellationToken).ConfigureAwait(false);
    
    // Check status
    HttpStatusCode statusCode = httpResponse.StatusCode;
    if (statusCode != HttpStatusCode.OK)
    {
        throw new HttpOperationException(
            $"Operation returned an invalid status code '{statusCode}'");
    }
    
    // Deserialize response
    string responseContent = await httpResponse.Content.ReadAsStringAsync()
        .ConfigureAwait(false);
    
    int? eventLogId = SafeJsonConvert.DeserializeObject<int?>(
        responseContent, 
        this.DeserializationSettings);
    
    // Return result
    return new HttpOperationResponse<int?>
    {
        Request = httpRequest,
        Response = httpResponse,
        Body = eventLogId
    };
}
```

---

## Complete Upload Flow

### High-Level Sequence

```
┌─────────────────────────────────────────────────────────────┐
│ 1. USER EXECUTES COMMAND                                     │
│    User clicks "Move" button or presses keyboard shortcut    │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│ 2. BEFORE_EXECUTED EVENT                                     │
│    CommandMonitor.SwitchToNewActiveCommand()                 │
│                                                               │
│    • Create UserEventData object                             │
│    • Capture BEFORE screenshot:                              │
│      - Call UIUtils.TakeScreenshot(document)                 │
│      - Get Revit window handle                               │
│      - Call PrintWindow API                                  │
│      - Save as PNG bytes                                     │
│      - Convert to Base64 string                              │
│      - Store in UserEventData.BeforeImageBytes               │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│ 3. COMMAND EXECUTES                                          │
│    Revit processes the command (Move, Copy, Delete, etc.)    │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│ 4. EXECUTED EVENT                                            │
│    CommandMonitor.CompleteActiveCommand()                    │
│                                                               │
│    • Capture AFTER screenshot:                               │
│      - Call UIUtils.TakeScreenshot(document)                 │
│      - Convert to Base64 string                              │
│      - Store in UserEventData.AfterImageBytes                │
│                                                               │
│    • Populate additional data:                               │
│      - Selected elements                                     │
│      - Added/Deleted elements                                │
│      - Active view info                                      │
│      - User comment/action                                   │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│ 5. UPLOAD TO SERVER                                          │
│    AnalyticsStorageHelper.PushToAnalytics()                  │
│                                                               │
│    • Serialize UserEventData to JSON                         │
│    • HTTP POST to api/Analytics/LogUserEvent3                │
│    • Server saves event log with screenshots                 │
│    • Returns event log ID                                    │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│ 6. CLEANUP                                                   │
│    CommandMonitor.CleanupBeforeAndAfterScreenshotData()      │
│                                                               │
│    • Clear UserEventData.BeforeImageBytes                    │
│    • Clear UserEventData.AfterImageBytes                     │
│    • Free memory                                             │
└─────────────────────────────────────────────────────────────┘
```

---

## Server-Side Processing (Inferred)

Based on the API endpoint and model structure, the server likely processes screenshots as follows:

### Database Storage

```sql
CREATE TABLE UserEventLogs
(
    EventLogId INT PRIMARY KEY IDENTITY,
    CompanyId GUID NOT NULL,
    ProjectGuid GUID NOT NULL,
    EventCategory INT,  -- 100 = Command
    EventCaption VARCHAR(100),  -- "AdminControlledCommandExecuted"
    EventDateUtc DATETIME2,
    UserName VARCHAR(200),
    ProjectName VARCHAR(500),
    FilePath VARCHAR(1000),
    RevitVersion VARCHAR(50),
    CommandName VARCHAR(200),
    CmdPrpsCode VARCHAR(50),
    UserActionShortCode VARCHAR(50),
    DialogValue VARCHAR(50),
    UserComment NVARCHAR(MAX),
    OverrideSource VARCHAR(100),
    ModeOnOverride VARCHAR(50),
    
    -- SCREENSHOTS stored as Base64 strings (large text)
    BeforeImageBytes NVARCHAR(MAX),
    AfterImageBytes NVARCHAR(MAX),
    
    -- JSON for complex data
    BeforeSelectedElementInfoList NVARCHAR(MAX),
    AfterSelectedElementInfoList NVARCHAR(MAX),
    AddedElementInfoList NVARCHAR(MAX),
    DeletedElementInfoList NVARCHAR(MAX),
    NumberOfAddedElements INT,
    
    ActiveViewInfo NVARCHAR(MAX),
    WarningInfoList NVARCHAR(MAX),
    
    IsInstantlyNotify BIT,
    ProjectSessionGuid GUID,
    
    IntervalSeqNo INT,
    RepetitionCount INT,
    
    SyncGuid GUID,
    
    -- Pin protection
    UnpinnedElements NVARCHAR(MAX),
    NotifyUnpinElementNames NVARCHAR(MAX),
    ProtectedBy VARCHAR(200),
    GroupType VARCHAR(100),
    
    CreatedDate DATETIME2 DEFAULT GETUTCDATE(),
    INDEX IX_ProjectGuid (ProjectGuid),
    INDEX IX_EventDateUtc (EventDateUtc),
    INDEX IX_UserName (UserName)
);
```

### Alternative: Blob Storage

For better performance, screenshots might be stored in Azure Blob Storage:

```
Container: legacy product-screenshots
Blob naming: {projectGuid}/{eventLogId}_before.png
              {projectGuid}/{eventLogId}_after.png
              
Database stores:
  BeforeImageUrl: https://storage.azure.com/legacy product-screenshots/{projectGuid}/{eventLogId}_before.png
  AfterImageUrl: https://storage.azure.com/legacy product-screenshots/{projectGuid}/{eventLogId}_after.png
```

---

## Configuration: AllowScreenshots Setting

Screenshots can be enabled/disabled per project.

**Model: `UserInteractionSettings`**

```csharp
public class UserInteractionSettings
{
    [JsonProperty(PropertyName = "AllowScreenshots")]
    public bool? AllowScreenshots { get; set; }
    
    // Other settings...
}
```

**Default Value:**
```csharp
public static UserInteractionSettings GetDefault()
{
    return new UserInteractionSettings
    {
        AllowScreenshots = true,  // Enabled by default
        // ...
    };
}
```

**Usage in UI:**
```csharp
// ProjectConfigViewModel.cs
public bool? IsAllowScreenshotsCapture
{
    get => this._isAllowScreenshotsCapture;
    set
    {
        if (Nullable.Equals(this._isAllowScreenshotsCapture, value))
            return;
        
        this._isAllowScreenshotsCapture = value;
        
        // Update project settings
        this.SelectedProjectConfig.ProjectSettingsDetail.UserInteractionSettings.AllowScreenshots = value;
        
        OnPropertyChanged(nameof(IsAllowScreenshotsCapture));
    }
}
```

---

## Implementation Checklist for CBOX Manage

### Phase 1: Screenshot Capture

- [ ] Create `ScreenshotHelper` class
  - [ ] Add Windows API interop for `GetWindowRect` and `PrintWindow`
  - [ ] Implement `CaptureRevitWindow()` method
  - [ ] Implement `CaptureDrawingArea(Document)` method
  - [ ] Handle errors gracefully (return null on failure)

- [ ] Add PNG encoding
  - [ ] Use `ImageFormat.Png` for compression
  - [ ] Return `byte[]` from capture method

- [ ] Add Base64 encoding
  - [ ] `Convert.ToBase64String(byte[])`
  - [ ] Return `string` for JSON serialization

### Phase 2: Data Model

- [ ] Create `CommandEventLog` class
  - [ ] Add `string BeforeImageBase64 { get; set; }`
  - [ ] Add `string AfterImageBase64 { get; set; }`
  - [ ] Add `[JsonProperty]` attributes for all properties
  - [ ] Add metadata fields (EventCaption, EventDateUtc, UserName, etc.)

- [ ] Add `ElementInfo` class for element tracking
- [ ] Add `ViewInfo` class  for active view tracking
- [ ] Add `WarningInfo` class for warning events (optional)

### Phase 3: Upload Implementation

- [ ] Create API endpoint
  - [ ] `POST api/Analytics/LogCommandEvent/{companyId}/{projectId}`
  - [ ] Accept `CommandEventLog` in request body
  - [ ] Return event log ID (int)

- [ ] Create HTTP client method
  - [ ] `Task<int> UploadCommandEventAsync(CommandEventLog eventLog)`
  - [ ] Serialize to JSON with `JsonConvert.SerializeObject()`
  - [ ] Set `Content-Type: application/json`
  - [ ] Handle errors and retries

### Phase 4: Server-Side Storage

- [ ] Create database table `CommandEventLogs`
  - [ ] Add columns for all `CommandEventLog` properties
  - [ ] Use `NVARCHAR(MAX)` for screenshot Base64 strings
  - [ ] Add indexes on `ProjectId`, `EventDateUtc`, `UserName`

- [ ] **Alternative:** Use Azure Blob Storage
  - [ ] Decode Base64 to byte[]
  - [ ] Upload to blob: `{projectId}/{eventLogId}_before.png`
  - [ ] Store blob URL in database instead of Base64

- [ ] Create API controller method
  - [ ] Validate request model
  - [ ] Save to database
  - [ ] Return event log ID

### Phase 5: Integration

- [ ] Modify `CommandMonitor` class
  - [ ] Capture "Before" screenshot on command start
  - [ ] Capture "After" screenshot on command complete
  - [ ] Store in `CommandEventLog` object

- [ ] Upload on command completion
  - [ ] Call `UploadCommandEventAsync(eventLog)`
  - [ ] Handle async operation (don't block UI)
  - [ ] Clean up screenshot data after upload

- [ ] Add project setting
  - [ ] `bool AllowScreenshots { get; set; }`
  - [ ] Default: `true`
  - [ ] Check before capturing screenshots

### Phase 6: Performance Optimization

- [ ] Implement background upload queue
  - [ ] Don't block command execution
  - [ ] Use `Task.Run()` or background thread
  - [ ] Retry failed uploads

- [ ] Add screenshot size limits
  - [ ] Resize large screenshots (e.g., > 2MB)
  - [ ] Use JPEG for large images (smaller size)

- [ ] Add compression
  - [ ] Consider JPEG instead of PNG for "After" screenshots
  - [ ] Trade quality for size reduction

### Phase 7: Testing

- [ ] Test screenshot capture
  - [ ] Verify PNG bytes are valid
  - [ ] Verify Base64 encoding/decoding
  - [ ] Test with obscured windows

- [ ] Test upload
  - [ ] Verify JSON serialization
  - [ ] Verify API endpoint receives data
  - [ ] Test large payloads (> 1MB screenshots)

- [ ] Test server-side
  - [ ] Verify database storage
  - [ ] Verify blob storage (if using)
  - [ ] Test image retrieval and display

---

## Summary

the legacy product's screenshot upload mechanism is straightforward and efficient:

1. **Capture:** Uses Windows `PrintWindow` API to capture Revit window as PNG bytes
2. **Encode:** Converts PNG bytes to Base64 string for JSON transport
3. **Embed:** Stores Base64 strings in `RequestOptionsLogUserEventWithAdditionalInfo` model properties (`BeforeImageBytes`, `AfterImageBytes`)
4. **Upload:** HTTP POST to `api/Analytics/LogUserEvent3/{licenseGuid}/{projectGuid}` with JSON payload
5. **Store:** Server saves Base64 strings in database (or decodes and stores in blob storage)

**Key Advantages:**
- ✅ Simple implementation (no file I/O required)
- ✅ Portable (Base64 works everywhere)
- ✅ Atomic (screenshot embedded in same request as event data)
- ✅ Reliable (Windows PrintWindow API)

**Considerations:**
- ⚠️ Large payload size (300-800 KB per screenshot)
- ⚠️ Database storage overhead (NVARCHAR(MAX) can be expensive)
- ⚠️ Consider blob storage for production use

**For CBOX Manage:** Follow the legacy product's approach exactly - it's proven and works well at scale!
