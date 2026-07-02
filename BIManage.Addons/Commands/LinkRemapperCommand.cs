using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.UI;
using BIManage.Addons.Helpers;
using BIManage.Addons.Models;
using BIManage.Addons.ViewModels.LinkRemapper;
using BIManage.Addons.Views.Common;
using BIManage.Addons.Views.LinkRemapper;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace BIManage.Addons.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LinkRemapperCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData cd, ref string msg, ElementSet els)
        {
            RevitWindowOwnership.RememberRevitHwnd(cd.Application.MainWindowHandle);

            try
            {
                var uiDoc = cd?.Application?.ActiveUIDocument;
                if (uiDoc == null || uiDoc.Document == null)
                {
                    ZeMessageBox.Show("Link Remapper",
                        "Please open a Revit project document before running Link Remapper.");
                    return Result.Cancelled;
                }
                var doc = uiDoc.Document;

                var links = new ObservableCollection<RevitLinkItem>(
                    new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .Select(inst => doc.GetElement(inst.GetTypeId()) as RevitLinkType)
                    .Where(lt => lt != null && !lt.IsNestedLink)
                    .GroupBy(lt => lt.Id)
                    .Select(g => g.First())
                    .Select(lt => new RevitLinkItem
                    {
                        LinkName = lt.Name ?? "(unnamed link)",
                        CurrentFilePath = GetLinkDisplayPath(lt) ?? "Unknown",
                        IsSelected = false
                    })
                );

                if (links.Count == 0)
                {
                    ZeMessageBox.Show("Link Remapper", "No Revit Links found in the active document.");
                    return Result.Succeeded;
                }

                var vm = new RevitLinksViewModel(links);
                var window = new LinkRemapperWindow { DataContext = vm };
                if (window.ShowDialog() != true)
                    return Result.Succeeded;

                var successes = new List<string>();
                var errors = new List<string>();

                foreach (var item in vm.RevitLinks.Where(x => x.IsSelected))
                {
                    try
                    {
                        if (vm.SelectedMap != null &&
                            vm.SelectedMap.TryGetValue(item.LinkName, out ApsVersion version))
                        {
                            ApplyCloudVersion(doc, item.LinkName, version);
                        }
                        else
                        {
                            var p = item.NewFilePaths?.LastOrDefault() ?? "";
                            if (p.StartsWith("cloud://", StringComparison.OrdinalIgnoreCase))
                                ApplyCloudPath(doc, item.LinkName, p);
                            else if (p.StartsWith("Autodesk Docs://", StringComparison.OrdinalIgnoreCase))
                                ApplyDocsPath(doc, item.LinkName, p);
                            else
                                ApplyLocalPath(doc, item.LinkName, p);
                        }

                        successes.Add(item.LinkName);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{item.LinkName}: {ex.Message}");
                    }
                }

                ShowSummary(successes, errors);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                msg = ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace;
                return Result.Failed;
            }
        }

        private void ApplyCloudVersion(Document doc, string linkName, ApsVersion v)
        {
            string region = v.Region.Equals("EMEA", StringComparison.OrdinalIgnoreCase)
                ? "EMEA"
                : v.Region.StartsWith("AP", StringComparison.OrdinalIgnoreCase)
                    ? "AP"
                    : "US";

            var mp = ModelPathUtils.ConvertCloudGUIDsToCloudPath(
                         region,
                         Guid.Parse(v.ProjectGuidRaw),
                         Guid.Parse(v.ModelGuidRaw));

            ReloadLink(doc, linkName, mp, null);
        }

        private void ApplyCloudPath(Document doc, string linkName, string path)
        {
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var regionTag = parts[1];
            var projGuid = Guid.Parse(parts[2]);
            var modelGuid = Guid.Parse(parts[3]);

            string region = regionTag.Equals("EMEA", StringComparison.OrdinalIgnoreCase)
                ? "EMEA"
                : regionTag.StartsWith("AP", StringComparison.OrdinalIgnoreCase)
                    ? "AP"
                    : "US";

            var mp = ModelPathUtils.ConvertCloudGUIDsToCloudPath(region, projGuid, modelGuid);
            ReloadLink(doc, linkName, mp, null);
        }

        private void ApplyDocsPath(Document doc, string linkName, string path)
        {
            var lt = FindLinkType(doc, linkName);
            var refs = lt.GetExternalResourceReferences();
            var typ = ExternalResourceTypes.BuiltInExternalResourceTypes.RevitLink;

            if (refs != null && refs.TryGetValue(typ, out var oldRef))
            {
                var newRef = new ExternalResourceReference(
                    oldRef.ServerId,
                    oldRef.GetReferenceInformation(),
                    oldRef.Version,
                    path);
                lt.LoadFrom(newRef, new WorksetConfiguration());
            }
            else
            {
                throw new InvalidOperationException("Cloud-link metadata missing for Docs path.");
            }
        }

        private void ApplyLocalPath(Document doc, string linkName, string path)
        {
            if (!System.IO.File.Exists(path))
                throw new System.IO.FileNotFoundException(path);

            var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
            ReloadLink(doc, linkName, mp, new WorksetConfiguration());
        }

        private void ReloadLink(Document doc, string linkName, ModelPath mp, WorksetConfiguration cfg)
        {
            var lt = FindLinkType(doc, linkName);
            lt.LoadFrom(mp, cfg);
        }

        private RevitLinkType FindLinkType(Document doc, string linkName)
        {
            var lt = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkType))
                .Cast<RevitLinkType>()
                .FirstOrDefault(x => x.Name == linkName);
            if (lt == null)
                throw new InvalidOperationException($"Couldn't find link type '{linkName}'.");
            return lt;
        }

        private string GetLinkDisplayPath(RevitLinkType lt)
        {
            try
            {
                var fr = lt.GetExternalFileReference();
                if (fr != null)
                    return ModelPathUtils.ConvertModelPathToUserVisiblePath(fr.GetPath());
            }
            catch { /* not a file link */ }

            var refs = lt.GetExternalResourceReferences();
            var typ = ExternalResourceTypes.BuiltInExternalResourceTypes.RevitLink;

            if (refs != null && refs.TryGetValue(typ, out var r))
            {
                if (!string.IsNullOrEmpty(r.InSessionPath))
                    return r.InSessionPath;

                var info = r.GetReferenceInformation();
                string proj = info.TryGetValue("ProjectName", out var p) ? p : "Docs";
                return $"Autodesk Docs://{proj}/{r.GetResourceShortDisplayName()}";
            }
            return "Unknown";
        }

        private void ShowSummary(List<string> ok, List<string> err)
        {
            var report = "";
            if (ok.Any())
                report += "SUCCESS:\n• " + string.Join("\n• ", ok) + "\n\n";
            if (err.Any())
                report += "ERRORS:\n• " + string.Join("\n• ", err);

            ZeMessageBox.Show(
                "Reload Summary",
                string.IsNullOrEmpty(report) ? "No links were reloaded." : report
            );
        }
    }
}
