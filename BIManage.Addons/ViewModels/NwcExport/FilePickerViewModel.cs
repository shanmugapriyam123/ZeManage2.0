using Autodesk.Revit.DB;
using BIManage.Addons.Services;
using BIManage.Addons.Views.Common;
using BIManage.Addons.Views.NwcExport;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Application = Autodesk.Revit.ApplicationServices.Application;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace BIManage.Addons.ViewModels.NwcExport
{
    public class FilePickerViewModel : INotifyPropertyChanged
    {
        readonly Application _app;
        public ObservableCollection<string> SelectedFiles { get; }
            = new ObservableCollection<string>();
        public ObservableCollection<FileViewPair> All3DViewPairs { get; }
            = new ObservableCollection<FileViewPair>();

        public ICommand BrowseFilesCommand { get; }
        public ICommand ConfirmSelectionCommand { get; }
        public ICommand AddCloudFilesCommand { get; }

        public bool UserConfirmed { get; private set; } = false;

        public FilePickerViewModel(Application app)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            BrowseFilesCommand = new RelayCommand(_ => BrowseFiles());
            AddCloudFilesCommand = new RelayCommand(_ => AddCloudFiles());
            ConfirmSelectionCommand = new RelayCommand(win =>
            {
                if (win is Window w)
                {
                    UserConfirmed = true;
                    w.DialogResult = true;
                }
            });
        }

        void AddCloudFiles()
        {
            Debug.WriteLine("[BIManage.Addons.BulkExport] AddCloudFiles: entered");
            try
            {
                var explorer = new BulkExportCloudPicker();
                if (explorer.ShowDialog() != true)
                {
                    Debug.WriteLine("[BIManage.Addons.BulkExport] AddCloudFiles: explorer cancelled");
                    return;
                }

                var fileNames = explorer.SelectedFileNames;
                var cloudVersions = explorer.SelectedCloudVersions;
                Debug.WriteLine(
                    $"[BIManage.Addons.BulkExport] AddCloudFiles: explorer ok, fileNames={(fileNames?.Count ?? 0)}, versions={(cloudVersions?.Count ?? 0)}");
                if (fileNames == null || fileNames.Count == 0) return;
                if (cloudVersions == null || cloudVersions.Count != fileNames.Count) return;

                var cloudUris = new List<string>(fileNames.Count);
                for (int i = 0; i < fileNames.Count; i++)
                {
                    var v = cloudVersions[i];
                    cloudUris.Add($"cloud://{v.Region}/{v.ProjectGuidRaw}/{v.ModelGuidRaw}");
                }

                var toLoad = cloudUris
                    .Where(uri => !All3DViewPairs.Any(vp =>
                        vp.FilePath.Equals(uri, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (toLoad.Count > 0)
                {
                    Debug.WriteLine(
                        $"[BIManage.Addons.BulkExport] AddCloudFiles: calling Load3DViews on {toLoad.Count} new URIs");
                    Load3DViews(toLoad);
                    Debug.WriteLine(
                        "[BIManage.Addons.BulkExport] AddCloudFiles: Load3DViews returned");
                }

                for (int i = 0; i < cloudUris.Count; i++)
                {
                    var uri = cloudUris[i];
                    var friendly = (i < fileNames.Count) ? fileNames[i] : Path.GetFileName(uri);
                    foreach (var pair in All3DViewPairs
                        .Where(p => string.Equals(p.FilePath, uri, StringComparison.OrdinalIgnoreCase)))
                    {
                        pair.FileName = friendly;
                    }
                    SelectedFiles.Remove(uri);
                }

                foreach (var name in fileNames)
                {
                    if (!SelectedFiles.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        SelectedFiles.Add(name);
                }

                OnPropertyChanged(nameof(SelectedFiles));
                OnPropertyChanged(nameof(All3DViewPairs));
                Debug.WriteLine("[BIManage.Addons.BulkExport] AddCloudFiles: complete");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BIManage.Addons.BulkExport] AddCloudFiles: caught " + ex);
                ZeMessageBox.Show("Add Cloud Files", $"Failed to add cloud files:\n{ex.Message}");
            }
        }

        void BrowseFiles()
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Revit Files|*.rvt",
                Multiselect = true,
                Title = "Select Revit Files"
            };

            if (dlg.ShowDialog() == true)
            {
                foreach (var f in dlg.FileNames)
                    if (!SelectedFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                        SelectedFiles.Add(f);

                var toLoad = dlg.FileNames
                    .Where(f => !All3DViewPairs
                        .Any(vp => vp.FilePath.Equals(f, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                Load3DViews(toLoad);
                OnPropertyChanged(nameof(SelectedFiles));
                OnPropertyChanged(nameof(All3DViewPairs));
            }
        }

        public void Load3DViews(IEnumerable<string> files)
        {
            foreach (var filePath in files)
            {
                if (string.IsNullOrWhiteSpace(filePath)) continue;

                if (!SelectedFiles.Any(s => s.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
                    SelectedFiles.Add(filePath);

                if (All3DViewPairs.Any(vp => vp.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
                    continue;

                try
                {
                    using var doc = OpenDocumentForPath(filePath);
                    if (doc == null) continue;

                    var views = new FilteredElementCollector(doc)
                        .OfClass(typeof(View3D))
                        .Cast<View3D>()
                        .Where(v => !v.IsTemplate);

                    foreach (var v in views)
                    {
                        if (!All3DViewPairs.Any(e =>
                            e.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) &&
                            e.ViewName == v.Name))
                        {
                            All3DViewPairs.Add(new FileViewPair
                            {
                                FileName = Path.GetFileName(filePath),
                                FilePath = filePath,
                                ViewName = v.Name,
                                IsSelected = false
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    ZeMessageBox.Show("Export Error",
                        $"Error loading views from file {filePath}:\n{ex.Message}");
                }
            }

            OnPropertyChanged(nameof(SelectedFiles));
            OnPropertyChanged(nameof(All3DViewPairs));
        }

        Document OpenDocumentForPath(string filePath)
        {
            if (filePath.StartsWith("cloud://", StringComparison.OrdinalIgnoreCase))
            {
                string suffix = filePath.Substring("cloud://".Length);
                var parts = suffix.Split('/');
                if (parts.Length >= 3)
                {
                    var region = parts[0];
                    var projectGuid = Guid.Parse(parts[1]);
                    var modelGuid = Guid.Parse(parts[2]);
                    var regionConst = region.Equals("EMEA", StringComparison.OrdinalIgnoreCase)
                        ? "EMEA"
                        : region.StartsWith("AP", StringComparison.OrdinalIgnoreCase)
                            ? "AP"
                            : "US";

                    var modelPath = ModelPathUtils.ConvertCloudGUIDsToCloudPath(regionConst, projectGuid, modelGuid);
                    var opts = new OpenOptions { Audit = false, AllowOpeningLocalByWrongUser = true };
                    return _app.OpenDocumentFile(modelPath, opts, new OpenFromCloudCallbackLocal());
                }
                else
                {
                    var opts = new OpenOptions
                    {
                        Audit = false,
                        DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets,
                        AllowOpeningLocalByWrongUser = true
                    };
                    var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                    return _app.OpenDocumentFile(modelPath, opts);
                }
            }
            else
            {
                var opts = new OpenOptions
                {
                    Audit = false,
                    DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets,
                    AllowOpeningLocalByWrongUser = true
                };
                var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                return _app.OpenDocumentFile(modelPath, opts);
            }
        }

        private class OpenFromCloudCallbackLocal : IOpenFromCloudCallback
        {
            public OpenConflictResult OnOpenConflict(OpenConflictScenario scenario)
            {
                return scenario switch
                {
                    OpenConflictScenario.OutOfDate => OpenConflictResult.KeepLocalChanges,
                    OpenConflictScenario.VersionArchived => OpenConflictResult.DiscardLocalChangesAndOpenLatestVersion,
                    OpenConflictScenario.Relinquished or OpenConflictScenario.Rollback => OpenConflictResult.DetachFromCentral,
                    _ => OpenConflictResult.Cancel
                };
            }
        }

        public List<FileViewPair> GetSelectedFileViewPairs()
            => All3DViewPairs.Where(p => p.IsSelected).ToList();

        public event PropertyChangedEventHandler PropertyChanged;
        void OnPropertyChanged(string n)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
