using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Infrastructure.Logging;
using System;
using System.Linq;

namespace BIManage.Revit.PinProtection
{
    /// <summary>
    /// External event handler for pinning elements after CAD import or Copy/Monitor.
    /// Called from DocumentChanged detection when elements need to be pinned.
    /// Cannot modify document inside DocumentChanged — must use ExternalEvent.
    /// </summary>
    public class PinAfterEventExternalEventHandler : IExternalEventHandler
    {
        private readonly ILogger? _logger;

        public PinAfterEventExternalEventHandler(ILogger? logger)
        {
            _logger = logger;
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var info = PinAfterEventExternalEventInfo.Instance;
                var document = info.Document;
                var elementIds = info.ElementIds;
                var sourceProtection = info.SourceProtection;
                var autoPin = info.AutoPinWithoutPrompt;

                if (document == null || !document.IsValidObject || elementIds == null || !elementIds.Any())
                {
                    _logger?.LogDebug("PinAfterEvent: No valid elements to process");
                    info.Clear();
                    return;
                }

                _logger?.LogInfo($"PinAfterEvent: Processing {elementIds.Count} elements from {sourceProtection}");

                bool shouldPin = autoPin;

                if (!autoPin)
                {
                    // Show prompt to user
                    string message = sourceProtection switch
                    {
                        "Ze_CADImportPinPrompt" =>
                            $"You have imported {elementIds.Count} CAD element(s).\nWould you like to pin them to prevent accidental movement?",
                        "Ze_RVTLinkPinPrompt" =>
                            $"You have inserted {elementIds.Count} Revit link(s).\nWould you like to pin them to prevent accidental movement?",
                        _ =>
                            $"Copy/Monitor operation completed with {elementIds.Count} element(s).\nWould you like to pin the monitored elements?"
                    };

                    var dialog = new TaskDialog("Pin Protection")
                    {
                        MainInstruction = "Pin Elements?",
                        MainContent = message,
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.Yes
                    };

                    shouldPin = dialog.Show() == TaskDialogResult.Yes;
                }

                if (shouldPin)
                {
                    int pinnedCount = 0;
                    using (var tx = new Transaction(document, $"Pin Elements ({sourceProtection})"))
                    {
                        tx.Start();
                        foreach (var elementId in elementIds)
                        {
                            try
                            {
                                var element = document.GetElement(elementId);
                                if (element != null && element.IsValidObject && !element.Pinned)
                                {
                                    element.Pinned = true;
                                    pinnedCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogDebug($"PinAfterEvent: Could not pin element {elementId}: {ex.Message}");
                            }
                        }
                        tx.Commit();
                    }

                    _logger?.LogInfo($"PinAfterEvent: Pinned {pinnedCount}/{elementIds.Count} elements from {sourceProtection}");
                }
                else
                {
                    _logger?.LogInfo($"PinAfterEvent: User declined to pin {elementIds.Count} elements from {sourceProtection}");
                }

                info.Clear();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in PinAfterEvent handler: {ex.Message}", ex);
                PinAfterEventExternalEventInfo.Instance.Clear();
            }
        }

        public string GetName() => "PinAfterEventExternalEvent";
    }
}
