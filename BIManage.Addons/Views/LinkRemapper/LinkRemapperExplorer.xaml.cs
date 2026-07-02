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

namespace BIManage.Addons.Views.LinkRemapper
{
    public partial class LinkRemapperExplorer : Window
    {
        private readonly List<ApsVersion> _pendingSelections = new();
        private readonly List<string> _pendingNames = new();

        public IReadOnlyList<ApsVersion> SelectedCloudVersions { get; private set; }
        public IReadOnlyDictionary<string, ApsVersion> SelectedMap { get; private set; }

        public LinkRemapperExplorer()
        {
            InitializeComponent();

            IconHelper.SetWindowIcon(this);
            HeaderLogo.Source = IconHelper.GetLogoBitmapImage();

            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };

            this.OwnByRevit();

            Title = "APS Data Explorer (select files)";

            _tree.AddHandler(TreeViewItem.ExpandedEvent,
                             new RoutedEventHandler(TreeItem_Expanded));
            _tree.SelectedItemChanged += Tree_SelectedItemChanged;

            _okButton.IsEnabled = false;
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
                foreach (var h in hubs)
                {
                    var tvi = new TreeViewItem { Header = h.Name, Tag = h };
                    tvi.Items.Add(null);
                    _tree.Items.Add(tvi);
                }
            }
            catch (Exception ex)
            {
                ZeMessageBox.Show("Link Remapper", $"Failed to load hubs:\n{ex.Message}");
            }
        }

        private static string FindHubRegion(ItemsControl node)
        {
            while (node != null)
            {
                if (node is TreeViewItem tvi && tvi.Tag is Hub h)
                    return h.Region;
                node = ItemsControl.ItemsControlFromItemContainer(node as DependencyObject);
            }
            return "US";
        }

        private async void TreeItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is TreeViewItem node) ||
                node.Items.Count != 1 || node.Items[0] != null)
                return;

            node.Items.Clear();

            switch (node.Tag)
            {
                case Hub hub:
                    var projects = await Task.Run(() => DataService.GetProjects(hub.Id));
                    foreach (var p in projects)
                    {
                        p.Region = hub.Region;
                        var child = new TreeViewItem { Header = p.Name, Tag = p };
                        child.Items.Add(null);
                        node.Items.Add(child);
                    }
                    break;

                case Project proj:
                    var folders = await Task.Run(() =>
                        DataService.GetTopFolders(proj.HubId, proj.Id));
                    foreach (var f in folders)
                    {
                        var child = new TreeViewItem { Header = f.Name, Tag = f };
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
                    {
                        node.Items.Add(new TreeViewItem { Header = it.Name, Tag = it });
                    }
                    break;
            }
        }

        private void Tree_SelectedItemChanged(object sender,
                                             RoutedPropertyChangedEventArgs<object> e)
        {
            if (!(e.NewValue is TreeViewItem node))
                return;

            if (node.Tag is Item item)
            {
                AddItemToSelection(item, node);
            }
            else if (node.Tag is Folder folder)
            {
                var subFolders = new List<Folder>();
                var items = new List<Item>();
                DataService.GetFolderContents(folder.ProjectId, folder.Id, out subFolders, out items);

                foreach (var revit in items
                         .Where(it => it.Name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)))
                {
                    AddItemToSelection(revit, node);
                }
            }
        }

        private void AddItemToSelection(Item item, TreeViewItem node)
        {
            var versions = DataService.GetItemVersions(item.ProjectId, item.Id);
            var latest = versions
                .LastOrDefault(v => !string.IsNullOrEmpty(v.ProjectGuidRaw)
                                  && !string.IsNullOrEmpty(v.ModelGuidRaw));
            if (latest == null) return;
            if (_pendingSelections.Any(v => v.Id == latest.Id)) return;

            latest.Region = FindHubRegion(node);
            _pendingSelections.Add(latest);
            _pendingNames.Add(item.Name);

            _preview.Items.Add(item.Name);
            _okButton.IsEnabled = _pendingSelections.Count > 0;
        }

        private void ExplorerWindow_OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingSelections.Count == 0)
            {
                ZeMessageBox.Show("Link Remapper", "Please select at least one file.");
                return;
            }

            SelectedCloudVersions = _pendingSelections.ToList();
            SelectedMap = _pendingNames
                .Zip(_pendingSelections, (name, ver) => new { name, ver })
                .ToDictionary(x => x.name, x => x.ver);

            DialogResult = true;
            Close();
        }
    }
}
