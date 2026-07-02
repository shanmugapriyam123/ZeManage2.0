using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;


namespace BIManageRevit.BIManage.Views.Developer
{
    public partial class SelectCommandDialog : Window
    {
        private readonly List<CommandItem> _allItems;

        public int? SelectedCommandId { get; private set; }
        public string? SelectedCommandName { get; private set; }

        public SelectCommandDialog(List<(int CommandId, string CommandName)> commands)
        {
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            _allItems = commands
                .OrderBy(c => c.CommandName)
                .Select(c => new CommandItem
                {
                    CommandId = c.CommandId,
                    CommandName = c.CommandName,
                    CommandIdDisplay = $"({c.CommandId})"
                })
                .ToList();

            CommandListBox.ItemsSource = _allItems;
            CommandCountLabel.Text = $"{_allItems.Count} commands";

            if (_allItems.Any())
                CommandListBox.SelectedIndex = 0;
        }

        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var filter = SearchBox.Text?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(filter))
            {
                CommandListBox.ItemsSource = _allItems;
                CommandCountLabel.Text = $"{_allItems.Count} commands";
            }
            else
            {
                var filtered = _allItems
                    .Where(c => c.CommandName.ToLowerInvariant().Contains(filter) ||
                                c.CommandId.ToString().Contains(filter))
                    .ToList();
                CommandListBox.ItemsSource = filtered;
                CommandCountLabel.Text = $"{filtered.Count} of {_allItems.Count} commands";

                if (filtered.Any())
                    CommandListBox.SelectedIndex = 0;
            }
        }

        private void CommandListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CommandListBox.SelectedItem is CommandItem selected)
            {
                SelectedCommandId = selected.CommandId;
                SelectedCommandName = selected.CommandName;
                DialogResult = true;
            }
        }

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            if (CommandListBox.SelectedItem is CommandItem selected)
            {
                SelectedCommandId = selected.CommandId;
                SelectedCommandName = selected.CommandName;
                DialogResult = true;
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();

        private class CommandItem
        {
            public int CommandId { get; set; }
            public string CommandName { get; set; } = string.Empty;
            public string CommandIdDisplay { get; set; } = string.Empty;
        }
    }
}
