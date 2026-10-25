using System.Windows;
using System.Windows.Input;

namespace FastPush
{
    public partial class RenameWindow : Window
    {
        public string NewName { get; private set; }

        public RenameWindow(string currentName)
        {
            InitializeComponent();
            NewName = currentName;
            NameTextBox.Text = currentName;
            
            // Сразу ставим курсор в поле и выделяем старый текст для быстрого ввода
            Loaded += (_, _) =>
            {
                NameTextBox.Focus();
                NameTextBox.SelectAll();
            };
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(NameTextBox.Text))
            {
                NewName = NameTextBox.Text.Trim();
                DialogResult = true;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void NameTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Save_Click(sender, e);
            else if (e.Key == Key.Escape) Cancel_Click(sender, e);
        }
    }
}