using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace FastPush
{
    public partial class RenameWindow : Window
    {
        public string NewName { get; private set; }
        public string NewPath { get; private set; }
        private readonly bool _requirePath;

        public RenameWindow(
            string currentName,
            string dialogTitle = "Переименовать",
            string promptLabel = "Название:",
            bool showPathEditor = false,
            string currentPath = "")
        {
            InitializeComponent();

            NewName = currentName;
            NewPath = currentPath;
            _requirePath = showPathEditor;

            DialogTitleText.Text = dialogTitle;
            PromptLabelText.Text = promptLabel;
            NameTextBox.Text = currentName;

            if (showPathEditor)
            {
                PathEditorPanel.Visibility = Visibility.Visible;
                PathTextBox.Text = currentPath;
            }

            Loaded += (_, _) =>
            {
                NameTextBox.Focus();
                NameTextBox.SelectAll();
            };
        }

        private void BrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Выберите приложение, ярлык или файл",
                Filter = "Приложения и ярлыки (*.exe;*.lnk;*.url;*.bat)|*.exe;*.lnk;*.url;*.bat|Все файлы (*.*)|*.*"
            };

            if (dlg.ShowDialog(this) == true)
            {
                PathTextBox.Text = dlg.FileName;
                if (string.IsNullOrWhiteSpace(NameTextBox.Text))
                {
                    NameTextBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
                }
            }
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Выберите папку"
            };

            if (dlg.ShowDialog(this) == true)
            {
                PathTextBox.Text = dlg.FolderName;
                if (string.IsNullOrWhiteSpace(NameTextBox.Text))
                {
                    string trimmed = dlg.FolderName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    NameTextBox.Text = Path.GetFileName(trimmed);
                }
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string name = NameTextBox.Text.Trim();
            string path = PathTextBox.Text.Trim();

            if (_requirePath && string.IsNullOrWhiteSpace(path))
            {
                PathTextBox.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                if (_requirePath && !string.IsNullOrWhiteSpace(path))
                {
                    string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    name = Path.GetFileNameWithoutExtension(trimmed);
                    if (string.IsNullOrWhiteSpace(name)) name = path;
                }
                else
                {
                    NameTextBox.Focus();
                    return;
                }
            }

            NewName = name;
            NewPath = path;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Save_Click(sender, e);
            else if (e.Key == Key.Escape) Cancel_Click(sender, e);
        }
    }
}