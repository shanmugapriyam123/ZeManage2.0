using BIManage.Addons.Helpers;
using BIManage.Addons.Models;
using BIManage.Addons.Services;
using BIManage.Addons.Views.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ApsVersion = BIManage.Addons.Models.ApsVersion;

namespace BIManage.Addons.Views.NwcExport
{
    public partial class BulkExportCloudPicker : Window
    {
        private readonly List<ApsVersion> _pendingSelections = new();
        private readonly List<string> _pendingNames = new();

        public IReadOnlyList<string> SelectedFileNames { get; private set; }
        public IReadOnlyList<ApsVersion> SelectedCloudVersions { get; private set; }
        public IReadOnlyDictionary<string, ApsVersion> SelectedMap { get; private set; }

        public BulkExportCloudPicker()
        {
            InitializeComponent();

            IconHelper.SetWindowIcon(this);
            HeaderLogo.Source = IconHelper.GetLogoBitmapImage();

            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };

            this.OwnByRevit();

            _tree.AddHandler(TreeViewItem.ExpandedEvent,
                             new RoutedEventHandler(TreeItem_Expanded));
            _tree.SelectedItemChanged += Tree_SelectedItemChanged;
        }

        private async void ExplorerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadRootNodes();
        }

        private async Task LoadRootNodes()
        {
            try
            {
                var hubs = await Task.Run(DataService.GetHubs);
                _tree.Items.Clear();

                foreach (var hub in hubs)
                {
                    var tvi = new TreeViewItem { Header = hub.Name, Tag = hub };
                    tvi.Items.Add(null);
                    _tree.Items.Add(tvi);
                }
            }
            catch (Exception ex)
            {
                ZeMessageBox.Show("Batch NWC Export", $"Failed to load hubs:\n{ex.Message}");
            }
        }

        private static string FindHubRegion(ItemsControl node)
        {
            while (node != null)
            {
                if (node is TreeViewItem tvi && tvi.Tag is Hub hub)
                    return hub.Region;
                node = ItemsControl.ItemsControlFromItemContainer(node as DependencyObject);
            }
            return "US";
        }

        private async void TreeItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not TreeViewItem node ||
                node.Items.Count != 1 ||
                node.Items[0] != null) return;

            node.Items.Clear();

            switch (node.Tag)
            {
                case Hub hub:
                    foreach (var proj in await Task.Run(() => DataService.GetProjects(hub.Id)))
                    {
                        proj.Region = hub.Region;
                        var child = new TreeViewItem { Header = proj.Name, Tag = proj };
                        child.Items.Add(null);
                        node.Items.Add(child);
                    }
                    break;

                case Project proj:
                    foreach (var folder in await Task.Run(() =>
                             DataService.GetTopFolders(proj.HubId, proj.Id)))
                    {
                        var child = new TreeViewItem { Header = folder.Name, Tag = folder };
                        child.Items.Add(null);
                        node.Items.Add(child);
                    }
                    break;

                case Folder folder:
                    var subFolders = new List<Folder>();
                    var items = new List<Item>();

                    await Task.Run(() =>
                        DataService.GetFolderContents(folder.ProjectId, folder.Id,
                                                      out subFolders, out items));

                    foreach (var sf in subFolders)
                    {
                        var child = new TreeViewItem { Header = sf.Name, Tag = sf };
                        child.Items.Add(null);
                        node.Items.Add(child);
                    }
                    foreach (var it in items)
                        node.Items.Add(new TreeViewItem { Header = it.Name, Tag = it });
                    break;
            }
        }

        private void Tree_SelectedItemChanged(object sender,
                                              RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not TreeViewItem node) return;

            void Queue(Item item)
            {
                if (_pendingSelections.Any(v => v.Id == item.Id)) return;

                var version = DataService.GetItemVersions(item.ProjectId, item.Id)
                                           .LastOrDefault(v =>
                                               !string.IsNullOrEmpty(v.ProjectGuidRaw) &&
                                               !string.IsNullOrEmpty(v.ModelGuidRaw));
                if (version == null) return;

                version.Region = FindHubRegion(node);
                _pendingSelections.Add(version);
                _pendingNames.Add(item.Name);
                _preview.Items.Add(item.Name);
                _okButton.IsEnabled = true;
            }

            switch (node.Tag)
            {
                case Item itm when itm.Name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase):
                    Queue(itm);
                    break;

                case Folder fld:
                    DataService.GetFolderContents(fld.ProjectId, fld.Id,
                                                  out var _, out var items);
                    foreach (var revit in items.Where(i =>
                               i.Name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)))
                        Queue(revit);
                    break;
            }
        }

        private void ExplorerWindow_OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingSelections.Count == 0)
            {
                ZeMessageBox.Show("Batch NWC Export", "Select at least one file.");
                return;
            }

            SelectedFileNames = _pendingNames.ToList();
            SelectedCloudVersions = _pendingSelections.ToList();
            SelectedMap = _pendingNames
                                    .Zip(_pendingSelections, (n, v) => new { n, v })
                                    .ToDictionary(x => x.n, x => x.v);

            DialogResult = true;
            Close();
        }

        private void RemoveSelected_MenuItem_Click(object sender, RoutedEventArgs e)
            => RemoveSelectedItems();

        private void Preview_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete &&
                Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                RemoveSelectedItems();
        }

        private void RemoveSelectedItems()
        {
            var toRemove = _preview.SelectedItems.Cast<string>().ToList();
            if (!toRemove.Any()) return;

            foreach (var name in toRemove)
            {
                var idx = _pendingNames.IndexOf(name);
                if (idx >= 0)
                {
                    _pendingNames.RemoveAt(idx);
                    _pendingSelections.RemoveAt(idx);
                }
                _preview.Items.Remove(name);
            }

            _okButton.IsEnabled = _pendingSelections.Count > 0;
        }
    }
}
