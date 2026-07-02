
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Common.Helpers;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Auth;
using BIManage.Revit.Helpers;
using BIManage.Revit.PinProtection;
using BIManage.Revit.PinProtection.Models;
using BIManageRevit.BIManage.Views.Bindings;

namespace BIManageRevit.Commands.RibbonCommands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class PinProtectionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            if (LicenseGuard.IsBlockedByLicense(commandData)) return Result.Succeeded;

            try
            {
                var uidoc = commandData.Application.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null)
                {
                    TaskDialog.Show("Pin Protection", "No active document found.");
                    return Result.Failed;
                }

                // Get application services
                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;

                if (services == null)
                {
                    TaskDialog.Show("Pin Protection", "Unable to access application services.");
                    return Result.Failed;
                }

                var pinProtectionRepo = services.GetService<PinProtectionRepository>();
                if (pinProtectionRepo == null)
                {
                    TaskDialog.Show("Pin Protection", "Pin protection repository not available.");
                    return Result.Failed;
                }

                // If user has elements selected, apply Protected Pin directly to them.
                var selectedIds = uidoc.Selection.GetElementIds();
                if (selectedIds != null && selectedIds.Count > 0)
                {
                    var selectedElements = selectedIds
                        .Select(id => doc.GetElement(id))
                        .Where(el => el != null && el.IsValidObject)
                        .ToList();

                    if (selectedElements.Count > 0)
                    {
                        return ApplyProtectedPinToSelection(commandData, doc, selectedElements, services);
                    }
                }

                // No selection → existing status dialog behavior
                string modelGuid = ModelGuidHelper.GetModelGuid(doc);

                var protectedElements = Task.Run(() => pinProtectionRepo.GetProtectedElementsWithDetailsAsync(modelGuid)).GetAwaiter().GetResult()
                    ?? new List<ProtectedElementDetails>();

                ResolveProtectedByNames(services, protectedElements);

                // Reloader: re-queries the local DB and resolves names again. Called every time
                // the dialog is activated (e.g. after the user unpins an element and refocuses it).
                Func<Task<List<ProtectedElementDetails>>> reloader = async () =>
                {
                    try
                    {
                        var fresh = await pinProtectionRepo.GetProtectedElementsWithDetailsAsync(modelGuid)
                                    ?? new List<ProtectedElementDetails>();
                        ResolveProtectedByNames(services, fresh);
                        return fresh;
                    }
                    catch { return new List<ProtectedElementDetails>(); }
                };

                var dialog = new PinProtectionStatusDialog(protectedElements, reloader);
                new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = commandData.Application.MainWindowHandle };
                dialog.ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = $"Error in Pin Protection: {ex.Message}";
                return Result.Failed;
            }
        }

        /// <summary>
        /// When the user clicks Pin Protection with elements selected, show the same
        /// Pin / Protected Pin chooser dialog that the native Revit Pin button uses,
        /// then apply the user's choice.
        /// </summary>
        private static Result ApplyProtectedPinToSelection(
            ExternalCommandData commandData,
            Document doc,
            List<Element> selectedElements,
            global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry services)
        {
            // Gate on model registration — protection requires a registered model
            string modelGuid = ModelGuidHelper.GetModelGuid(doc);
            if (!string.IsNullOrEmpty(modelGuid))
            {
                var modelRepo = services.GetService<RegisteredModelsRepository>();
                if (modelRepo != null)
                {
                    bool isRegistered = Task.Run(() => modelRepo.IsModelRegisteredAsync(modelGuid)).GetAwaiter().GetResult();
                    if (!isRegistered)
                    {
                        TaskDialog.Show("Pin Protection",
                            "Pin protection requires the model to be registered first.\n\n" +
                            "Please register this model before applying protected pins.");
                        return Result.Cancelled;
                    }
                }
            }

            var username = GetUsername(services, commandData);

            // Step 1: pin the elements (if not already pinned)
            using (var pinTx = new Transaction(doc, "Pin Elements"))
            {
                pinTx.Start();
                try
                {
                    foreach (var element in selectedElements)
                    {
                        if (element != null && element.IsValidObject && !element.Pinned)
                            element.Pinned = true;
                    }
                    pinTx.Commit();
                }
                catch (Exception ex)
                {
                    pinTx.RollBack();
                    TaskDialog.Show("Pin Protection", $"Failed to pin selected elements.\n\n{ex.Message}");
                    return Result.Failed;
                }
            }

            // Step 2: apply Protected Pin metadata
            using (var protectTx = new Transaction(doc, "Apply Pin Protection"))
            {
                protectTx.Start();
                try
                {
                    var info = ProtectedPinInfo.Create(
                        selectedElements[0],
                        username,
                        adminComment: "Protected element - admin authorization required to unpin",
                        mode: ProtectionMode.Protect,
                        requireComment: true,
                        notifyOnUnpin: true);

                    PinProtectionStorage.SetProtection(doc, info, selectedElements);
                    protectTx.Commit();
                }
                catch (Exception ex)
                {
                    protectTx.RollBack();
                    TaskDialog.Show("Pin Protection", $"Failed to apply protection metadata.\n\n{ex.Message}");
                    return Result.Failed;
                }
            }

            // Show the existing "Protection Applied" success popup (same as native Pin flow)
            try
            {
                var successDialog = new BIManageRevit.BIManage.Views.Bindings.ProtectionAppliedDialog
                {
                    DataContext = new BIManageRevit.BIManage.ViewModels.Bindings.ProtectionAppliedViewModel(
                        selectedElements.Count,
                        username,
                        "Protected Pin has been applied.",
                        () => { })
                };
                new System.Windows.Interop.WindowInteropHelper(successDialog) { Owner = commandData.Application.MainWindowHandle };
                successDialog.ShowDialog();
            }
            catch
            {
                // Fallback if dialog fails to render
                TaskDialog.Show("Pin Protection", $"Protected Pin applied to {selectedElements.Count} element(s).");
            }

            return Result.Succeeded;
        }

        private static string GetUsername(
            global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry services,
            ExternalCommandData commandData)
        {
            try
            {
                var userService = services.GetService<global::BIManage.Core.Identity.IUserService>();
                if (userService?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(userService.CurrentUser?.UserName))
                    return userService.CurrentUser!.UserName!;
            }
            catch { }
            return commandData.Application.Application.Username ?? Environment.UserName;
        }

        /// <summary>
        /// Resolves GUID-format ProtectedBy values to human-readable usernames.
        /// Uses the stored user identity from SecureTokenStorage.
        /// </summary>
        private static void ResolveProtectedByNames(
            global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry services,
            List<ProtectedElementDetails> elements)
        {
            try
            {
                var secureStorage = services.GetService<SecureTokenStorage>();
                if (secureStorage == null) return;

                var identity = secureStorage.LoadUserIdentity();
                if (identity == null || string.IsNullOrEmpty(identity.UserId)) return;

                var displayName = identity.UserName ?? identity.Email ?? identity.UserId;

                foreach (var element in elements)
                {
                    // Match against UserId, ProfileId, or Email
                    if (string.Equals(element.ProtectedBy, identity.UserId, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrEmpty(identity.ProfileId) && string.Equals(element.ProtectedBy, identity.ProfileId, StringComparison.OrdinalIgnoreCase))
                        || (!string.IsNullOrEmpty(identity.Email) && string.Equals(element.ProtectedBy, identity.Email, StringComparison.OrdinalIgnoreCase)))
                    {
                        element.ProtectedBy = displayName;
                    }
                }
            }
            catch
            {
                // Fail silently - showing GUIDs is acceptable fallback
            }
        }
    }

    /// <summary>
    /// Detailed information about a protected element from database
    /// </summary>
    public class ProtectedElementDetails
    {
        public string ElementGuid { get; set; } = string.Empty;
        public long ElementId { get; set; }
        public string? ElementName { get; set; }
        public string? ElementCategory { get; set; }
        public int ProtectionMode { get; set; }
        public string ProtectedBy { get; set; } = string.Empty;
        public DateTime ProtectedAt { get; set; }
        public string? AdminComment { get; set; }
    }
}
