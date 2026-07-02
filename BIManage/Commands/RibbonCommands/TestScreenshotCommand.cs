using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Core.Evidence;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;

namespace BIManageRevit.Commands.RibbonCommands
{
    /// <summary>
    /// Test command to verify screenshot functionality
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class TestScreenshotCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            try
            {
                var uiApp = commandData.Application;

                // Get logger from ServiceRegistry
                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;

                var logger = services?.GetService<ILogger>();
                if (logger == null)
                {
                    TaskDialog.Show("Screenshot Test Error",
                        "Unable to access logging service.\n\nCannot proceed with screenshot test.");
                    return Result.Failed;
                }

                // Generate test IDs
                var sessionId = Guid.NewGuid().ToString("N");
                var evidenceId = Guid.NewGuid().ToString("N");

                // Create screenshot service
                var screenshotService = new ScreenshotService(uiApp, logger);

                logger.LogInfo("=== SCREENSHOT TEST STARTED ===");
                logger.LogInfo($"Session ID: {sessionId}");
                logger.LogInfo($"Evidence ID: {evidenceId}");

                // Test 1: Capture screenshot without hash (COMMENTED OUT - Using hash-based screenshot only)
                /*
                logger.LogInfo("Test 1: Capturing screenshot (no hash)...");

                string filePath1 = null;
                var captureTask1 = Task.Run(async () =>
                    await screenshotService.CaptureRevitWindowAsync(evidenceId, sessionId, "test"));

                if (!captureTask1.Wait(TimeSpan.FromSeconds(10)))
                {
                    logger.LogError("Test 1: Screenshot capture timed out after 10 seconds");
                    TaskDialog.Show("Screenshot Test Error",
                        "Screenshot capture timed out (Test 1).\n\n" +
                        "This may indicate a graphics driver or system issue.\n\n" +
                        "Check the log file for details.");
                    return Result.Failed;
                }

                try
                {
                    filePath1 = captureTask1.Result; // Safe - task already completed
                }
                catch (AggregateException ae)
                {
                    var innerEx = ae.InnerException ?? ae;
                    logger.LogError($"Test 1: Screenshot capture failed: {innerEx.Message}", innerEx);
                    TaskDialog.Show("Screenshot Test Error",
                        $"Screenshot capture failed (Test 1):\n\n{innerEx.Message}");
                    return Result.Failed;
                }

                if (filePath1 != null && File.Exists(filePath1))
                {
                    var fileInfo = new FileInfo(filePath1);
                    logger.LogInfo($"✓ Screenshot captured successfully: {filePath1}");
                    logger.LogInfo($"  File size: {fileInfo.Length / 1024} KB");
                }
                else
                {
                    logger.LogError("✗ Screenshot capture FAILED (no file created)");
                }
                */

                // Test 2: Capture screenshot with hash (ACTIVE)
                logger.LogInfo("Test: Capturing screenshot with SHA-256 hash...");

                (string filePath, string sha256Hash)? result2 = null;
                var captureTask2 = Task.Run(async () =>
                    await screenshotService.CaptureRevitWindowWithHashAsync(evidenceId, sessionId, "test"));

                if (!captureTask2.Wait(TimeSpan.FromSeconds(10)))
                {
                    logger.LogError("Screenshot capture timed out after 10 seconds");
                    TaskDialog.Show("Screenshot Test Error",
                        "Screenshot capture with hash timed out.\n\n" +
                        "This may indicate a graphics driver or system issue.\n\n" +
                        "Check the log file for details.");
                    return Result.Failed;
                }

                try
                {
                    result2 = captureTask2.Result; // Safe - task already completed
                }
                catch (AggregateException ae)
                {
                    var innerEx = ae.InnerException ?? ae;
                    logger.LogError($"Screenshot capture failed: {innerEx.Message}", innerEx);
                    TaskDialog.Show("Screenshot Test Error",
                        $"Screenshot capture with hash failed:\n\n{innerEx.Message}");
                    return Result.Failed;
                }

                if (result2 != null)
                {
                    var (filePath, hash) = result2.Value;
                    var fileInfo = new FileInfo(filePath);
                    logger.LogInfo($"✓ Screenshot with hash captured successfully:");
                    logger.LogInfo($"  File: {filePath}");
                    logger.LogInfo($"  Size: {fileInfo.Length / 1024} KB");
                    logger.LogInfo($"  SHA-256: {hash}");

                    // Save screenshot metadata to database
                    try
                    {
                        var evidenceRepo = services?.GetService<EvidenceRepository>();
                        if (evidenceRepo != null)
                        {
                            var evidence = new EvidenceRepository.EvidenceCapture
                            {
                                EvidenceId = evidenceId,
                                AuditLogId = null, // Test command — no audit log to link
                                SessionId = sessionId,
                                CaptureType = "screenshot",
                                CaptureStage = "test",
                                CapturedAt = DateTime.UtcNow,
                                CommandId = "TEST_SCREENSHOT",
                                CommandName = "Test Screenshot",
                                ElementCount = 0,
                                FilePath = filePath,
                                FileSizeBytes = fileInfo.Length,
                                FileFormat = "png",
                                UploadStatus = "pending",
                                FileHashSha256 = hash,
                                Metadata = $"{{\"test\":true,\"revit_window\":true}}"
                            };

                            var recordTask = Task.Run(async () => await evidenceRepo.RecordEvidenceCaptureAsync(evidence));
                            recordTask.Wait();
                            var recordId = recordTask.Result;
                            logger.LogInfo($"✓ Screenshot evidence saved to database (ID: {recordId})");
                        }
                        else
                        {
                            logger.LogWarning("EvidenceRepository not available - screenshot not saved to database");
                        }
                    }
                    catch (Exception dbEx)
                    {
                        logger.LogError($"Failed to save screenshot to database: {dbEx.Message}", dbEx);
                        // Don't fail the test if database save fails
                    }
                }
                else
                {
                    logger.LogError("✗ Screenshot with hash FAILED");
                }

                // Test 3: Check temp directory
                var tempDir = screenshotService.GetTempStorageDirectory(sessionId);
                logger.LogInfo($"Test 3: Checking temp directory...");
                logger.LogInfo($"  Directory: {tempDir}");

                if (Directory.Exists(tempDir))
                {
                    var files = Directory.GetFiles(tempDir, "*.png");
                    logger.LogInfo($"✓ Temp directory exists with {files.Length} PNG file(s)");
                    foreach (var file in files)
                    {
                        logger.LogInfo($"  - {Path.GetFileName(file)}");
                    }
                }
                else
                {
                    logger.LogError("✗ Temp directory does not exist");
                }

                logger.LogInfo("=== SCREENSHOT TEST COMPLETED ===");

                // Show result dialog
                var resultMsg = $"Screenshot Test Results:\n\n" +
                                $"Screenshot with SHA-256 Hash: {(result2 != null ? "✓ PASS" : "✗ FAIL")}\n\n" +
                                $"Screenshots saved to:\n{tempDir}\n\n" +
                                $"Check the main log file for details.";

                TaskDialog.Show("Screenshot Test", resultMsg);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = $"Screenshot test failed: {ex.Message}";
                TaskDialog.Show("Screenshot Test Error", $"Test failed:\n\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}");
                return Result.Failed;
            }
        }
    }
}
