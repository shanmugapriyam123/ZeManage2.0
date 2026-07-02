using Autodesk.Revit.UI;
using BIManage.Addons.Models;
using BIManage.Addons.Services;
using BIManage.Addons.Views.LinkRemapper;
using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using Application = Autodesk.Revit.ApplicationServices.Application;

namespace BIManage.Addons.ViewModels.LinkRemapper
{
    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Predicate<object> _canExecute;

        public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) =>
            _canExecute == null || _canExecute(parameter);

        public void Execute(object parameter) =>
            _execute(parameter);

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }

    public class RevitLinksViewModel : INotifyPropertyChanged
    {
        private string _searchText = string.Empty;
        private ICollectionView _filteredRevitLinks;
        private bool _isAllSelected;

        public IReadOnlyDictionary<string, ApsVersion> SelectedMap { get; private set; }
        public IReadOnlyList<ApsVersion> SelectedCloudVersions { get; private set; }

        public ObservableCollection<RevitLinkItem> RevitLinks { get; }

        public bool IsAllSelected
        {
            get => _isAllSelected;
            set
            {
                _isAllSelected = value;
                OnPropertyChanged(nameof(IsAllSelected));

                foreach (var link in RevitLinks)
                {
                    var originalCallback = link._onSelectionChanged;
                    link._onSelectionChanged = null;
                    link.IsSelected = value;
                    link._onSelectionChanged = originalCallback;
                }

                OnPropertyChanged(nameof(CanApplyChanges));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value;
                OnPropertyChanged(nameof(SearchText));
                ApplyFilter();
            }
        }

        public ICollectionView FilteredRevitLinks
        {
            get => _filteredRevitLinks;
            private set
            {
                _filteredRevitLinks = value;
                OnPropertyChanged(nameof(FilteredRevitLinks));
            }
        }

        public int FilteredItemsCount
        {
            get
            {
                var view = FilteredRevitLinks?.Cast<object>();
                return view?.Count() ?? 0;
            }
        }

        public int TotalItemsCount => RevitLinks.Count;

        public bool CanApplyChanges =>
            RevitLinks.Any(link => link.IsSelected && !string.IsNullOrEmpty(link.NewFilePath));

        public ICommand BrowseLocalCommand { get; }
        public ICommand BrowseCloudCommand { get; }
        public ICommand OkCommand { get; }
        public ICommand ClearSearchCommand { get; }

        public bool? DialogResult { get; private set; }

        public RevitLinksViewModel(ObservableCollection<RevitLinkItem> links)
        {
            RevitLinks = links;

            FilteredRevitLinks = CollectionViewSource.GetDefaultView(RevitLinks);

            RevitLinks.CollectionChanged += OnLinksChanged;
            foreach (var item in RevitLinks)
            {
                item.PropertyChanged += OnItemPropertyChanged;
                item._onSelectionChanged = OnLinkSelectionChanged;
            }

            BrowseLocalCommand = new RelayCommand(_ => BrowseLocal(),
                                                  _ => RevitLinks.Any(x => x.IsSelected));
            BrowseCloudCommand = new RelayCommand(_ => BrowseCloud(),
                                                  _ => RevitLinks.Any(x => x.IsSelected));
            ClearSearchCommand = new RelayCommand(_ => ClearSearch());

            OkCommand = new RelayCommand(_ =>
            {
                DialogResult = true;
                OnPropertyChanged(nameof(DialogResult));
            }, _ => CanApplyChanges);

            ApplyFilter();
            UpdateMasterCheckboxState();
        }

        private void OnLinkSelectionChanged(bool isSelected)
        {
            UpdateMasterCheckboxState();
            OnPropertyChanged(nameof(CanApplyChanges));
            CommandManager.InvalidateRequerySuggested();
        }

        private void UpdateMasterCheckboxState()
        {
            if (!RevitLinks.Any())
            {
                _isAllSelected = false;
                OnPropertyChanged(nameof(IsAllSelected));
                return;
            }

            bool allSelected = RevitLinks.All(link => link.IsSelected);
            _isAllSelected = allSelected;
            OnPropertyChanged(nameof(IsAllSelected));
        }

        private void ApplyFilter()
        {
            if (FilteredRevitLinks != null)
            {
                FilteredRevitLinks.Filter = FilterLinks;
                OnPropertyChanged(nameof(FilteredItemsCount));
                OnPropertyChanged(nameof(TotalItemsCount));
            }
        }

        private bool FilterLinks(object item)
        {
            if (item is RevitLinkItem link)
            {
                if (string.IsNullOrWhiteSpace(SearchText))
                    return true;

                var searchTermLower = SearchText.ToLowerInvariant();

                return (link.LinkName?.ToLowerInvariant().Contains(searchTermLower) == true) ||
                       (link.CurrentFilePath?.ToLowerInvariant().Contains(searchTermLower) == true) ||
                       (link.NewFilePath?.ToLowerInvariant().Contains(searchTermLower) == true);
            }
            return true;
        }

        private void ClearSearch()
        {
            SearchText = string.Empty;
        }

        private void OnLinksChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (RevitLinkItem oldItem in e.OldItems)
                {
                    oldItem.PropertyChanged -= OnItemPropertyChanged;
                    oldItem._onSelectionChanged = null;
                }
            }

            if (e.NewItems != null)
            {
                foreach (RevitLinkItem newItem in e.NewItems)
                {
                    newItem.PropertyChanged += OnItemPropertyChanged;
                    newItem._onSelectionChanged = OnLinkSelectionChanged;
                }
            }

            OnPropertyChanged(nameof(TotalItemsCount));
            OnPropertyChanged(nameof(FilteredItemsCount));
            OnPropertyChanged(nameof(CanApplyChanges));
            CommandManager.InvalidateRequerySuggested();
            UpdateMasterCheckboxState();
        }

        private void OnItemPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RevitLinkItem.IsSelected) ||
                e.PropertyName == nameof(RevitLinkItem.NewFilePath))
            {
                OnPropertyChanged(nameof(CanApplyChanges));
                CommandManager.InvalidateRequerySuggested();
            }

            if (e.PropertyName == nameof(RevitLinkItem.NewFilePath))
            {
                ApplyFilter();
            }
        }

        private void BrowseLocal()
        {
            try
            {
                using var dlg = new CommonOpenFileDialog()
                {
                    IsFolderPicker = true,
                    Multiselect = true,
                    Title = "Select folder(s) containing the .rvt link files"
                };

                if (dlg.ShowDialog() != CommonFileDialogResult.Ok)
                    return;

                var selectedFolders = dlg.FileNames.ToList();
                if (!selectedFolders.Any())
                    return;

                var byFullName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var byBaseName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var folder in selectedFolders)
                {
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(folder, "*.rvt", SearchOption.TopDirectoryOnly))
                        {
                            var full = Path.GetFileName(f);
                            var basn = Path.GetFileNameWithoutExtension(f);
                            byFullName[full] = f;
                            byBaseName[basn] = f;
                        }
                    }
                    catch (Exception ex)
                    {
                        Views.Common.ZeMessageBox.Show("Link Remapper - Warning", $"Could not access folder: {folder}\nError: {ex.Message}");
                    }
                }

                if (!byFullName.Any())
                {
                    Views.Common.ZeMessageBox.Show("Link Remapper - Info", "No Revit (.rvt) files were found in any of the selected folders.");
                    return;
                }

                int matched = 0;
                var unmatched = new List<string>();

                foreach (var link in RevitLinks.Where(x => x.IsSelected))
                {
                    link.NewFilePaths.Clear();
                    var local = ResolveLocalRvtForLink(link, byFullName, byBaseName);
                    if (local != null)
                    {
                        link.NewFilePaths.Add(local);
                        matched++;
                    }
                    else
                    {
                        unmatched.Add(link.LinkName);
                    }
                }

                if (matched == 0)
                {
                    Views.Common.ZeMessageBox.Show("Browse Local",
                        "Found .rvt files in the selected folder(s), but none matched the names of the selected links.\n\n" +
                        "Expected names: " + string.Join(", ", unmatched) + "\n\n" +
                        "Tip: the matcher tries the link's Name, the link's current filename, and any '.rvt' variation. " +
                        "If your links have been renamed away from their original filenames, rename either the link or the file on disk so they line up.");
                }
                else if (unmatched.Any())
                {
                    Views.Common.ZeMessageBox.Show("Browse Local",
                        $"Matched {matched} of {matched + unmatched.Count} selected links.\n\n" +
                        $"No match found for: {string.Join(", ", unmatched)}");
                }
            }
            catch (Exception ex)
            {
                Views.Common.ZeMessageBox.Show("Browse Local failed",
                    "An error occurred while picking a folder.\n\n" +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string ResolveLocalRvtForLink(
            RevitLinkItem link,
            IReadOnlyDictionary<string, string> byFullName,
            IReadOnlyDictionary<string, string> byBaseName)
        {
            if (link == null) return null;

            var name = link.LinkName;
            if (!string.IsNullOrEmpty(name))
            {
                if (byFullName.TryGetValue(name, out var hit1)) return hit1;
                if (byFullName.TryGetValue(name + ".rvt", out var hit2)) return hit2;
                if (byBaseName.TryGetValue(name, out var hit3)) return hit3;
            }

            var current = link.CurrentFilePath;
            if (!string.IsNullOrEmpty(current))
            {
                string tail;
                try { tail = Path.GetFileName(current); }
                catch { tail = current; }

                if (!string.IsNullOrEmpty(tail))
                {
                    if (byFullName.TryGetValue(tail, out var hit4)) return hit4;
                    var tailBase = Path.GetFileNameWithoutExtension(tail);
                    if (!string.IsNullOrEmpty(tailBase) &&
                        byBaseName.TryGetValue(tailBase, out var hit5)) return hit5;
                }
            }

            return null;
        }

        private Result BrowseCloud()
        {
            if (!IsUserAuthenticated())
            {
                Views.Common.ZeMessageBox.Show("Link Remapper - Error", "Please sign in to Revit to access cloud files.");
                return Result.Failed;
            }

            var selectedLinks = RevitLinks.Where(x => x.IsSelected).ToList();
            if (selectedLinks.Count == 0)
                return Result.Failed;

            var explorer = new LinkRemapperExplorer();
            if (explorer.ShowDialog() != true)
                return Result.Failed;

            SelectedMap = explorer.SelectedMap;
            SelectedCloudVersions = explorer.SelectedCloudVersions;

            var lookup = new Dictionary<string, ApsVersion>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in SelectedMap)
            {
                lookup[kv.Key] = kv.Value;
                if (kv.Key.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                    lookup[Path.GetFileNameWithoutExtension(kv.Key)] = kv.Value;
            }

            int applied = 0;
            foreach (var link in selectedLinks)
            {
                if (!lookup.TryGetValue(link.LinkName, out var v))
                    continue;

                link.NewFilePaths.Clear();
                link.NewFilePaths.Add($"cloud://{v.Region}/{v.ProjectGuidRaw}/{v.ModelGuidRaw}");
                applied++;
            }

            return applied > 0 ? Result.Succeeded : Result.Failed;
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

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
