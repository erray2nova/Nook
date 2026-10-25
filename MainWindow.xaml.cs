using System;
using System.Diagnostics; // Пространство имен для работы с Process.Start
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FastPush
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        // Единый обработчик нажатия для всех плиток
        private void Tile_Click(object sender, RoutedEventArgs e)
        {
            // Проверяем, что источник клика — это кнопка, у которой в свойстве Tag записана строка (путь)
            if (sender is Button clickedButton && clickedButton.Tag is string appPath)
            {
                LaunchProgram(appPath);
            }
        }

        // Метод запуска программы по указанному пути
        private void LaunchProgram(string path)
        {
            // Обязательно оборачиваем в try-catch: если путь неверный или файл удален, оверлей не упадет с ошибкой
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = path,
                    // UseShellExecute = true позволяет запускать программы через оболочку Windows (как при двойном клике)
                    UseShellExecute = true 
                };

                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                // Если что-то пошло не так, покажем понятное окно с причиной ошибки
                MessageBox.Show($"Не удалось запустить файл:\n{path}\n\nОшибка: {ex.Message}", 
                                "Ошибка FastPush", 
                                MessageBoxButton.OK, 
                                MessageBoxImage.Error);
            }
        }

        // Обработчик нажатий клавиш на клавиатуре, пока окно активно
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Если нажата клавиша Escape — закрываем окно (и завершаем программу)
            if (e.Key == Key.Escape)
            {
                this.Close();
            }
        }
    }
}