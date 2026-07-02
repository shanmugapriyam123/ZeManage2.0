using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using BIManage.Infrastructure.Logging;

namespace BIManage.Core.Evidence
{
    /// <summary>
    /// Implementation of screenshot service for capturing Revit window evidence
    /// </summary>
    public class ScreenshotService : IScreenshotService
    {
        private readonly ILogger? _logger;
        private readonly UIApplication _uiApp;
        private readonly ImageCodecInfo? _pngCodec; // Cache PNG encoder to eliminate 100-500ms enumeration delay

        public ScreenshotService(UIApplication uiApp, ILogger? logger = null)
        {
            _uiApp = uiApp ?? throw new ArgumentNullException(nameof(uiApp));
            _logger = logger;

            // Cache PNG encoder at initialization (eliminates 100-500ms delay per screenshot)
            try
            {
                _pngCodec = ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(codec => codec.FormatID == ImageFormat.Png.Guid);

                if (_pngCodec != null)
                {
                    _logger?.LogDebug("PNG codec cached successfully");
                }
                else
                {
                    _logger?.LogWarning("PNG codec not found - will use default PNG encoding");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to cache PNG encoder: {ex.Message}");
                _pngCodec = null;
            }
        }

        /// <summary>
        /// Capture the entire Revit window as a PNG screenshot
        /// </summary>
        public async Task<string?> CaptureRevitWindowAsync(string evidenceId, string sessionId, string stage)
        {
            try
            {
                // Get Revit main window handle
                var mainWindowHandle = _uiApp.MainWindowHandle;
                if (mainWindowHandle == IntPtr.Zero)
                {
                    _logger?.LogWarning("Cannot capture screenshot: Revit main window handle is null");
                    return null;
                }

                // Create temp directory for this session
                var tempDir = GetTempStorageDirectory(sessionId);
                Directory.CreateDirectory(tempDir);

                // Generate filename: {evidenceId}_{stage}.png
                var fileName = $"{evidenceId}_{stage}.png";
                var filePath = Path.Combine(tempDir, fileName);

                // Capture screenshot synchronously (must be on current thread for Win32 GDI operations)
                // The calling code (IExternalCommand) is already on Revit's UI thread
                CaptureWindow(mainWindowHandle, filePath);

                if (File.Exists(filePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    _logger?.LogInfo($"Screenshot captured: {fileName} ({fileInfo.Length / 1024} KB)");
                    return filePath;
                }
                else
                {
                    _logger?.LogError("Screenshot capture failed: file was not created");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to capture screenshot for evidence {evidenceId}: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Capture screenshot and calculate SHA-256 hash for integrity verification
        /// </summary>
        public async Task<(string filePath, string sha256Hash)?> CaptureRevitWindowWithHashAsync(
            string evidenceId,
            string sessionId,
            string stage)
        {
            var filePath = await CaptureRevitWindowAsync(evidenceId, sessionId, stage);
            if (filePath == null) return null;

            // Calculate SHA-256 hash
            var hash = await CalculateFileHashAsync(filePath);

            _logger?.LogInfo($"Screenshot hash calculated: {Path.GetFileName(filePath)}, SHA-256: {hash}");

            return (filePath, hash);
        }

        /// <summary>
        /// Calculate SHA-256 hash of file contents
        /// </summary>
        public async Task<string> CalculateFileHashAsync(string filePath)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                var fileBytes = await Task.Run(() => File.ReadAllBytes(filePath));
                var hashBytes = sha256.ComputeHash(fileBytes);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Get the temporary storage directory for screenshots
        /// </summary>
        public string GetTempStorageDirectory(string sessionId)
        {
            var tempBase = Path.GetTempPath();
            return Path.Combine(tempBase, "BIManage", "Evidence", sessionId);
        }

        /// <summary>
        /// Clean up old screenshots from temp directory after successful upload
        /// </summary>
        public async Task CleanupTempFilesAsync(string sessionId, int olderThanHours = 24)
        {
            try
            {
                var tempDir = GetTempStorageDirectory(sessionId);
                if (!Directory.Exists(tempDir))
                    return;

                var cutoffTime = DateTime.Now.AddHours(-olderThanHours);

                await Task.Run(() =>
                {
                    var files = Directory.GetFiles(tempDir, "*.png")
                        .Where(f => File.GetCreationTime(f) < cutoffTime)
                        .ToList();

                    foreach (var file in files)
                    {
                        try
                        {
                            File.Delete(file);
                            _logger?.LogDebug($"Deleted old screenshot: {Path.GetFileName(file)}");
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning($"Failed to delete {Path.GetFileName(file)}: {ex.Message}");
                        }
                    }

                    if (files.Count > 0)
                    {
                        _logger?.LogInfo($"Cleaned up {files.Count} old screenshot(s) from temp directory");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to cleanup temp files: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Capture the entire Revit window as Base64-encoded PNG bytes
        /// Uses Win32 PrintWindow/BitBlt for capture
        /// </summary>
        /// <returns>Base64-encoded PNG image string, or null if capture failed</returns>
        public string? CaptureRevitWindowAsBase64()
        {
            try
            {
                // Get Revit main window handle
                var mainWindowHandle = _uiApp.MainWindowHandle;
                if (mainWindowHandle == IntPtr.Zero)
                {
                    _logger?.LogWarning("Cannot capture screenshot: Revit main window handle is null");
                    return null;
                }

                // Capture window to bitmap and convert to Base64
                var base64 = CaptureWindowAsBase64(mainWindowHandle);

                if (!string.IsNullOrEmpty(base64))
                {
                    _logger?.LogInfo($"Screenshot captured as Base64 ({base64.Length} chars)");
                }

                return base64;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to capture screenshot as Base64: {ex.Message}", ex);
                return null;
            }
        }

        #region Screenshot Capture Implementation

        /// <summary>
        /// Capture a window by its handle and return as Base64-encoded PNG string
        /// </summary>
        private string? CaptureWindowAsBase64(IntPtr windowHandle)
        {
            // Get visual (shadow-free) window rectangle
            if (!GetVisualWindowRect(windowHandle, out RECT rect, out int xSrcOffset, out int ySrcOffset))
            {
                throw new InvalidOperationException("Failed to get window rectangle");
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException($"Invalid window dimensions: {width}x{height}");
            }

            // Create bitmap and capture window
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    var hdcDest = graphics.GetHdc();
                    try
                    {
                        var hdcSrc = GetWindowDC(windowHandle);
                        try
                        {
                            // BitBlt from visual origin (skipping DWM shadow padding)
                            if (!BitBlt(hdcDest, 0, 0, width, height, hdcSrc, xSrcOffset, ySrcOffset, SRCCOPY))
                            {
                                throw new InvalidOperationException("BitBlt operation failed");
                            }
                        }
                        finally
                        {
                            ReleaseDC(windowHandle, hdcSrc);
                        }
                    }
                    finally
                    {
                        graphics.ReleaseHdc(hdcDest);
                    }
                }

                // Convert bitmap to PNG bytes in memory
                using (var memoryStream = new MemoryStream())
                {
                    // Save as PNG with compression
                    var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);

                    // Use cached PNG codec (eliminates 100-500ms enumeration delay)
                    if (_pngCodec != null)
                    {
                        bitmap.Save(memoryStream, _pngCodec, encoderParams);
                    }
                    else
                    {
                        // Fallback to default PNG save
                        bitmap.Save(memoryStream, ImageFormat.Png);
                    }

                    // Convert to Base64 string
                    var pngBytes = memoryStream.ToArray();
                    return Convert.ToBase64String(pngBytes);
                }
            }
        }

        /// <summary>
        /// Capture a window by its handle and save as PNG
        /// </summary>
        private void CaptureWindow(IntPtr windowHandle, string outputPath)
        {
            // Get visual (shadow-free) window rectangle
            if (!GetVisualWindowRect(windowHandle, out RECT rect, out int xSrcOffset, out int ySrcOffset))
            {
                throw new InvalidOperationException("Failed to get window rectangle");
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException($"Invalid window dimensions: {width}x{height}");
            }

            // Create bitmap and capture window
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    var hdcDest = graphics.GetHdc();
                    try
                    {
                        var hdcSrc = GetWindowDC(windowHandle);
                        try
                        {
                            // BitBlt from visual origin (skipping DWM shadow padding)
                            if (!BitBlt(hdcDest, 0, 0, width, height, hdcSrc, xSrcOffset, ySrcOffset, SRCCOPY))
                            {
                                throw new InvalidOperationException("BitBlt operation failed");
                            }
                        }
                        finally
                        {
                            ReleaseDC(windowHandle, hdcSrc);
                        }
                    }
                    finally
                    {
                        graphics.ReleaseHdc(hdcDest);
                    }
                }

                // Save as PNG with compression
                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);

                // Use cached PNG codec (eliminates 100-500ms enumeration delay)
                if (_pngCodec != null)
                {
                    bitmap.Save(outputPath, _pngCodec, encoderParams);
                }
                else
                {
                    // Fallback to default PNG save
                    bitmap.Save(outputPath, ImageFormat.Png);
                }
            }
        }

        #endregion

        /// <summary>
        /// Save Base64 screenshot to temp folder (for developer debugging)
        /// </summary>
        /// <param name="base64Image">Base64-encoded PNG image</param>
        /// <param name="sessionId">Session ID for folder organization</param>
        /// <param name="filename">Filename without extension</param>
        /// <returns>File path where saved, or null if failed</returns>
        public string? SaveBase64ToTempFile(string base64Image, string sessionId, string filename)
        {
            try
            {
                var tempDir = GetTempStorageDirectory(sessionId);
                Directory.CreateDirectory(tempDir);

                var filePath = Path.Combine(tempDir, $"{filename}.png");
                var bytes = Convert.FromBase64String(base64Image);
                File.WriteAllBytes(filePath, bytes);

                _logger?.LogDebug($"Saved screenshot to temp: {filePath}");
                return filePath;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to save screenshot to temp: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns the visual (shadow-free) window rect and the source offset into the window DC.
        /// DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS) excludes the invisible DWM drop-shadow
        /// padding that GetWindowRect includes, preventing the thin black border in captures.
        /// Falls back to GetWindowRect when DWM compositing is unavailable.
        /// </summary>
        private bool GetVisualWindowRect(IntPtr windowHandle, out RECT visualRect, out int xSrcOffset, out int ySrcOffset)
        {
            int hr = DwmGetWindowAttribute(windowHandle, DWMWA_EXTENDED_FRAME_BOUNDS,
                                           out RECT extendedRect,
                                           Marshal.SizeOf(typeof(RECT)));
            if (hr == 0) // S_OK
            {
                GetWindowRect(windowHandle, out RECT fullRect);
                // Shadow inset = difference between GetWindowRect and DWM extended bounds
                xSrcOffset = extendedRect.Left - fullRect.Left;
                ySrcOffset = extendedRect.Top  - fullRect.Top;
                visualRect = extendedRect;
                return true;
            }

            // Fallback: DWM unavailable (e.g. non-composited RDP session)
            xSrcOffset = 0;
            ySrcOffset = 0;
            return GetWindowRect(windowHandle, out visualRect);
        }

        #region Win32 API Imports

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
            IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        private const int SRCCOPY = 0x00CC0020;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        #endregion
    }
}
