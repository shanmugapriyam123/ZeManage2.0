using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Addons.Services;
using BIManage.Addons.ViewModels.LinkRemapper;
using BIManage.Addons.Views.Common;
using BIManage.Addons.Views.NwcExport;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using BIManage.Addons.Models;
using Application = Autodesk.Revit.ApplicationServices.Application;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace BIManage.Addons.ViewModels.NwcExport
{
    public class MainViewModel : INotifyPropertyChanged
    {
        readonly Application _app;
        List<FileViewPair> _selectedFileViewPairs = new();

        public ObservableCollection<RevitLinkItem> RevitLinks { get; }
        public IReadOnlyDictionary<string, ApsVersion> SelectedMap { get; private set; }
        public IReadOnlyList<ApsVersion> SelectedCloudVersions { get; private set; }

        public ICommand LocalFileCommand { get; }
        public ICommand CloudFileCommand { get; }
        public ICommand BrowseExportFolderCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand ImportConfigCommand { get; }
        public ICommand ExportConfigCommand { get; }

        public bool ConvertElementProperties { get; set; } = false;
        public bool ConvertLights { get; set; } = false;
        public bool ConvertLinkedCADFormats { get; set; } = true;
        public bool DivideFileIntoLevels { get; set; } = true;
        public bool ExportElementIds { get; set; } = true;
        public bool ExportLinks { get; set; } = false;
        public bool ExportParts { get; set; } = false;
        public bool ExportRoomAsAttribute { get; set; } = true;
        public bool ExportRoomGeometry { get; set; } = true;
        public bool ExportUrls { get; set; } = true;
        public bool FindMissingMaterials { get; set; } = true;
        public bool ExportInternalCoordinates { get; set; } = false;

        private int _facetingFactor = 1;
        public int FacetingFactor
        {
            get => _facetingFactor;
            set
            {
                var newValue = Math.Max(1, value);
                if (_facetingFactor != newValue)
                {
                    _facetingFactor = newValue;
                    OnPropertyChanged(nameof(FacetingFactor));
                }
            }
        }

        private string _exportFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        public string ExportFolderPath
        {
            get => _exportFolderPath;
            set
            {
                if (_exportFolderPath != value)
                {
                    _exportFolderPath = value;
                    OnPropertyChanged(nameof(ExportFolderPath));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private bool _hasSelectedViews;
        public bool HasSelectedViews
        {
            get => _hasSelectedViews;
            set
            {
                if (_hasSelectedViews == value) return;
                _hasSelectedViews = value;
                OnPropertyChanged(nameof(HasSelectedViews));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public MainViewModel(Application app)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            RevitLinks = new ObservableCollection<RevitLinkItem>();

            LocalFileCommand = new RelayCommand(_ => PickFiles());
            CloudFileCommand = new RelayCommand(_ => ExportCloudFiles());
            BrowseExportFolderCommand = new RelayCommand(_ => BrowseExportFolder());

            ExportCommand = new RelayCommand(
                _ =>
                {
                    if (!HasSelectedViews)
                    {
                        ZeMessageBox.Show("Export", "No views selected. Please select files and views first.");
                        return;
                    }

                    if (_selectedFileViewPairs != null && _selectedFileViewPairs.Any())
                    {
                        ExportLocalSelectedViews();
                        return;
                    }

                    ZeMessageBox.Show("Export", "Nothing to export. Please select local views first, or use the Cloud flow.");
                },
                _ => HasSelectedViews
            );

            ImportConfigCommand = new RelayCommand(_ => ImportConfig());
            ExportConfigCommand = new RelayCommand(_ => ExportConfig());

            HasSelectedViews = false;
        }

        private void ExportConfig()
        {
            Debug.WriteLine("[BIManage.Addons.BulkExport] ExportConfig: entered");
            try
            {
                var dlg = new SaveFileDialog
                {
                    Title = "Save Batch NWC Export Settings",
                    Filter = "ZeConnect preset (*.ze)|*.ze",
                    FileName = "BatchNwcExportConfig.ze",
                    DefaultExt = ".ze"
                };

                if (dlg.ShowDialog() != true) return;
                Debug.WriteLine(
                    $"[BIManage.Addons.BulkExport] ExportConfig: dialog ok, target='{dlg.FileName}', selectedPairs={(_selectedFileViewPairs?.Count ?? 0)}");

                var config = new BatchNwcExportConfig
                {
                    SelectedFileViewPairs = (_selectedFileViewPairs ?? new List<FileViewPair>())
                        .Select(p => new FileViewPairDto
                        {
                            FileName = p.FileName,
                            FilePath = p.FilePath,
                            ViewName = p.ViewName,
                            IsSelected = p.IsSelected
                        })
                        .ToList(),
                    SelectedFiles = (_selectedFileViewPairs ?? new List<FileViewPair>())
                        .Select(p => p.FilePath)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    ExportFolderPath = ExportFolderPath,
                    ConvertElementProperties = ConvertElementProperties,
                    ConvertLights = ConvertLights,
                    ConvertLinkedCADFormats = ConvertLinkedCADFormats,
                    DivideFileIntoLevels = DivideFileIntoLevels,
                    ExportElementIds = ExportElementIds,
                    ExportLinks = ExportLinks,
                    ExportParts = ExportParts,
                    ExportRoomAsAttribute = ExportRoomAsAttribute,
                    ExportRoomGeometry = ExportRoomGeometry,
                    ExportUrls = ExportUrls,
                    FindMissingMaterials = FindMissingMaterials,
                    ExportInternalCoordinates = ExportInternalCoordinates,
                    FacetingFactor = FacetingFactor
                };

                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(dlg.FileName, json);
                Debug.WriteLine(
                    $"[BIManage.Addons.BulkExport] ExportConfig: wrote {json.Length} chars to '{dlg.FileName}'");

                ZeMessageBox.Show("Export Settings", $"Settings saved to:\n{dlg.FileName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BIManage.Addons.BulkExport] ExportConfig: caught " + ex);
                ZeMessageBox.Show("Export Settings Error", $"Failed to save settings:\n{ex.Message}");
            }
        }

        private void ImportConfig()
        {
            Debug.WriteLine("[BIManage.Addons.BulkExport] ImportConfig: entered");
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Load Batch NWC Export Settings",
                    Filter = "ZeConnect preset (*.ze)|*.ze|JSON files (*.json)|*.json"
                };

                if (dlg.ShowDialog() != true) return;
                Debug.WriteLine(
                    $"[BIManage.Addons.BulkExport] ImportConfig: dialog ok, source='{dlg.FileName}'");

                var json = File.ReadAllText(dlg.FileName);
                var config = JsonConvert.DeserializeObject<BatchNwcExportConfig>(json);
                if (config == null)
                {
                    Debug.WriteLine("[BIManage.Addons.BulkExport] ImportConfig: deserialize returned null");
                    ZeMessageBox.Show("Import Settings", "The selected file did not contain valid settings.");
                    return;
                }
                Debug.WriteLine(
                    $"[BIManage.Addons.BulkExport] ImportConfig: deserialized — savedPairs={(config.SelectedFileViewPairs?.Count ?? 0)}, folder='{config.ExportFolderPath}'");

                if (!string.IsNullOrEmpty(config.ExportFolderPath))
                    ExportFolderPath = config.ExportFolderPath;

                ConvertElementProperties = config.ConvertElementProperties;
                ConvertLights = config.ConvertLights;
                ConvertLinkedCADFormats = config.ConvertLinkedCADFormats;
                DivideFileIntoLevels = config.DivideFileIntoLevels;
                ExportElementIds = config.ExportElementIds;
                ExportLinks = config.ExportLinks;
                ExportParts = config.ExportParts;
                ExportRoomAsAttribute = config.ExportRoomAsAttribute;
                ExportRoomGeometry = config.ExportRoomGeometry;
                ExportUrls = config.ExportUrls;
                FindMissingMaterials = config.FindMissingMaterials;
                ExportInternalCoordinates = config.ExportInternalCoordinates;
                FacetingFactor = config.FacetingFactor > 0 ? config.FacetingFactor : 1;

                foreach (var prop in new[]
                {
                    nameof(ExportFolderPath),
                    nameof(ConvertElementProperties), nameof(ConvertLights),
                    nameof(ConvertLinkedCADFormats), nameof(DivideFileIntoLevels),
                    nameof(ExportElementIds), nameof(ExportLinks), nameof(ExportParts),
                    nameof(ExportRoomAsAttribute), nameof(ExportRoomGeometry),
                    nameof(ExportUrls), nameof(FindMissingMaterials),
                    nameof(ExportInternalCoordinates), nameof(FacetingFactor)
                })
                {
                    OnPropertyChanged(prop);
                }

                var savedPairs = config.SelectedFileViewPairs ?? new List<FileViewPairDto>();
                var filePaths = savedPairs
                    .Select(p => p.FilePath)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (filePaths.Count == 0)
                {
                    Debug.WriteLine("[BIManage.Addons.BulkExport] ImportConfig: no file paths in preset, options-only import");
                    ZeMessageBox.Show("Import Settings",
                        "Options restored. The preset did not contain any file/view selections.");
                    return;
                }

                Debug.WriteLine(
                    $"[BIManage.Addons.BulkExport] ImportConfig: calling Load3DViews on {filePaths.Count} paths");
                var vm = new FilePickerViewModel(_app);
                vm.Load3DViews(filePaths);
                Debug.WriteLine(
                    $"[BIManage.Addons.BulkExport] ImportConfig: Load3DViews done, {vm.All3DViewPairs.Count} pairs loaded");

                var savedKeys = new HashSet<string>(
                    savedPairs.Select(p => MakeViewKey(p.FilePath, p.ViewName)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var pair in vm.All3DViewPairs)
                {
                    pair.IsSelected = savedKeys.Contains(MakeViewKey(pair.FilePath, pair.ViewName));
                }

                foreach (var saved in savedPairs)
                {
                    if (string.IsNullOrEmpty(saved.FilePath) || string.IsNullOrEmpty(saved.FileName))
                        continue;
                    foreach (var pair in vm.All3DViewPairs
                        .Where(p => string.Equals(p.FilePath, saved.FilePath,
                                                  StringComparison.OrdinalIgnoreCase)))
                    {
                        pair.FileName = saved.FileName;
                    }
                }

                vm.SelectedFiles.Clear();
                foreach (var name in savedPairs
                    .Select(p => p.FileName)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    vm.SelectedFiles.Add(name);
                }

                var window = new BulkExportFilePickerWindow
                {
                    DataContext = vm,
                    ShowBrowseButton = false,
                    ShowImportSourceButtons = true
                };
                Debug.WriteLine("[BIManage.Addons.BulkExport] ImportConfig: showing picker");
                if (window.ShowDialog() == true)
                {
                    _selectedFileViewPairs = vm.GetSelectedFileViewPairs();
                    HasSelectedViews = _selectedFileViewPairs != null && _selectedFileViewPairs.Any();
                    CommandManager.InvalidateRequerySuggested();
                    Debug.WriteLine(
                        $"[BIManage.Addons.BulkExport] ImportConfig: picker OK, captured {_selectedFileViewPairs?.Count ?? 0} pairs");
                }
                else
                {
                    Debug.WriteLine("[BIManage.Addons.BulkExport] ImportConfig: picker cancelled");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BIManage.Addons.BulkExport] ImportConfig: caught " + ex);
                ZeMessageBox.Show("Import Settings Error", $"Failed to load settings:\n{ex.Message}");
            }
        }

        private static string MakeViewKey(string filePath, string viewName)
            => $"{(filePath ?? string.Empty)}||{(viewName ?? string.Empty)}";

        private void BrowseExportFolder()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Folder to Save NWC Files",
                Filter = "Folder Selection|*.folder",
                FileName = "Select This Folder",
                CheckFileExists = false
            };

            bool? result = dlg.ShowDialog();
            if (result == true)
            {
                string folderPath = Path.GetDirectoryName(dlg.FileName);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    ExportFolderPath = folderPath;
                }
            }
        }

        private Result ExportCloudFiles()
        {
            if (!IsUserAuthenticated())
            {
                ZeMessageBox.Show("Export Error", "Please sign in to Revit to access cloud files.");
                return Result.Failed;
            }

            var explorer = new BulkExportCloudPicker();
            if (explorer.ShowDialog() != true)
                return Result.Failed;

            var fileNames = explorer.SelectedFileNames;
            var cloudVersions = explorer.SelectedCloudVersions;
            if (fileNames == null || !fileNames.Any()) return Result.Failed;
            if (cloudVersions == null || cloudVersions.Count != fileNames.Count)
                throw new InvalidOperationException(
                    "SelectedFileNames and SelectedCloudVersions count mismatch.");

            var cloudUris = new List<string>(fileNames.Count);
            for (int i = 0; i < fileNames.Count; i++)
            {
                var v = cloudVersions[i];
                cloudUris.Add($"cloud://{v.Region}/{v.ProjectGuidRaw}/{v.ModelGuidRaw}");
            }

            var vm = new FilePickerViewModel(_app);
            vm.Load3DViews(cloudUris);

            vm.SelectedFiles.Clear();
            foreach (var name in fileNames)
                vm.SelectedFiles.Add(name);

            for (int i = 0; i < cloudUris.Count; i++)
            {
                var uri = cloudUris[i];
                var friendly = (i < fileNames.Count) ? fileNames[i] : Path.GetFileName(uri);
                foreach (var pair in vm.All3DViewPairs.Where(p => p.FilePath.Equals(uri, StringComparison.OrdinalIgnoreCase)))
                    pair.FileName = friendly;
            }

            var window = new BulkExportFilePickerWindow { DataContext = vm, ShowBrowseButton = false };
            if (window.ShowDialog() == true)
                _selectedFileViewPairs = vm.GetSelectedFileViewPairs();

            HasSelectedViews = _selectedFileViewPairs != null && _selectedFileViewPairs.Any();
            CommandManager.InvalidateRequerySuggested();

            return Result.Succeeded;
        }

        private void PickFiles()
        {
            var vm = new FilePickerViewModel(_app);
            var window = new BulkExportFilePickerWindow { DataContext = vm };

            if (window.ShowDialog() == true)
                _selectedFileViewPairs = vm.GetSelectedFileViewPairs();

            HasSelectedViews = _selectedFileViewPairs != null && _selectedFileViewPairs.Any();
            CommandManager.InvalidateRequerySuggested();
        }

        private bool IsUserAuthenticated()
        {
            if (!Application.IsLoggedIn)
                return false;

            try
            {
                _ = RevitSsoTokenProvider.GetAccessToken();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ExportLocalSelectedViews()
        {
            int total = 0;

            foreach (var pair in _selectedFileViewPairs)
            {
                try
                {
                    Document doc = null;

                    if (!string.IsNullOrEmpty(pair.FilePath) &&
                        pair.FilePath.StartsWith("cloud://", StringComparison.OrdinalIgnoreCase))
                    {
                        string suffix = pair.FilePath.Substring("cloud://".Length);
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
                            doc = OpenCloudModelWithCallback(_app, modelPath);
                        }
                        else
                        {
                            var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(pair.FilePath);
                            doc = _app.OpenDocumentFile(mp, new OpenOptions
                            {
                                Audit = false,
                                DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets,
                                AllowOpeningLocalByWrongUser = true
                            });
                        }
                    }
                    else
                    {
                        var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(pair.FilePath);
                        doc = _app.OpenDocumentFile(mp, new OpenOptions
                        {
                            Audit = false,
                            DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets,
                            AllowOpeningLocalByWrongUser = true
                        });
                    }

                    using (doc)
                    {
                        var view3D = new FilteredElementCollector(doc)
                            .OfClass(typeof(View3D))
                            .Cast<View3D>()
                            .FirstOrDefault(v => !v.IsTemplate && v.Name == pair.ViewName);

                        if (view3D != null)
                        {
                            var nwc = new NavisworksExportOptions
                            {
                                ConvertElementProperties = ConvertElementProperties,
                                ConvertLights = ConvertLights,
                                ConvertLinkedCADFormats = ConvertLinkedCADFormats,
                                Coordinates = ExportInternalCoordinates
                                    ? NavisworksCoordinates.Internal
                                    : NavisworksCoordinates.Shared,
                                DivideFileIntoLevels = DivideFileIntoLevels,
                                ExportElementIds = ExportElementIds,
                                ExportLinks = ExportLinks,
                                ExportParts = ExportParts,
                                ExportRoomAsAttribute = ExportRoomAsAttribute,
                                ExportRoomGeometry = ExportRoomGeometry,
                                ExportUrls = ExportUrls,
                                FindMissingMaterials = FindMissingMaterials,
                                FacetingFactor = (double)FacetingFactor,
                                ExportScope = NavisworksExportScope.View,
                                ViewId = view3D.Id
                            };

                            var baseName = !string.IsNullOrEmpty(pair.FileName)
                                ? Path.GetFileNameWithoutExtension(pair.FileName)
                                : Path.GetFileNameWithoutExtension(pair.FilePath);

                            var safeViewName = string.Concat(pair.ViewName.Split(Path.GetInvalidFileNameChars()));
                            var fileName = $"{baseName}_{safeViewName}.nwc";
                            doc.Export(ExportFolderPath, fileName, nwc);

                            total++;
                        }
                        else
                        {
                            ZeMessageBox.Show("Export Warning",
                                $"View '{pair.ViewName}' not found in '{pair.FileName ?? pair.FilePath}'.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    ZeMessageBox.Show("Export Error",
                        $"Error exporting {pair.ViewName} from {pair.FilePath}:\n{ex.Message}");
                }
            }

            ZeMessageBox.Show("Export Complete",
                $"Exported {total} NWC file(s) to:\n{ExportFolderPath}");
        }

        class OpenFromCloudCallback : IOpenFromCloudCallback
        {
            public OpenConflictResult OnOpenConflict(OpenConflictScenario scenario)
            {
                return scenario switch
                {
                    OpenConflictScenario.OutOfDate => OpenConflictResult.KeepLocalChanges,
                    OpenConflictScenario.VersionArchived => OpenConflictResult.DiscardLocalChangesAndOpenLatestVersion,
                    OpenConflictScenario.Relinquished or
                    OpenConflictScenario.Rollback => OpenConflictResult.DetachFromCentral,
                    _ => OpenConflictResult.Cancel
                };
            }
        }

        static Document OpenCloudModelWithCallback(Application app, ModelPath mp)
        {
            using (BeginEventProtectionSuppression())
                return app.OpenDocumentFile(mp, BuildExportOpenOptions(isCloudModel: true), new OpenFromCloudCallback());
        }

        static string CopyToTempForExport(string sourcePath)
        {
            try
            {
                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                    return null;

                var tempDir = Path.Combine(Path.GetTempPath(), "ZeManageNwcExport");
                Directory.CreateDirectory(tempDir);

                var ext = Path.GetExtension(sourcePath);
                var stem = Path.GetFileNameWithoutExtension(sourcePath);
                var tempPath = Path.Combine(tempDir, $"{stem}_{Guid.NewGuid():N}{ext}");

                File.Copy(sourcePath, tempPath, overwrite: true);
                return tempPath;
            }
            catch
            {
                return null;
            }
        }

        static OpenOptions BuildExportOpenOptions(bool isCloudModel = false)
        {
            var opts = new OpenOptions
            {
                Audit = false,
                AllowOpeningLocalByWrongUser = true
            };

            if (!isCloudModel)
            {
                opts.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
            }

            opts.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
            return opts;
        }

        static IDisposable BeginEventProtectionSuppression()
        {
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "BIManageRevit");
                var type = asm?.GetType("BIManage.Revit.Protection.EventProtectionSuppression");
                var method = type?.GetMethod("BeginScope", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method?.Invoke(null, null) is IDisposable scope)
                    return scope;
            }
            catch { /* fall through to no-op */ }
            return NoOpDisposable.Instance;
        }

        private sealed class NoOpDisposable : IDisposable
        {
            public static readonly NoOpDisposable Instance = new();
            public void Dispose() { }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class BatchNwcExportConfig
    {
        public List<FileViewPairDto> SelectedFileViewPairs { get; set; } = new List<FileViewPairDto>();
        public List<string> SelectedFiles { get; set; } = new List<string>();
        public string ExportFolderPath { get; set; }
        public bool ConvertElementProperties { get; set; }
        public bool ConvertLights { get; set; }
        public bool ConvertLinkedCADFormats { get; set; }
        public bool DivideFileIntoLevels { get; set; }
        public bool ExportElementIds { get; set; }
        public bool ExportLinks { get; set; }
        public bool ExportParts { get; set; }
        public bool ExportRoomAsAttribute { get; set; }
        public bool ExportRoomGeometry { get; set; }
        public bool ExportUrls { get; set; }
        public bool FindMissingMaterials { get; set; }
        public bool ExportInternalCoordinates { get; set; }
        public int FacetingFactor { get; set; } = 1;
    }

    public class FileViewPairDto
    {
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public string ViewName { get; set; }
        public bool IsSelected { get; set; }
    }

    /// <summary>
    /// Serializable subset of the NWC export options — just the 13 checkbox/numeric
    /// fields users tune in the dialog, without the per-run selection state (selected
    /// files, export folder) that's part of <see cref="BatchNwcExportConfig"/>. Used by
    /// the Export Settings / Import Settings buttons so users can save their preferred
    /// option set to a .ze file and reload it on another machine without dragging
    /// per-run file paths around.
    /// </summary>
    public class NwcExportSettings
    {
        public bool ConvertElementProperties { get; set; }
        public bool ConvertLights { get; set; }
        public bool ConvertLinkedCADFormats { get; set; }
        public bool DivideFileIntoLevels { get; set; }
        public bool ExportElementIds { get; set; }
        public bool ExportLinks { get; set; }
        public bool ExportParts { get; set; }
        public bool ExportRoomAsAttribute { get; set; }
        public bool ExportRoomGeometry { get; set; }
        public bool ExportUrls { get; set; }
        public bool FindMissingMaterials { get; set; }
        public bool ExportInternalCoordinates { get; set; }
        public int FacetingFactor { get; set; } = 1;
    }
}
