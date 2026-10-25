using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FastPush
{
    public partial class MainWindow : Window
    {
        // --- КОНСТАНТЫ WIN API ---
        private const int HOTKEY_ID = 9000;
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint VK_SPACE = 0x20;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private IntPtr _windowHandle;
        private HwndSource? _source;
        private bool _isExplicitExit = false;

        // Полный путь к файлу config.json рядом с исполняемым файлом программы
        private readonly string _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        public MainWindow()
        {
            InitializeComponent();
            TrayIcon.Icon = System.Drawing.SystemIcons.Application;

            // Загружаем конфигурацию и строим плитки при старте
            LoadConfiguration();
        }

        // --- ЗАГРУЗКА КОНФИГУРАЦИИ И ИКОНОК ---

        private void LoadConfiguration()
        {
            AppConfig config = ReadOrCreateConfig();
            List<TileViewModel> viewModels = new();

            // Защита на случай, если в JSON написали "Tiles": null
            if (config.Tiles != null)
            {
                foreach (TileConfig tile in config.Tiles)
                {
                    // Пропускаем совсем пустые записи без пути запуска
                    if (string.IsNullOrWhiteSpace(tile.Path))
                        continue;

                    viewModels.Add(new TileViewModel
                    {
                        // Если имя не указано, берем имя самого файла без расширения
                        Name = string.IsNullOrWhiteSpace(tile.Name) 
                               ? Path.GetFileNameWithoutExtension(tile.Path) 
                               : tile.Name,
                        Path = tile.Path,
                        IconSource = ResolveTileIcon(tile.Path, tile.Icon)
                    });
                }
            }

            // Передаем готовый список плиток в интерфейс
            TilesContainer.ItemsSource = viewModels;
        }

        private AppConfig ReadOrCreateConfig()
        {
            // 1. Если файла нет — создаем стандартный config.json и возвращаем его
            if (!File.Exists(_configPath))
            {
                AppConfig defaultConfig = CreateDefaultConfig();
                try
                {
                    var saveOptions = new JsonSerializerOptions
                    {
                        WriteIndented = true, // Красивое форматирование с отступами
                        // Разрешаем кириллицу в JSON без экранирования в \u0411...
                        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic)
                    };
                    string jsonOutput = JsonSerializer.Serialize(defaultConfig, saveOptions);
                    File.WriteAllText(_configPath, jsonOutput);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Не удалось создать файл config.json:\n{ex.Message}",
                                    "Предупреждение FastPush", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return defaultConfig;
            }

            // 2. Если файл есть — пробуем безопасно прочитать и распарсить
            try
            {
                string jsonContent = File.ReadAllText(_configPath);
                var readOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true, // Не страшно, если напишут "name" вместо "Name"
                    ReadCommentHandling = JsonCommentHandling.Skip, // Разрешаем комментарии // в JSON
                    AllowTrailingCommas = true // Прощаем лишнюю запятую после последнего элемента
                };

                AppConfig? parsedConfig = JsonSerializer.Deserialize<AppConfig>(jsonContent, readOptions);
                return parsedConfig ?? CreateDefaultConfig();
            }
            catch (JsonException jsonEx)
            {
                MessageBox.Show($"Ошибка синтаксиса в файле config.json (строка {jsonEx.LineNumber + 1}).\n" +
                                $"Загружен резервный набор плиток.\n\nДетали: {jsonEx.Message}",
                                "Ошибка конфигурации FastPush", MessageBoxButton.OK, MessageBoxImage.Warning);
                return CreateDefaultConfig();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось прочитать config.json:\n{ex.Message}\n\nЗагружен резервный набор плиток.",
                                "Ошибка чтения FastPush", MessageBoxButton.OK, MessageBoxImage.Warning);
                return CreateDefaultConfig();
            }
        }

        // Базовый набор плиток по умолчанию
        private AppConfig CreateDefaultConfig()
        {
            return new AppConfig
            {
                Tiles = new List<TileConfig>
                {
                    new TileConfig { Name = "Блокнот", Path = @"C:\Windows\System32\notepad.exe", Icon = "" },
                    new TileConfig { Name = "Калькулятор", Path = @"C:\Windows\System32\calc.exe", Icon = "" },
                    new TileConfig { Name = "Проводник", Path = @"C:\Windows\explorer.exe", Icon = "" }
                }
            };
        }

        // Метод получения иконки: сначала проверяет кастомную картинку, затем пытается вытащить иконку из .exe
        private ImageSource? ResolveTileIcon(string targetPath, string customIconPath)
        {
            // 1. Если в JSON задан путь к отдельному файлу иконки (.png, .ico, .jpg) и файл существует
            if (!string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath))
            {
                try
                {
                    // Если указали .ico или картинку — загружаем через BitmapImage
                    BitmapImage bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(Path.GetFullPath(customIconPath), UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad; // Сразу читаем в память, чтобы не блокировать файл на диске
                    bitmap.EndInit();
                    bitmap.Freeze(); // Делаем потокобезопасным и быстрым для рендера WPF
                    return bitmap;
                }
                catch
                {
                    // Если картинка битая — тихо переходим к попытке достать иконку из самого файла
                }
            }

            // 2. Если поле Icon пустое (или не сработало) и целевой файл реально существует (например, .exe)
            if (File.Exists(targetPath))
            {
                try
                {
                    using System.Drawing.Icon? associatedIcon = System.Drawing.Icon.ExtractAssociatedIcon(targetPath);
                    if (associatedIcon != null)
                    {
                        ImageSource wpfIcon = Imaging.CreateBitmapSourceFromHIcon(
                            associatedIcon.Handle,
                            Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());

                        wpfIcon.Freeze();
                        return wpfIcon;
                    }
                }
                catch
                {
                    // Если не удалось извлечь (например, нет прав доступа) — просто вернем null (плитка будет только с текстом)
                }
            }

            return null;
        }

        // --- ГЛОБАЛЬНЫЙ ХОТКЕЙ И ОКНО ---

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _windowHandle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_windowHandle);
            _source.AddHook(HwndHook);

            bool isRegistered = RegisterHotKey(
                _windowHandle,
                HOTKEY_ID,
                MOD_CONTROL | MOD_ALT | MOD_NOREPEAT,
                VK_SPACE);

            if (!isRegistered)
            {
                MessageBox.Show("Не удалось зарегистрировать хоткей Ctrl + Alt + Space. Возможно, он уже занят.",
                                "FastPush", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                ToggleOverlay();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void ToggleOverlay()
        {
            if (this.IsVisible)
            {
                this.Hide();
            }
            else
            {
                this.Show();
                this.Activate();
                this.Focus();
            }
        }

        // --- ЗАПУСК ПРОГРАММ ---

        private void Tile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button clickedButton && clickedButton.Tag is string appPath)
            {
                LaunchProgram(appPath);
                this.Hide();
            }
        }

        private void LaunchProgram(string path)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true // Позволяет открывать не только .exe, но и папки, ссылки (https://...) и документы
                };
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть:\n{path}\n\nОшибка: {ex.Message}",
                                "Ошибка FastPush", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // --- ОБРАБОТЧИКИ СОБЫТИЙ И ТРЕЯ ---

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                this.Hide();
            }
        }

        private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e) => ToggleOverlay();

        private void MenuOpen_Click(object sender, RoutedEventArgs e) => ToggleOverlay();

        // Перезагрузка JSON без перезапуска самой программы
        private void MenuReload_Click(object sender, RoutedEventArgs e) => LoadConfiguration();

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            _isExplicitExit = true;
            this.Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_isExplicitExit)
            {
                e.Cancel = true;
                this.Hide();
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _source?.RemoveHook(HwndHook);
            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            TrayIcon.Dispose();
            base.OnClosed(e);
        }
    }
}