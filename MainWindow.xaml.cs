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

        // --- 2. WIN API: ACRYLIC BLUR И СКРУГЛЕНИЕ DWM ---
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

        // --- 3. WIN API: СИСТЕМНЫЕ ИКОНКИ ---
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

        // --- СОСТОЯНИЕ ЛАУНЧЕРА ---
        private const string RUN_REGISTRY_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_REGISTRY_NAME = "FastPush";

        private IntPtr _windowHandle;
        private HwndSource? _source;
        private bool _isExplicitExit = false;
        private bool _isAnimatingHide = false;
        private bool _isDrawerOpen = false;
        private bool _isSyncingUI = true;
        private uint _acrylicTintBgr = 0x120E0E;

        private readonly string _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        private DesignSettings _design = new();
        private readonly Dictionary<string, ObservableCollection<TileViewModel>> _categoriesMap = new();
        private readonly List<string> _categoryOrder = new();
        private string _activeCategory = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
            TrayIcon.Icon = System.Drawing.SystemIcons.Application;

            LoadConfiguration();
            SyncSettingsUI();

            string[] args = Environment.GetCommandLineArgs();
            if (args.Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase)))
            {
                Loaded += (_, _) =>
                {
                    StopBackgroundAnimations();
                    this.Hide();
                };
            }
            else
            {
                Loaded += (_, _) =>
                {
                    UpdateBackgroundAnimations();
                    PlayShowAnimation();
                };
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _windowHandle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_windowHandle);
            _source.AddHook(HwndHook);

            UpdateAcrylicBlur();

            int cornerPreference = 2; // Аппаратное скругление углов Windows 11
            DwmSetWindowAttribute(_windowHandle, 33, ref cornerPreference, sizeof(int));

            bool isRegistered = RegisterHotKey(
                _windowHandle,
                HOTKEY_ID,
                MOD_CONTROL | MOD_ALT | MOD_NOREPEAT,
                VK_SPACE);

            if (!isRegistered)
            {
                MessageBox.Show("Не удалось зарегистрировать хоткей Ctrl + Alt + Space. Возможно, он уже занят.",
                                "Nook", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // --- ДВИЖОК ПАЛИТРЫ: 4 ТЕМЫ + 8 АКЦЕНТОВ + АДАПТИВНЫЙ ГРАДИЕНТ ---

        private void ApplyPalette()
        {
            Color accent;
            try { accent = (Color)ColorConverter.ConvertFromString(_design.AccentColor); }
            catch { accent = (Color)ColorConverter.ConvertFromString("#EC4899"); }

            bool isLight = string.Equals(_design.Theme, "White", StringComparison.OrdinalIgnoreCase);

            // Контрастный текст на кнопках и активных вкладках
            double luminance = accent.R * 0.299 + accent.G * 0.587 + accent.B * 0.114;
            Color accentFg = luminance > 180 ? Color.FromRgb(20, 20, 24) : Colors.White;

            SetSolidBrush("AccentBrush", accent);
            SetSolidBrush("AccentSoftBrush", Color.FromArgb(isLight ? (byte)0x26 : (byte)0x30, accent.R, accent.G, accent.B));
            SetSolidBrush("AccentForegroundBrush", accentFg);

            Color baseBg;
            Color midBg;

            switch (_design.Theme)
            {
                case "White":
                    _acrylicTintBgr = 0xF7F5F4;
                    baseBg = Color.FromRgb(246, 247, 251);
                    midBg = Color.FromRgb(238, 240, 246);
                    SetSolidBrush("PrimaryTextBrush", Color.FromRgb(24, 24, 30));
                    SetSolidBrush("SecondaryTextBrush", Color.FromRgb(104, 106, 120));
                    SetSolidBrush("HeaderSurfaceBrush", Color.FromArgb(0xB8, 255, 255, 255));
                    SetSolidBrush("DrawerSurfaceBrush", Color.FromArgb(0xF6, 250, 250, 253));
                    SetSolidBrush("CardSurfaceBrush", Color.FromArgb(0xAA, 255, 255, 255));
                    SetSolidBrush("TileSurfaceBrush", Color.FromArgb(0xC8, 255, 255, 255));
                    SetSolidBrush("TileHoverSurfaceBrush", Color.FromArgb(0xF2, 255, 255, 255));
                    SetSolidBrush("InputSurfaceBrush", Color.FromArgb(0xCC, 236, 238, 244));
                    SetSolidBrush("MenuSurfaceBrush", Color.FromArgb(0xFA, 252, 252, 255));
                    SetSolidBrush("BorderSubtleBrush", Color.FromArgb(0x20, 15, 18, 28));
                    SetSolidBrush("GridLineBrush", Color.FromArgb(0x16, accent.R, accent.G, accent.B));
                    SetSolidBrush("ScrimBrush", Color.FromArgb(0x38, 15, 18, 28));
                    break;

                case "Espresso":
                    _acrylicTintBgr = 0x111621; // BGR для теплого кофейного #211611
                    baseBg = Color.FromRgb(26, 18, 14);
                    midBg = Color.FromRgb(38, 26, 20);
                    SetSolidBrush("PrimaryTextBrush", Color.FromRgb(247, 239, 234));
                    SetSolidBrush("SecondaryTextBrush", Color.FromRgb(176, 156, 145));
                    SetSolidBrush("HeaderSurfaceBrush", Color.FromArgb(0x88, 38, 26, 21));
                    SetSolidBrush("DrawerSurfaceBrush", Color.FromArgb(0xF5, 30, 20, 16));
                    SetSolidBrush("CardSurfaceBrush", Color.FromArgb(0x78, 45, 31, 25));
                    SetSolidBrush("TileSurfaceBrush", Color.FromArgb(0x98, 46, 32, 26));
                    SetSolidBrush("TileHoverSurfaceBrush", Color.FromArgb(0xCC, 62, 44, 36));
                    SetSolidBrush("InputSurfaceBrush", Color.FromArgb(0xAA, 54, 38, 30));
                    SetSolidBrush("MenuSurfaceBrush", Color.FromArgb(0xF8, 34, 23, 18));
                    SetSolidBrush("BorderSubtleBrush", Color.FromArgb(0x28, 255, 235, 220));
                    SetSolidBrush("GridLineBrush", Color.FromArgb(0x14, 255, 225, 205));
                    SetSolidBrush("ScrimBrush", Color.FromArgb(0x66, 8, 5, 4));
                    break;

                case "Matcha":
                    _acrylicTintBgr = 0x151E12; // BGR для глубокого матча #121E15
                    baseBg = Color.FromRgb(16, 27, 20);
                    midBg = Color.FromRgb(23, 39, 29);
                    SetSolidBrush("PrimaryTextBrush", Color.FromRgb(237, 247, 240));
                    SetSolidBrush("SecondaryTextBrush", Color.FromRgb(146, 174, 156));
                    SetSolidBrush("HeaderSurfaceBrush", Color.FromArgb(0x88, 22, 38, 29));
                    SetSolidBrush("DrawerSurfaceBrush", Color.FromArgb(0xF5, 18, 31, 23));
                    SetSolidBrush("CardSurfaceBrush", Color.FromArgb(0x78, 27, 46, 35));
                    SetSolidBrush("TileSurfaceBrush", Color.FromArgb(0x98, 28, 48, 36));
                    SetSolidBrush("TileHoverSurfaceBrush", Color.FromArgb(0xCC, 38, 64, 48));
                    SetSolidBrush("InputSurfaceBrush", Color.FromArgb(0xAA, 32, 54, 41));
                    SetSolidBrush("MenuSurfaceBrush", Color.FromArgb(0xF8, 20, 35, 26));
                    SetSolidBrush("BorderSubtleBrush", Color.FromArgb(0x26, 220, 255, 235));
                    SetSolidBrush("GridLineBrush", Color.FromArgb(0x14, 210, 255, 225));
                    SetSolidBrush("ScrimBrush", Color.FromArgb(0x66, 4, 10, 6));
                    break;

                default: // Black
                    _acrylicTintBgr = 0x130E0E;
                    baseBg = Color.FromRgb(13, 13, 17);
                    midBg = Color.FromRgb(20, 20, 26);
                    SetSolidBrush("PrimaryTextBrush", Color.FromRgb(245, 245, 248));
                    SetSolidBrush("SecondaryTextBrush", Color.FromRgb(146, 146, 160));
                    SetSolidBrush("HeaderSurfaceBrush", Color.FromArgb(0x82, 24, 24, 31));
                    SetSolidBrush("DrawerSurfaceBrush", Color.FromArgb(0xF5, 18, 18, 24));
                    SetSolidBrush("CardSurfaceBrush", Color.FromArgb(0x72, 28, 28, 36));
                    SetSolidBrush("TileSurfaceBrush", Color.FromArgb(0x94, 28, 28, 36));
                    SetSolidBrush("TileHoverSurfaceBrush", Color.FromArgb(0xC8, 40, 40, 52));
                    SetSolidBrush("InputSurfaceBrush", Color.FromArgb(0xAA, 34, 34, 44));
                    SetSolidBrush("MenuSurfaceBrush", Color.FromArgb(0xF8, 22, 22, 29));
                    SetSolidBrush("BorderSubtleBrush", Color.FromArgb(0x24, 255, 255, 255));
                    SetSolidBrush("GridLineBrush", Color.FromArgb(0x11, 255, 255, 255));
                    SetSolidBrush("ScrimBrush", Color.FromArgb(0x68, 0, 0, 0));
                    break;
            }

            // Градиент фона из базовой палитры темы и выбранного акцентного цвета
            byte surfaceAlpha = (byte)Math.Clamp((int)Math.Round(_design.BlurOpacity * 2.25), 70, 235);
            Color startTint = BlendColor(baseBg, accent, isLight ? 0.14 : 0.12, surfaceAlpha);
            Color middleTint = Color.FromArgb(surfaceAlpha, midBg.R, midBg.G, midBg.B);
            Color endTint = BlendColor(baseBg, accent, isLight ? 0.26 : 0.24, surfaceAlpha);

            var bgGradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            bgGradient.GradientStops.Add(new GradientStop(startTint, 0.0));
            bgGradient.GradientStops.Add(new GradientStop(middleTint, 0.52));
            bgGradient.GradientStops.Add(new GradientStop(endTint, 1.0));
            bgGradient.Freeze();
            Application.Current.Resources["BackgroundGradientBrush"] = bgGradient;

            // Градиентные сферы (пузыри) в цвет акцента
            var orb1 = new RadialGradientBrush();
            orb1.GradientStops.Add(new GradientStop(Color.FromArgb(isLight ? (byte)0x55 : (byte)0x48, accent.R, accent.G, accent.B), 0.0));
            orb1.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1.0));
            orb1.Freeze();
            Application.Current.Resources["OrbPrimaryBrush"] = orb1;

            var orb2 = new RadialGradientBrush();
            orb2.GradientStops.Add(new GradientStop(Color.FromArgb(isLight ? (byte)0x42 : (byte)0x36, accent.R, accent.G, accent.B), 0.0));
            orb2.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1.0));
            orb2.Freeze();
            Application.Current.Resources["OrbSecondaryBrush"] = orb2;

            UpdateAcrylicBlur();
        }

        private static Color BlendColor(Color baseCol, Color accentCol, double accentRatio, byte alpha)
        {
            byte r = (byte)Math.Clamp(baseCol.R * (1 - accentRatio) + accentCol.R * accentRatio, 0, 255);
            byte g = (byte)Math.Clamp(baseCol.G * (1 - accentRatio) + accentCol.G * accentRatio, 0, 255);
            byte b = (byte)Math.Clamp(baseCol.B * (1 - accentRatio) + accentCol.B * accentRatio, 0, 255);
            return Color.FromArgb(alpha, r, g, b);
        }

        private static void SetSolidBrush(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }

        private void UpdateAcrylicBlur()
        {
            if (_windowHandle == IntPtr.Zero) return;

            byte alpha = (byte)Math.Clamp((int)Math.Round(_design.BlurOpacity / 100.0 * 240), 50, 240);
            uint gradientColor = ((uint)alpha << 24) | _acrylicTintBgr;

            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2,
                GradientColor = gradientColor
            };

            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(accent, ptr, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = 19,
                SizeOfData = size,
                Data = ptr
            };

            try
            {
                SetWindowCompositionAttribute(_windowHandle, ref data);
            }
            catch
            {
                accent.AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND;
                Marshal.StructureToPtr(accent, ptr, false);
                SetWindowCompositionAttribute(_windowHandle, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        // --- АНИМАЦИИ ЗАДНЕГО ПЛАНА (ПУЗЫРИ + СЕТКА) ---

        private void UpdateBackgroundAnimations()
        {
            BubblesCanvas.Visibility = _design.AnimateBubbles ? Visibility.Visible : Visibility.Collapsed;
            GridPatternLayer.Visibility = _design.AnimateGrid ? Visibility.Visible : Visibility.Collapsed;

            if (!this.IsVisible) return;

            if (_design.AnimateBubbles)
            {
                var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
                Orb1Translate.BeginAnimation(TranslateTransform.XProperty, CreateWaveAnim(0, 220, 13, sine));
                Orb1Translate.BeginAnimation(TranslateTransform.YProperty, CreateWaveAnim(0, 120, 16, sine));
                Orb2Translate.BeginAnimation(TranslateTransform.XProperty, CreateWaveAnim(0, -210, 15, sine));
                Orb2Translate.BeginAnimation(TranslateTransform.YProperty, CreateWaveAnim(0, -110, 18, sine));
                Orb3Scale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateWaveAnim(0.85, 1.22, 10, sine));
                Orb3Scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateWaveAnim(0.85, 1.22, 12, sine));
            }
            else
            {
                Orb1Translate.BeginAnimation(TranslateTransform.XProperty, null);
                Orb1Translate.BeginAnimation(TranslateTransform.YProperty, null);
                Orb2Translate.BeginAnimation(TranslateTransform.XProperty, null);
                Orb2Translate.BeginAnimation(TranslateTransform.YProperty, null);
                Orb3Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                Orb3Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }

            if (_design.AnimateGrid)
            {
                // Бесшовное движение сетки на шаг ячейки 48px
                var loopX = new DoubleAnimation(0, 48, TimeSpan.FromSeconds(14)) { RepeatBehavior = RepeatBehavior.Forever };
                var loopY = new DoubleAnimation(0, 48, TimeSpan.FromSeconds(14)) { RepeatBehavior = RepeatBehavior.Forever };
                GridPatternTranslate.BeginAnimation(TranslateTransform.XProperty, loopX);
                GridPatternTranslate.BeginAnimation(TranslateTransform.YProperty, loopY);
            }
            else
            {
                GridPatternTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                GridPatternTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            }
        }

        private void StopBackgroundAnimations()
        {
            Orb1Translate.BeginAnimation(TranslateTransform.XProperty, null);
            Orb1Translate.BeginAnimation(TranslateTransform.YProperty, null);
            Orb2Translate.BeginAnimation(TranslateTransform.XProperty, null);
            Orb2Translate.BeginAnimation(TranslateTransform.YProperty, null);
            Orb3Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            Orb3Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            GridPatternTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            GridPatternTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        }

        private static DoubleAnimation CreateWaveAnim(double from, double to, double sec, IEasingFunction ease)
        {
            return new DoubleAnimation(from, to, TimeSpan.FromSeconds(sec))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = ease
            };
        }

        // --- ВЫЕЗЖАЮЩАЯ ПАНЕЛЬ НАСТРОЕК (СПРАВА НАЛЕВО) ---

        private void OpenSettingsDrawer()
        {
            if (_isDrawerOpen) return;
            _isDrawerOpen = true;

            SyncSettingsUI();

            SettingsScrim.Visibility = Visibility.Visible;
            SettingsDrawer.Visibility = Visibility.Visible;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(260);

            SettingsScrim.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration));
            DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(420, 0, duration) { EasingFunction = ease });
        }

        private void CloseSettingsDrawer(bool instant = false)
        {
            if (!_isDrawerOpen) return;
            _isDrawerOpen = false;

            if (instant)
            {
                SettingsScrim.BeginAnimation(UIElement.OpacityProperty, null);
                DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                DrawerTranslate.X = 420;
                SettingsScrim.Visibility = Visibility.Collapsed;
                SettingsDrawer.Visibility = Visibility.Collapsed;
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var duration = TimeSpan.FromMilliseconds(210);

            var scrimFade = new DoubleAnimation(SettingsScrim.Opacity, 0, duration);
            var slideOut = new DoubleAnimation(DrawerTranslate.X, 420, duration) { EasingFunction = ease };

            slideOut.Completed += (_, _) =>
            {
                if (!_isDrawerOpen)
                {
                    SettingsScrim.Visibility = Visibility.Collapsed;
                    SettingsDrawer.Visibility = Visibility.Collapsed;
                }
            };

            SettingsScrim.BeginAnimation(UIElement.OpacityProperty, scrimFade);
            DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, slideOut);
        }

        private void OpenSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isDrawerOpen) CloseSettingsDrawer();
            else OpenSettingsDrawer();
        }

        private void CloseSettingsBtn_Click(object sender, RoutedEventArgs e) => CloseSettingsDrawer();

        private void SettingsScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseSettingsDrawer();

        private void SettingsTab_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelAppearance == null || sender is not RadioButton rb || rb.Tag is not string tab) return;

            PanelAppearance.Visibility = tab == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
            PanelGeneral.Visibility = tab == "General" ? Visibility.Visible : Visibility.Collapsed;
            PanelOther.Visibility = tab == "Other" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SyncSettingsUI()
        {
            _isSyncingUI = true;

            foreach (var rb in ThemesPanel.Children.OfType<RadioButton>())
                rb.IsChecked = string.Equals(rb.Tag as string, _design.Theme, StringComparison.OrdinalIgnoreCase);

            foreach (var rb in AccentsPanel.Children.OfType<RadioButton>())
                rb.IsChecked = string.Equals(rb.Tag as string, _design.AccentColor, StringComparison.OrdinalIgnoreCase);

            int blur = Math.Clamp(_design.BlurOpacity, 25, 95);
            BlurSlider.Value = blur;
            BlurValueText.Text = $"{blur}%";

            BubblesToggle.IsChecked = _design.AnimateBubbles;
            GridToggle.IsChecked = _design.AnimateGrid;
            HideAfterLaunchToggle.IsChecked = _design.HideAfterLaunch;
            SmoothAnimToggle.IsChecked = _design.SmoothAnimations;

            bool autoStart = IsAutoStartEnabled();
            AutoStartToggle.IsChecked = autoStart;
            AutoStartMenuItem.IsChecked = autoStart;

            _isSyncingUI = false;
        }

        private void ThemeOption_Click(object sender, RoutedEventArgs e)
        {
            if (_isSyncingUI || sender is not RadioButton rb || rb.Tag is not string theme) return;
            _design.Theme = theme;
            ApplyPalette();
            SaveConfiguration();
        }

        private void AccentSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (_isSyncingUI || sender is not RadioButton rb || rb.Tag is not string hex) return;
            _design.AccentColor = hex;
            ApplyPalette();
            SaveConfiguration();
        }

        private void BlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSyncingUI) return;
            _design.BlurOpacity = (int)Math.Round(e.NewValue);
            if (BlurValueText != null) BlurValueText.Text = $"{_design.BlurOpacity}%";
            ApplyPalette();
            SaveConfiguration();
        }

        private void DesignToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isSyncingUI) return;
            _design.AnimateBubbles = BubblesToggle.IsChecked == true;
            _design.AnimateGrid = GridToggle.IsChecked == true;
            _design.HideAfterLaunch = HideAfterLaunchToggle.IsChecked == true;
            _design.SmoothAnimations = SmoothAnimToggle.IsChecked == true;

            UpdateBackgroundAnimations();
            SaveConfiguration();
        }

        private void AutoStartToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isSyncingUI) return;
            bool enable = AutoStartToggle.IsChecked == true;
            SetAutoStart(enable);
            AutoStartMenuItem.IsChecked = IsAutoStartEnabled();
        }

        private void ResetDesign_Click(object sender, RoutedEventArgs e)
        {
            _design = new DesignSettings();
            ApplyPalette();
            UpdateBackgroundAnimations();
            SyncSettingsUI();
            SaveConfiguration();
        }

        private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{AppDomain.CurrentDomain.BaseDirectory}\"",
                UseShellExecute = true
            });
        }

        // --- АНИМАЦИЯ ПОЯВЛЕНИЯ И СКРЫТИЯ ОКНА ---

        private void PlayShowAnimation()
        {
            _isAnimatingHide = false;
            if (!_design.SmoothAnimations)
            {
                this.Opacity = 1.0;
                OverlayScale.ScaleX = 1.0;
                OverlayScale.ScaleY = 1.0;
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(180);

            this.BeginAnimation(Window.OpacityProperty, new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = ease });
            var scaleAnim = new DoubleAnimation(0.95, 1.0, duration) { EasingFunction = ease };
            OverlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            OverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        }

        private void AnimateAndHide()
        {
            if (!this.IsVisible || _isAnimatingHide) return;
            CloseSettingsDrawer(instant: true);

            if (!_design.SmoothAnimations)
            {
                StopBackgroundAnimations();
                this.Hide();
                return;
            }

            _isAnimatingHide = true;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var duration = TimeSpan.FromMilliseconds(130);

            var fadeAnim = new DoubleAnimation(this.Opacity, 0.0, duration) { EasingFunction = ease };
            var scaleAnim = new DoubleAnimation(1.0, 0.96, duration) { EasingFunction = ease };

            fadeAnim.Completed += (_, _) =>
            {
                if (_isAnimatingHide)
                {
                    StopBackgroundAnimations();
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
                this.Opacity = 0;
                this.Show();
                this.Activate();
                this.Focus();
                UpdateBackgroundAnimations();
                PlayShowAnimation();
            }
        }

        // --- АВТОЗАПУСК С WINDOWS ---

        private bool IsAutoStartEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RUN_REGISTRY_KEY, false);
                return key?.GetValue(APP_REGISTRY_NAME) != null;
            }
            catch { return false; }
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RUN_REGISTRY_KEY, true);
                if (key == null) return;

                if (enable)
                {
                    string? exePath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath))
                        key.SetValue(APP_REGISTRY_NAME, $"\"{exePath}\" --autostart");
                }
                else
                {
                    key.DeleteValue(APP_REGISTRY_NAME, false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось изменить настройки автозапуска:\n{ex.Message}",
                                "Nook", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void AutoStartMenuItem_Click(object sender, RoutedEventArgs e)
        {
            SetAutoStart(AutoStartMenuItem.IsChecked);
            SyncSettingsUI();
        }

        // --- КАТЕГОРИИ И ПЛИТКИ (JSON) ---

        private void LoadConfiguration()
        {
            AppConfig config = ReadOrCreateConfig();
            _design = config.Settings ?? new DesignSettings();
            ApplyPalette();

            _categoriesMap.Clear();
            _categoryOrder.Clear();

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

            if (config.Categories == null || config.Categories.Count == 0)
                config = CreateDefaultConfig();

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
                        SwitchActiveCategoryCollection(selectedCat);
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
                if (match != null) match.IsChecked = true;
            }
        }

        private void SwitchActiveCategoryCollection(string categoryName)
        {
            if (!_categoriesMap.TryGetValue(categoryName, out var tilesCollection)) return;

            _activeCategory = categoryName;
            TilesContainer.ItemsSource = tilesCollection;
            UpdateEmptyHint();

            if (_design.SmoothAnimations)
            {
                TilesContainer.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(140)));
            }
        }

        private ObservableCollection<TileViewModel>? GetActiveTiles()
        {
            return _categoriesMap.TryGetValue(_activeCategory, out var list) ? list : null;
        }

        private void UpdateEmptyHint()
        {
            var current = GetActiveTiles();
            EmptyStatePanel.Visibility = (current == null || current.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveConfiguration()
        {
            try
            {
                var configToSave = new AppConfig
                {
                    Settings = _design,
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
                    Tiles = null
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
                MessageBox.Show($"Не удалось сохранить config.json:\n{ex.Message}",
                                "Nook", MessageBoxButton.OK, MessageBoxImage.Error);
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
            catch
            {
                return CreateDefaultConfig();
            }
        }

        private AppConfig CreateDefaultConfig()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string downloadsPath = Path.Combine(userProfile, "Downloads");

            return new AppConfig
            {
                Settings = new DesignSettings(),
                Categories = new List<CategoryConfig>
                {
                    new CategoryConfig { Name = "Игры", Tiles = new List<TileConfig>() },
                    new CategoryConfig
                    {
                        Name = "Работа",
                        Tiles = new List<TileConfig>
                        {
                            new TileConfig { Name = "Блокнот", Path = @"C:\Windows\System32\notepad.exe" },
                            new TileConfig { Name = "Калькулятор", Path = @"C:\Windows\System32\calc.exe" }
                        }
                    },
                    new CategoryConfig
                    {
                        Name = "Папки",
                        Tiles = new List<TileConfig>
                        {
                            new TileConfig { Name = "Проводник", Path = @"C:\Windows\explorer.exe" },
                            new TileConfig { Name = "Загрузки", Path = Directory.Exists(downloadsPath) ? downloadsPath : userProfile }
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

        // --- ДОБАВЛЕНИЕ ПЛИТОК (КНОПКА + DRAG & DROP) ---

        private void AddTileManual_Click(object sender, RoutedEventArgs e)
        {
            var activeTiles = GetActiveTiles();
            if (activeTiles == null) return;

            var dialog = new OpenFileDialog
            {
                Title = "Выберите приложение или ярлык",
                Filter = "Приложения и ярлыки (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url|Все файлы (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == true)
            {
                string path = dialog.FileName;
                if (!activeTiles.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    activeTiles.Add(new TileViewModel
                    {
                        Name = GetDefaultDisplayName(path),
                        Path = path,
                        CustomIconPath = string.Empty,
                        IconSource = ResolveTileIcon(path, null)
                    });
                    SaveConfiguration();
                }
            }
        }

        private void Overlay_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                OverlayBorder.BorderBrush = (Brush)Application.Current.Resources["AccentBrush"];
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Overlay_DragLeave(object sender, DragEventArgs e)
        {
            OverlayBorder.BorderBrush = (Brush)Application.Current.Resources["BorderSubtleBrush"];
        }

        private void Overlay_Drop(object sender, DragEventArgs e)
        {
            OverlayBorder.BorderBrush = (Brush)Application.Current.Resources["BorderSubtleBrush"];

            var activeTiles = GetActiveTiles();
            if (activeTiles == null) return;

            if (e.Data.GetData(DataFormats.FileDrop) is string[] droppedPaths)
            {
                bool addedAny = false;

                foreach (string path in droppedPaths)
                {
                    if (!File.Exists(path) && !Directory.Exists(path)) continue;
                    if (activeTiles.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

                    activeTiles.Add(new TileViewModel
                    {
                        Name = GetDefaultDisplayName(path),
                        Path = path,
                        CustomIconPath = string.Empty,
                        IconSource = ResolveTileIcon(path, null)
                    });
                    addedAny = true;
                }

                if (addedAny) SaveConfiguration();
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
                if (_design.HideAfterLaunch)
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
                    startInfo.WorkingDirectory = workingDir;

                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть объект:\n{path}\n\nОшибка: {ex.Message}",
                                "Nook", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // --- ГОРЯЧИЕ КЛАВИШИ И ТРЕЙ ---

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
                // Если открыта шторка настроек — первый Esc плавно закрывает её, второй скрывает окно
                if (_isDrawerOpen) CloseSettingsDrawer();
                else AnimateAndHide();
                return;
            }

            if (!_isDrawerOpen && e.Key >= Key.D1 && e.Key <= Key.D9)
            {
                int index = e.Key - Key.D1;
                if (index < _categoryOrder.Count)
                    SelectCategory(_categoryOrder[index]);
            }
        }

        private void TrayIcon_DoubleClick(object sender, RoutedEventArgs e) => ToggleOverlay();

        private void MenuOpen_Click(object sender, RoutedEventArgs e) => ToggleOverlay();

        private void MenuSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!this.IsVisible) ToggleOverlay();
            OpenSettingsDrawer();
        }

        private void MenuReload_Click(object sender, RoutedEventArgs e)
        {
            LoadConfiguration();
            SyncSettingsUI();
        }

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
            StopBackgroundAnimations();
            _source?.RemoveHook(HwndHook);
            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            TrayIcon.Dispose();
            base.OnClosed(e);
        }
    }
}