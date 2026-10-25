using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace FastPush
{
    public partial class MainWindow : Window
    {
        // --- 1. WIN API: ГЛОБАЛЬНЫЙ ХОТКЕЙ ---
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

        // --- 2. WIN API: ACRYLIC РАЗМЫТИЕ ФОНА И СКРУГЛЕНИЕ УГЛОВ DWM ---
        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private enum AccentState
        {
            ACCENT_DISABLED = 0,
            ACCENT_ENABLE_BLURBEHIND = 3,
            ACCENT_ENABLE_ACRYLICBLURBEHIND = 4
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public AccentState AccentState;
            public uint AccentFlags;
            public uint GradientColor; // Формат AABBGGRR
            public uint AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        // --- 3. WIN API: ИЗВЛЕЧЕНИЕ СИСТЕМНЫХ ИКОНОК ---
        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // --- СОСТОЯНИЕ ПРИЛОЖЕНИЯ ---
        private const string RUN_REGISTRY_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_REGISTRY_NAME = "FastPush";

        private IntPtr _windowHandle;
        private HwndSource? _source;
        private bool _isExplicitExit = false;
        private bool _isAnimatingHide = false;

        private readonly string _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        // Храним плитки по вкладкам в оперативной памяти
        private readonly Dictionary<string, ObservableCollection<TileViewModel>> _categoriesMap = new();
        private readonly List<string> _categoryOrder = new();
        private string _activeCategory = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
            TrayIcon.Icon = System.Drawing.SystemIcons.Application;

            // Проверяем статус автозапуска для галочки в меню трея
            AutoStartMenuItem.IsChecked = IsAutoStartEnabled();

            LoadConfiguration();

            // Если Windows запустила программу автоматически при старте системы (--autostart),
            // сразу прячем оверлей в трей после инициализации дескриптора окна
            string[] args = Environment.GetCommandLineArgs();
            if (args.Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase)))
            {
                Loaded += (_, _) => this.Hide();
            }
            else
            {
                Loaded += (_, _) => PlayShowAnimation();
            }
        }

        // --- СИСТЕМНОЕ ОФОРМЛЕНИЕ: ACRYLIC BLUR И УГЛЫ WINDOWS 11 ---

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _windowHandle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_windowHandle);
            _source.AddHook(HwndHook);

            // Включаем размытие фона за окном
            EnableAcrylicBlur();

            // На Windows 11 включаем аппаратное скругление углов окна (DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2)
            int cornerPreference = 2;
            DwmSetWindowAttribute(_windowHandle, 33, ref cornerPreference, sizeof(int));

            // Регистрируем хоткей Ctrl + Alt + Space
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

        private void EnableAcrylicBlur()
        {
            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2,
                // Цвет тонировки в формате AABBGGRR: 0xCC (80% плотность) + темный графитовый оттенок 161414
                GradientColor = 0xCC181414
            };

            int accentStructSize = Marshal.SizeOf(accent);
            IntPtr accentPtr = Marshal.AllocHGlobal(accentStructSize);
            Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = 19, // WCA_ACCENT_POLICY
                SizeOfData = accentStructSize,
                Data = accentPtr
            };

            try
            {
                SetWindowCompositionAttribute(_windowHandle, ref data);
            }
            catch
            {
                // На старых сборках Windows без Acrylic откатываемся на классический Blur
                accent.AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND;
                Marshal.StructureToPtr(accent, accentPtr, false);
                SetWindowCompositionAttribute(_windowHandle, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }

        // --- АНИМАЦИЯ ПОЯВЛЕНИЯ И СКРЫТИЯ ---

        private void PlayShowAnimation()
        {
            _isAnimatingHide = false;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(180);

            // Плавное проявление прозрачности окна
            var fadeAnim = new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = ease };
            this.BeginAnimation(Window.OpacityProperty, fadeAnim);

            // Легкий зум контента из центра (0.94 -> 1.0)
            var scaleAnim = new DoubleAnimation(0.94, 1.0, duration) { EasingFunction = ease };
            OverlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            OverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        }

        private void AnimateAndHide()
        {
            if (!this.IsVisible || _isAnimatingHide) return;
            _isAnimatingHide = true;

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var duration = TimeSpan.FromMilliseconds(130);

            var fadeAnim = new DoubleAnimation(this.Opacity, 0.0, duration) { EasingFunction = ease };
            var scaleAnim = new DoubleAnimation(1.0, 0.96, duration) { EasingFunction = ease };

            fadeAnim.Completed += (_, _) =>
            {
                if (_isAnimatingHide)
                {
                    this.Hide();
                    _isAnimatingHide = false;
                }
            };

            this.BeginAnimation(Window.OpacityProperty, fadeAnim);
            OverlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            OverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        }

        private void ToggleOverlay()
        {
            if (this.IsVisible && !_isAnimatingHide)
            {
                AnimateAndHide();
            }
            else
            {
                this.Opacity = 0; // Убираем мелькание кадра перед стартом анимации
                this.Show();
                this.Activate();
                this.Focus();
                PlayShowAnimation();
            }
        }

        // --- АВТОЗАПУСК С WINDOWS (РЕЕСТР HKCU) ---

        private bool IsAutoStartEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RUN_REGISTRY_KEY, false);
                return key?.GetValue(APP_REGISTRY_NAME) != null;
            }
            catch
            {
                return false;
            }
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RUN_REGISTRY_KEY, true);
                if (key == null) return;

                if (enable)
                {
                    // Environment.ProcessPath возвращает точный путь к текущему .exe
                    string? exePath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue(APP_REGISTRY_NAME, $"\"{exePath}\" --autostart");
                    }
                }
                else
                {
                    key.DeleteValue(APP_REGISTRY_NAME, false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось изменить настройки автозапуска:\n{ex.Message}",
                                "FastPush", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void AutoStartMenuItem_Click(object sender, RoutedEventArgs e)
        {
            SetAutoStart(AutoStartMenuItem.IsChecked);
        }

        // --- КАТЕГОРИИ (ВКЛАДКИ) И КОНФИГУРАЦИЯ JSON ---

        private void LoadConfiguration()
        {
            AppConfig config = ReadOrCreateConfig();
            _categoriesMap.Clear();
            _categoryOrder.Clear();

            // Миграция: если в файле старый формат (только плоский список Tiles без Categories),
            // переносим существующие плитки во вкладку "Работа" и добавляем "Игры" + "Папки"
            if ((config.Categories == null || config.Categories.Count == 0) && config.Tiles != null && config.Tiles.Count > 0)
            {
                config.Categories = new List<CategoryConfig>
                {
                    new CategoryConfig { Name = "Игры", Tiles = new List<TileConfig>() },
                    new CategoryConfig { Name = "Работа", Tiles = config.Tiles },
                    new CategoryConfig { Name = "Папки", Tiles = new List<TileConfig>() }
                };
                config.Tiles = null;
            }

            // Если конфиг совсем пустой — создаем стандартные 3 категории
            if (config.Categories == null || config.Categories.Count == 0)
            {
                config = CreateDefaultConfig();
            }

            foreach (CategoryConfig category in config.Categories)
            {
                string catName = string.IsNullOrWhiteSpace(category.Name) ? "Общее" : category.Name.Trim();
                if (_categoriesMap.ContainsKey(catName)) continue;

                var collection = new ObservableCollection<TileViewModel>();
                collection.CollectionChanged += (_, _) => UpdateEmptyHint();

                if (category.Tiles != null)
                {
                    foreach (TileConfig tile in category.Tiles)
                    {
                        if (string.IsNullOrWhiteSpace(tile.Path)) continue;

                        collection.Add(new TileViewModel
                        {
                            Name = string.IsNullOrWhiteSpace(tile.Name) ? GetDefaultDisplayName(tile.Path) : tile.Name,
                            Path = tile.Path,
                            CustomIconPath = tile.Icon ?? string.Empty,
                            IconSource = ResolveTileIcon(tile.Path, tile.Icon)
                        });
                    }
                }

                _categoriesMap[catName] = collection;
                _categoryOrder.Add(catName);
            }

            BuildCategoryTabsUI();

            // Выбираем первую вкладку по умолчанию (или сохраняем активную при перезагрузке конфига)
            string targetTab = _categoriesMap.ContainsKey(_activeCategory) ? _activeCategory : _categoryOrder.First();
            SelectCategory(targetTab);
        }

        private void BuildCategoryTabsUI()
        {
            var tabButtons = new List<RadioButton>();

            foreach (string categoryName in _categoryOrder)
            {
                var radioBtn = new RadioButton
                {
                    Content = categoryName,
                    GroupName = "FastPushCategories",
                    Style = (Style)FindResource("CategoryTabStyle"),
                    Tag = categoryName
                };

                radioBtn.Checked += (s, _) =>
                {
                    if (s is RadioButton rb && rb.Tag is string selectedCat)
                    {
                        SwitchActiveCategoryCollection(selectedCat);
                    }
                };

                tabButtons.Add(radioBtn);
            }

            CategoriesContainer.ItemsSource = tabButtons;
        }

        private void SelectCategory(string categoryName)
        {
            if (CategoriesContainer.ItemsSource is IEnumerable<RadioButton> buttons)
            {
                RadioButton? match = buttons.FirstOrDefault(b => string.Equals(b.Tag as string, categoryName, StringComparison.Ordinal));
                if (match != null)
                {
                    match.IsChecked = true;
                }
            }
        }

        private void SwitchActiveCategoryCollection(string categoryName)
        {
            if (!_categoriesMap.TryGetValue(categoryName, out var tilesCollection)) return;

            _activeCategory = categoryName;
            TilesContainer.ItemsSource = tilesCollection;
            UpdateEmptyHint();

            // Легкая плавная проявка плиток при смене вкладки
            TilesContainer.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(140)));
        }

        private ObservableCollection<TileViewModel>? GetActiveTiles()
        {
            return _categoriesMap.TryGetValue(_activeCategory, out var list) ? list : null;
        }

        private void UpdateEmptyHint()
        {
            var current = GetActiveTiles();
            EmptyHintText.Visibility = (current == null || current.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveConfiguration()
        {
            try
            {
                var configToSave = new AppConfig
                {
                    Categories = _categoryOrder.Select(catName => new CategoryConfig
                    {
                        Name = catName,
                        Tiles = _categoriesMap[catName].Select(t => new TileConfig
                        {
                            Name = t.Name,
                            Path = t.Path,
                            Icon = t.CustomIconPath
                        }).ToList()
                    }).ToList(),
                    Tiles = null // Старое поле больше не пишем
                };

                var saveOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic)
                };

                File.WriteAllText(_configPath, JsonSerializer.Serialize(configToSave, saveOptions));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось сохранить изменения в config.json:\n{ex.Message}",
                                "Ошибка сохранения FastPush", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private AppConfig ReadOrCreateConfig()
        {
            if (!File.Exists(_configPath))
            {
                AppConfig defaultConfig = CreateDefaultConfig();
                try
                {
                    var saveOptions = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic)
                    };
                    File.WriteAllText(_configPath, JsonSerializer.Serialize(defaultConfig, saveOptions));
                }
                catch { }
                return defaultConfig;
            }

            try
            {
                string jsonContent = File.ReadAllText(_configPath);
                var readOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                return JsonSerializer.Deserialize<AppConfig>(jsonContent, readOptions) ?? CreateDefaultConfig();
            }
            catch (JsonException jsonEx)
            {
                MessageBox.Show($"Ошибка синтаксиса в файле config.json (строка {jsonEx.LineNumber + 1}).\n" +
                                $"Загружен резервный набор категорий.\n\nДетали: {jsonEx.Message}",
                                "Ошибка конфигурации FastPush", MessageBoxButton.OK, MessageBoxImage.Warning);
                return CreateDefaultConfig();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось прочитать config.json:\n{ex.Message}",
                                "Ошибка чтения FastPush", MessageBoxButton.OK, MessageBoxImage.Warning);
                return CreateDefaultConfig();
            }
        }

        private AppConfig CreateDefaultConfig()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string downloadsPath = Path.Combine(userProfile, "Downloads");

            return new AppConfig
            {
                Categories = new List<CategoryConfig>
                {
                    new CategoryConfig
                    {
                        Name = "Игры",
                        Tiles = new List<TileConfig>()
                    },
                    new CategoryConfig
                    {
                        Name = "Работа",
                        Tiles = new List<TileConfig>
                        {
                            new TileConfig { Name = "Блокнот", Path = @"C:\Windows\System32\notepad.exe", Icon = "" },
                            new TileConfig { Name = "Калькулятор", Path = @"C:\Windows\System32\calc.exe", Icon = "" }
                        }
                    },
                    new CategoryConfig
                    {
                        Name = "Папки",
                        Tiles = new List<TileConfig>
                        {
                            new TileConfig { Name = "Проводник", Path = @"C:\Windows\explorer.exe", Icon = "" },
                            new TileConfig { Name = "Загрузки", Path = Directory.Exists(downloadsPath) ? downloadsPath : userProfile, Icon = "" }
                        }
                    }
                }
            };
        }

        private string GetDefaultDisplayName(string path)
        {
            string trimmedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Directory.Exists(path))
            {
                string folderName = Path.GetFileName(trimmedPath);
                return string.IsNullOrEmpty(folderName) ? path : folderName;
            }

            string fileName = Path.GetFileNameWithoutExtension(trimmedPath);
            return string.IsNullOrWhiteSpace(fileName) ? trimmedPath : fileName;
        }

        // --- ИЗВЛЕЧЕНИЕ ИКОНОК ---

        private ImageSource ResolveTileIcon(string targetPath, string? customIconPath)
        {
            if (!string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(Path.GetFullPath(customIconPath), UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }
                catch { }
            }

            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                SHFILEINFO shinfo = new SHFILEINFO();
                IntPtr result = SHGetFileInfo(
                    targetPath, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_LARGEICON);

                if (result != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
                {
                    try
                    {
                        ImageSource shellIcon = Imaging.CreateBitmapSourceFromHIcon(
                            shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        shellIcon.Freeze();
                        return shellIcon;
                    }
                    catch { }
                    finally
                    {
                        DestroyIcon(shinfo.hIcon);
                    }
                }
            }

            ImageSource defaultIcon = Imaging.CreateBitmapSourceFromHIcon(
                System.Drawing.SystemIcons.Application.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            defaultIcon.Freeze();
            return defaultIcon;
        }

        // --- DRAG & DROP В ТЕКУЩУЮ АКТИВНУЮ ВКЛАДКУ ---

        private void Overlay_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                OverlayBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 122, 254));
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Overlay_DragLeave(object sender, DragEventArgs e)
        {
            OverlayBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        }

        private void Overlay_Drop(object sender, DragEventArgs e)
        {
            OverlayBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));

            var activeTiles = GetActiveTiles();
            if (activeTiles == null) return;

            if (e.Data.GetData(DataFormats.FileDrop) is string[] droppedPaths)
            {
                bool addedAny = false;

                foreach (string path in droppedPaths)
                {
                    if (!File.Exists(path) && !Directory.Exists(path)) continue;

                    bool alreadyExists = activeTiles.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
                    if (alreadyExists) continue;

                    activeTiles.Add(new TileViewModel
                    {
                        Name = GetDefaultDisplayName(path),
                        Path = path,
                        CustomIconPath = string.Empty,
                        IconSource = ResolveTileIcon(path, null)
                    });

                    addedAny = true;
                }

                if (addedAny)
                {
                    SaveConfiguration();
                }
            }
        }

        // --- ПЕРЕИМЕНОВАНИЕ, УДАЛЕНИЕ И ЗАПУСК ---

        private TileViewModel? GetTileFromMenuItem(object sender)
        {
            if (sender is MenuItem menuItem &&
                menuItem.Parent is ContextMenu contextMenu &&
                contextMenu.PlacementTarget is Button button &&
                button.Tag is TileViewModel tile)
            {
                return tile;
            }
            return null;
        }

        private void RenameTile_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? tile = GetTileFromMenuItem(sender);
            if (tile == null) return;

            var dialog = new RenameWindow(tile.Name) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                tile.Name = dialog.NewName;
                SaveConfiguration();
            }
        }

        private void DeleteTile_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? tile = GetTileFromMenuItem(sender);
            var activeTiles = GetActiveTiles();
            if (tile == null || activeTiles == null) return;

            activeTiles.Remove(tile);
            SaveConfiguration();
        }

        private void Tile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button clickedButton && clickedButton.Tag is TileViewModel tile)
            {
                LaunchItem(tile.Path);
                AnimateAndHide();
            }
        }

        private void LaunchItem(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{path}\"",
                        UseShellExecute = true
                    });
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                };

                string? workingDir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
                {
                    startInfo.WorkingDirectory = workingDir;
                }

                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть объект:\n{path}\n\nОшибка: {ex.Message}",
                                "Ошибка FastPush", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // --- ГОРЯЧИЕ КЛАВИШИ И ЖИЗНЕННЫЙ ЦИКЛ ОКНА ---

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                ToggleOverlay();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                AnimateAndHide();
                return;
            }

            // Быстрое переключение вкладок цифрами 1..9 на клавиатуре
            if (e.Key >= Key.D1 && e.Key <= Key.D9)
            {
                int index = e.Key - Key.D1;
                if (index < _categoryOrder.Count)
                {
                    SelectCategory(_categoryOrder[index]);
                }
            }
        }

        private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e) => ToggleOverlay();

        private void MenuOpen_Click(object sender, RoutedEventArgs e) => ToggleOverlay();

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
                AnimateAndHide();
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