using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace BIManage.Core.Evidence
{
    /// <summary>
    /// Service for capturing screenshots of the Revit window for evidence and compliance
    /// </summary>
    public interface IScreenshotService
    {
        /// <summary>
        /// Capture the entire Revit window as a PNG screenshot
        /// </summary>
        /// <param name="evidenceId">Unique evidence ID for the screenshot</param>
        /// <param name="sessionId">Current Revit session ID</param>
        /// <param name="stage">Capture stage: "before" or "after"</param>
        /// <returns>File path where screenshot was saved, or null if capture failed</returns>
        Task<string?> CaptureRevitWindowAsync(string evidenceId, string sessionId, string stage);

        /// <summary>
        /// Capture the entire Revit window as Base64-encoded PNG bytes
        /// Uses Win32 PrintWindow/BitBlt for capture
        /// </summary>
        /// <returns>Base64-encoded PNG image string, or null if capture failed</returns>
        string? CaptureRevitWindowAsBase64();

        /// <summary>
        /// Capture screenshot and calculate SHA-256 hash for integrity verification
        /// </summary>
        /// <param name="evidenceId">Unique evidence ID for the screenshot</param>
        /// <param name="sessionId">Current Revit session ID</param>
        /// <param name="stage">Capture stage: "before" or "after"</param>
        /// <returns>Tuple of (file path, SHA-256 hash) or null if capture failed</returns>
        Task<(string filePath, string sha256Hash)?> CaptureRevitWindowWithHashAsync(string evidenceId, string sessionId, string stage);

        /// <summary>
        /// Calculate SHA-256 hash of file contents
        /// </summary>
        /// <param name="filePath">Path to the file to hash</param>
        /// <returns>SHA-256 hash as lowercase hex string</returns>
        Task<string> CalculateFileHashAsync(string filePath);

        /// <summary>
        /// Get the temporary storage directory for screenshots
        /// </summary>
        string GetTempStorageDirectory(string sessionId);

        /// <summary>
        /// Clean up old screenshots from temp directory after successful upload
        /// </summary>
        /// <param name="sessionId">Session ID to clean up</param>
        /// <param name="olderThanHours">Delete files older than N hours (default: 24)</param>
        Task CleanupTempFilesAsync(string sessionId, int olderThanHours = 24);

        /// <summary>
        /// Save Base64 screenshot to temp folder (for developer debugging)
        /// </summary>
        /// <param name="base64Image">Base64-encoded PNG image</param>
        /// <param name="sessionId">Session ID for folder organization</param>
        /// <param name="filename">Filename without extension</param>
        /// <returns>File path where saved, or null if failed</returns>
        string? SaveBase64ToTempFile(string base64Image, string sessionId, string filename);
    }
}
