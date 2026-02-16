using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using vault.Core.Domain;
using vault.UI.Localization;

namespace vault.UI
{
    public partial class MoveItemsWindow : Window
    {
        private readonly VaultManager _vaultManager;

        public MoveItemsWindow(VaultManager vaultManager, string initialSelectionPath)
        {
            InitializeComponent();
            _vaultManager = vaultManager ?? throw new ArgumentNullException(nameof(vaultManager));
            ApplyLocalization();
            BuildFolderTree(initialSelectionPath);
        }

        public string SelectedDestinationPath { get; private set; } = string.Empty;

        private void ConfirmMove_Click(object sender, RoutedEventArgs e)
        {
            SelectedDestinationPath = GetSelectedPath();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            string parentPath = GetSelectedPath();
            var prompt = new TextInputWindow(
                UiText.Get("move.prompt.newFolderTitle"),
                UiText.Format(
                    "move.prompt.newFolderIn",
                    string.IsNullOrWhiteSpace(parentPath) ? "/" : "/" + parentPath),
                UiText.Get("move.prompt.create"))
            {
                Owner = this
            };

            if (prompt.ShowDialog() != true)
                return;

            try
            {
                VaultFileItem created = _vaultManager.CreateFolder(prompt.InputValue, parentPath);
                BuildFolderTree(created.FullPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    UiText.Format("move.msg.createFolderError", ex.Message),
                    UiText.Get("common.error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BuildFolderTree(string? selectedPath)
        {
            FolderTreeView.Items.Clear();

            var rootItem = new TreeViewItem
            {
                Header = "/",
                Tag = string.Empty,
                IsExpanded = true
            };
            FolderTreeView.Items.Add(rootItem);

            var index = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = rootItem
            };

            foreach (string rawPath in _vaultManager.GetAllFolderPaths()
                         .Where(p => !string.IsNullOrWhiteSpace(p))
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string path = NormalizePath(rawPath);
                string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                    continue;

                string current = string.Empty;
                TreeViewItem parent = rootItem;

                foreach (string segment in segments)
                {
                    current = string.IsNullOrWhiteSpace(current) ? segment : $"{current}/{segment}";

                    if (!index.TryGetValue(current, out TreeViewItem? node))
                    {
                        node = new TreeViewItem
                        {
                            Header = segment,
                            Tag = current,
                            IsExpanded = true
                        };
                        parent.Items.Add(node);
                        index[current] = node;
                    }

                    parent = node;
                }
            }

            string target = NormalizePath(selectedPath);
            if (!index.TryGetValue(target, out TreeViewItem? selected))
                selected = rootItem;

            selected.IsSelected = true;
            selected.BringIntoView();
            selected.Focus();
        }

        private string GetSelectedPath()
        {
            if (FolderTreeView.SelectedItem is TreeViewItem selected &&
                selected.Tag is string tag)
            {
                return NormalizePath(tag);
            }

            return string.Empty;
        }

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string normalized = path.Replace('\\', '/').Trim().Trim('/');
            return normalized.Length == 0 ? string.Empty : normalized;
        }

        private void ApplyLocalization()
        {
            Title = UiText.Get("move.windowTitle");
            InstructionTextBlock.Text = UiText.Get("move.instruction");
            NewFolderButton.Content = UiText.Get("move.button.newFolder");
            CancelButton.Content = UiText.Get("common.cancel");
            ConfirmMoveButton.Content = UiText.Get("move.button.move");
        }
    }
}
