using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FastPush
{
    public partial class MainWindow : Window
    {
        // DependencyProperty для мгновенного включения iOS-покачивания плиток и вкладок в XAML
        public static readonly DependencyProperty IsEditModeProperty =
            DependencyProperty.Register(nameof(IsEditMode), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

        public bool IsEditMode
        {
            get => (bool)GetValue(IsEditModeProperty);
            set => SetValue(IsEditModeProperty, value);
        }

        // --- 1. WIN API: ГЛОБАЛЬНЫЙ ХОТКЕЙ ---
        private const int HOTKEY_ID = 9000;
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

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
            public uint GradientColor;
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

        // --- СОСТОЯНИЕ И ПУТИ (%LocalAppData%\Nook) ---
        private const string RUN_REGISTRY_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_REGISTRY_NAME = "Nook";
        private const string DRAG_CATEGORY_FORMAT = "NookInternalCategoryDrag";

        private readonly string _dataDir;
        private readonly string _configPath;

        private IntPtr _windowHandle;
        private HwndSource? _source;
        private bool _isExplicitExit = false;
        private bool _isAnimatingHide = false;
        private bool _isDrawerOpen = false;
        private bool _isSyncingUI = true;
        private bool _suppressDeactivateHide = false;
        private bool _isRecordingHotkey = false;
        private uint _acrylicTintBgr = 0x130E0E;

        // Перетаскивание плиток и вкладок
        private Point _tileDragStartPoint;
        private Point _tileDragPointerOffset;
        private Point _tileLayoutPosition;
        private TileViewModel? _potentialDraggedTile;
        private Button? _draggedTileButton;
        private bool _isTileDragging;
        private bool _isInternalDragging = false;

        private Point _catDragStartPoint;
        private string? _potentialDraggedCategory;

        // Анимация Боке и Часы
        private readonly List<(TranslateTransform MoveY, TranslateTransform SwayX, double SpeedSec, double SwayAmount, double StartY)> _bokehParticles = new();
        private readonly DispatcherTimer _clockTimer = new();

        private DesignSettings _design = new();
        private readonly Dictionary<string, ObservableCollection<TileViewModel>> _categoriesMap = new();
        private readonly List<string> _categoryOrder = new();
        private string _activeCategory = string.Empty;

        public MainWindow()
        {
            // Хранение конфига строго в %LocalAppData%\Nook\config.json
            _dataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nook");
            Directory.CreateDirectory(_dataDir);
            _configPath = System.IO.Path.Combine(_dataDir, "config.json");
            MigrateLegacyConfigIfNeeded();

            InitializeComponent();
            TrayIcon.Icon = System.Drawing.SystemIcons.Application;
            InitializeBokehParticles();

            LoadConfiguration();
            SyncSettingsUI();

            _clockTimer.Interval = TimeSpan.FromSeconds(5);
            _clockTimer.Tick += (_, _) => UpdateClockText();
            UpdateClockText();

            string[] args = Environment.GetCommandLineArgs();
            if (args.Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase)))
            {
                Loaded += (_, _) =>
                {
                    StopBackgroundAnimations();
                    _clockTimer.Stop();
                    this.Hide();
                };
            }
            else
            {
                Loaded += (_, _) =>
                {
                    _clockTimer.Start();
                    UpdateBackgroundAnimations();
                    PlayShowAnimation();
                };
            }
        }

        private void MigrateLegacyConfigIfNeeded()
        {
            try
            {
                string legacyPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
                if (!File.Exists(_configPath) && File.Exists(legacyPath))
                    File.Copy(legacyPath, _configPath);
            }
            catch { }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _windowHandle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_windowHandle);
            _source.AddHook(HwndHook);

            UpdateAcrylicBlur();

            int cornerPreference = 2; // Аппаратное скругление углов DWM Windows 11
            DwmSetWindowAttribute(_windowHandle, 33, ref cornerPreference, sizeof(int));

            RegisterCurrentHotkey(showWarningOnFail: true);
        }

        // --- ЧАСЫ В ШАПКЕ ---

        private void UpdateClockText()
        {
            DateTime now = DateTime.Now;
            ClockTimeText.Text = now.ToString("HH:mm");
            ClockDateText.Text = now.ToString("ddd, d MMM", new CultureInfo("ru-RU"));
        }

        // --- ДВИЖОК ТЕМ, АКЦЕНТОВ И ПРОЗРАЧНОСТИ СТЕКЛА (0% – 100%) ---

        private void ApplyPalette()
        {
            Color accent;
            try { accent = (Color)ColorConverter.ConvertFromString(_design.AccentColor); }
            catch { accent = (Color)ColorConverter.ConvertFromString("#EC4899"); }

            bool isLight = string.Equals(_design.Theme, "White", StringComparison.OrdinalIgnoreCase);

            double luminance = accent.R * 0.299 + accent.G * 0.587 + accent.B * 0.114;
            Color accentFg = luminance > 180 ? Color.FromRgb(20, 20, 24) : Colors.White;

            SetSolidBrush("AccentBrush", accent);
            SetSolidBrush("AccentSoftBrush", Color.FromArgb(isLight ? (byte)0x26 : (byte)0x32, accent.R, accent.G, accent.B));
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
                    SetSolidBrush("HeaderSurfaceBrush", Color.FromArgb(0xBC, 255, 255, 255));
                    SetSolidBrush("DrawerSurfaceBrush", Color.FromArgb(0xF7, 250, 250, 253));
                    SetSolidBrush("CardSurfaceBrush", Color.FromArgb(0xB2, 255, 255, 255));
                    SetSolidBrush("TileSurfaceBrush", Color.FromArgb(0xCC, 255, 255, 255));
                    SetSolidBrush("TileHoverSurfaceBrush", Color.FromArgb(0xF4, 255, 255, 255));
                    SetSolidBrush("InputSurfaceBrush", Color.FromArgb(0xD2, 236, 238, 244));
                    SetSolidBrush("MenuSurfaceBrush", Color.FromArgb(0xFA, 252, 252, 255));
                    SetSolidBrush("BorderSubtleBrush", Color.FromArgb(0x22, 15, 18, 28));
                    SetSolidBrush("GridLineBrush", Color.FromArgb(0x18, accent.R, accent.G, accent.B));
                    SetSolidBrush("ScrimBrush", Color.FromArgb(0x38, 15, 18, 28));
                    break;

                case "Espresso":
                    _acrylicTintBgr = 0x111621;
                    baseBg = Color.FromRgb(26, 18, 14);
                    midBg = Color.FromRgb(38, 26, 20);
                    SetSolidBrush("PrimaryTextBrush", Color.FromRgb(247, 239, 234));
                    SetSolidBrush("SecondaryTextBrush", Color.FromRgb(176, 156, 145));
                    SetSolidBrush("HeaderSurfaceBrush", Color.FromArgb(0x8E, 38, 26, 21));
                    SetSolidBrush("DrawerSurfaceBrush", Color.FromArgb(0xF6, 30, 20, 16));
                    SetSolidBrush("CardSurfaceBrush", Color.FromArgb(0x82, 45, 31, 25));
                    SetSolidBrush("TileSurfaceBrush", Color.FromArgb(0x9C, 46, 32, 26));
                    SetSolidBrush("TileHoverSurfaceBrush", Color.FromArgb(0xCF, 62, 44, 36));
                    SetSolidBrush("InputSurfaceBrush", Color.FromArgb(0xB0, 54, 38, 30));
                    SetSolidBrush("MenuSurfaceBrush", Color.FromArgb(0xF8, 34, 23, 18));
                    SetSolidBrush("BorderSubtleBrush", Color.FromArgb(0x28, 255, 235, 220));
                    SetSolidBrush("GridLineBrush", Color.FromArgb(0x16, 255, 225, 205));
                    SetSolidBrush("ScrimBrush", Color.FromArgb(0x66, 8, 5, 4));
                    break;

                case "Matcha":
                    _acrylicTintBgr = 0x151E12;
                    baseBg = Color.FromRgb(16, 27, 20);
                    midBg = Color.FromRgb(23, 39, 29);
                    SetSolidBrush("PrimaryTextBrush", Color.FromRgb(237, 247, 240));
                    SetSolidBrush("SecondaryTextBrush", Color.FromRgb(146, 174, 156));
                    SetSolidBrush("HeaderSurfaceBrush", Color.FromArgb(0x8E, 22, 38, 29));
                    SetSolidBrush("DrawerSurfaceBrush", Color.FromArgb(0xF6, 18, 31, 23));
                    SetSolidBrush("CardSurfaceBrush", Color.FromArgb(0x82, 27, 46, 35));
                    SetSolidBrush("TileSurfaceBrush", Color.FromArgb(0x9C, 28, 48, 36));
                    SetSolidBrush("TileHoverSurfaceBrush", Color.FromArgb(0xCF, 38, 64, 48));
                    SetSolidBrush("InputSurfaceBrush", Color.FromArgb(0xB0, 32, 54, 41));
                    SetSolidBrush("MenuSurfaceBrush", Color.FromArgb(0xF8, 20, 35, 26));
                    SetSolidBrush("BorderSubtleBrush", Color.FromArgb(0x26, 220, 255, 235));
                    SetSolidBrush("GridLineBrush", Color.FromArgb(0x16, 210, 255, 225));
                    SetSolidBrush("ScrimBrush", Color.FromArgb(0x66, 4, 10, 6));
                    break;

                default: // Black
                    _acrylicTintBgr = 0x130E0E;
                    baseBg = Color.FromRgb(13, 13, 17);
                    midBg = Color.FromRgb(20, 20, 26);
                    SetSolidBrush("PrimaryTextBrush", Color.FromRgb(245, 245, 248));
                    SetSolidBrush("SecondaryTextBrush", Color.FromRgb(146, 146, 160));
                    SetSolidBrush("HeaderSurfaceBrush", Color.FromArgb(0x88, 24, 24, 31));
                    SetSolidBrush("DrawerSurfaceBrush", Color.FromArgb(0xF6, 18, 18, 24));
                    SetSolidBrush("CardSurfaceBrush", Color.FromArgb(0x7E, 28, 28, 36));
                    SetSolidBrush("TileSurfaceBrush", Color.FromArgb(0x98, 28, 28, 36));
                    SetSolidBrush("TileHoverSurfaceBrush", Color.FromArgb(0xCC, 40, 40, 52));
                    SetSolidBrush("InputSurfaceBrush", Color.FromArgb(0xAE, 34, 34, 44));
                    SetSolidBrush("MenuSurfaceBrush", Color.FromArgb(0xF8, 22, 22, 29));
                    SetSolidBrush("BorderSubtleBrush", Color.FromArgb(0x24, 255, 255, 255));
                    SetSolidBrush("GridLineBrush", Color.FromArgb(0x13, 255, 255, 255));
                    SetSolidBrush("ScrimBrush", Color.FromArgb(0x68, 0, 0, 0));
                    break;
            }

            // Честный диапазон 0% (прозрачное стекло, alpha=8) .. 100% (матовый корпус, alpha=250)
            double t = Math.Clamp(_design.BlurOpacity / 100.0, 0.0, 1.0);
            byte surfaceAlpha = (byte)Math.Clamp((int)Math.Round(8 + t * 242), 8, 250);

            Color startTint = BlendColor(baseBg, accent, isLight ? 0.14 : 0.12, surfaceAlpha);
            Color middleTint = Color.FromArgb(surfaceAlpha, midBg.R, midBg.G, midBg.B);
            Color endTint = BlendColor(baseBg, accent, isLight ? 0.28 : 0.25, surfaceAlpha);

            var bgGradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            bgGradient.GradientStops.Add(new GradientStop(startTint, 0.0));
            bgGradient.GradientStops.Add(new GradientStop(middleTint, 0.52));
            bgGradient.GradientStops.Add(new GradientStop(endTint, 1.0));
            bgGradient.Freeze();
            Application.Current.Resources["BackgroundGradientBrush"] = bgGradient;

            var orb1 = new RadialGradientBrush();
            orb1.GradientStops.Add(new GradientStop(Color.FromArgb(isLight ? (byte)0x58 : (byte)0x4C, accent.R, accent.G, accent.B), 0.0));
            orb1.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1.0));
            orb1.Freeze();
            Application.Current.Resources["OrbPrimaryBrush"] = orb1;

            var orb2 = new RadialGradientBrush();
            orb2.GradientStops.Add(new GradientStop(Color.FromArgb(isLight ? (byte)0x44 : (byte)0x38, accent.R, accent.G, accent.B), 0.0));
            orb2.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1.0));
            orb2.Freeze();
            Application.Current.Resources["OrbSecondaryBrush"] = orb2;

            HeaderClockPanel.Visibility = _design.ShowClock ? Visibility.Visible : Visibility.Collapsed;
            TrayIcon.ToolTipText = $"Nook • Beta 0.3 ({_design.HotkeyDisplay})";

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

            double t = Math.Clamp(_design.BlurOpacity / 100.0, 0.0, 1.0);
            // Минимум alpha=10 сохраняет активным аппаратный размытый слой Windows 11 даже на 0%
            byte winAlpha = (byte)Math.Clamp((int)Math.Round(10 + t * 235), 10, 245);
            uint gradientColor = ((uint)winAlpha << 24) | _acrylicTintBgr;

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

        // --- 6 РЕЖИМОВ АНИМАЦИИ ЗАДНЕГО ПЛАНА ---

        private void InitializeBokehParticles()
        {
            double[] xPositions = { 90, 230, 390, 540, 710, 860, 1020, 1150, 320, 940 };
            double[] sizes = { 18, 34, 24, 44, 20, 38, 26, 30, 16, 40 };
            double[] speeds = { 11, 15, 12.5, 17, 10.5, 14, 13, 16, 9.5, 15.5 };
            double[] sways = { 28, -34, 22, -30, 26, -24, 32, -26, 20, -32 };
            double[] startOffsets = { 680, 520, 730, 380, 610, 290, 690, 440, 560, 220 };

            for (int i = 0; i < xPositions.Length; i++)
            {
                var moveY = new TranslateTransform(0, startOffsets[i]);
                var swayX = new TranslateTransform(0, 0);
                var group = new TransformGroup();
                group.Children.Add(moveY);
                group.Children.Add(swayX);

                var circle = new Ellipse
                {
                    Width = sizes[i],
                    Height = sizes[i],
                    Opacity = 0.24,
                    RenderTransform = group,
                    CacheMode = new BitmapCache(0.5)
                };
                circle.SetResourceReference(Shape.FillProperty, "AccentBrush");

                Canvas.SetLeft(circle, xPositions[i]);
                Canvas.SetTop(circle, 0);
                BokehCanvas.Children.Add(circle);

                _bokehParticles.Add((moveY, swayX, speeds[i], sways[i], startOffsets[i]));
            }
        }

        private void UpdateBackgroundAnimations()
        {
            StopBackgroundAnimations();

            string mode = _design.BackgroundMode ?? "Combo";
            bool useBubbles = mode is "Combo" or "Aurora";
            bool useGrid = mode is "Combo" or "Grid";
            bool useWaves = mode == "Waves";
            bool useBokeh = mode == "Bokeh";

            BubblesCanvas.Visibility = useBubbles ? Visibility.Visible : Visibility.Collapsed;
            GridPatternLayer.Visibility = useGrid ? Visibility.Visible : Visibility.Collapsed;
            WavesLayer.Visibility = useWaves ? Visibility.Visible : Visibility.Collapsed;
            BokehCanvas.Visibility = useBokeh ? Visibility.Visible : Visibility.Collapsed;

            if (!this.IsVisible) return;

            var sine = new SineEase { EasingMode = EasingMode.EaseInOut };

            if (useBubbles)
            {
                Orb1Translate.BeginAnimation(TranslateTransform.XProperty, CreateWaveAnim(0, 220, 13, sine));
                Orb1Translate.BeginAnimation(TranslateTransform.YProperty, CreateWaveAnim(0, 120, 16, sine));
                Orb2Translate.BeginAnimation(TranslateTransform.XProperty, CreateWaveAnim(0, -210, 15, sine));
                Orb2Translate.BeginAnimation(TranslateTransform.YProperty, CreateWaveAnim(0, -110, 18, sine));
                Orb3Scale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateWaveAnim(0.85, 1.22, 10, sine));
                Orb3Scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateWaveAnim(0.85, 1.22, 12, sine));
            }

            if (useGrid)
            {
                var loop = new DoubleAnimation(0, 48, TimeSpan.FromSeconds(14)) { RepeatBehavior = RepeatBehavior.Forever };
                GridPatternTranslate.BeginAnimation(TranslateTransform.XProperty, loop);
                GridPatternTranslate.BeginAnimation(TranslateTransform.YProperty, loop);
            }

            if (useWaves)
            {
                StartPulseRing(WaveRing1, Wave1Scale, 0.0);
                StartPulseRing(WaveRing2, Wave2Scale, 2.0);
                StartPulseRing(WaveRing3, Wave3Scale, 4.0);
            }

            if (useBokeh)
            {
                foreach (var p in _bokehParticles)
                {
                    var floatUp = new DoubleAnimation(740, -60, TimeSpan.FromSeconds(p.SpeedSec))
                    {
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    p.MoveY.BeginAnimation(TranslateTransform.YProperty, floatUp);
                    p.SwayX.BeginAnimation(TranslateTransform.XProperty, CreateWaveAnim(-p.SwayAmount, p.SwayAmount, p.SpeedSec * 0.45, sine));
                }
            }
        }

        private static void StartPulseRing(Ellipse ring, ScaleTransform scale, double delaySec)
        {
            var dur = TimeSpan.FromSeconds(6);
            var begin = TimeSpan.FromSeconds(delaySec);

            var scaleAnim = new DoubleAnimation(0.25, 2.5, dur)
            {
                BeginTime = begin,
                RepeatBehavior = RepeatBehavior.Forever
            };
            var fadeAnim = new DoubleAnimation(0.42, 0.0, dur)
            {
                BeginTime = begin,
                RepeatBehavior = RepeatBehavior.Forever
            };

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            ring.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
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

            Wave1Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            Wave1Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            WaveRing1.BeginAnimation(UIElement.OpacityProperty, null);
            Wave2Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            Wave2Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            WaveRing2.BeginAnimation(UIElement.OpacityProperty, null);
            Wave3Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            Wave3Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            WaveRing3.BeginAnimation(UIElement.OpacityProperty, null);

            foreach (var p in _bokehParticles)
            {
                p.MoveY.BeginAnimation(TranslateTransform.YProperty, null);
                p.SwayX.BeginAnimation(TranslateTransform.XProperty, null);
            }
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

        // --- РЕЖИМ РЕДАКТИРОВАНИЯ (КАК НА IPHONE: ПЛИТКИ + КАТЕГОРИИ + DRAG & DROP) ---

        private void SetEditMode(bool enable)
        {
            IsEditMode = enable;
            EditModeBtn.IsChecked = enable;
            EditModeBtn.Content = enable ? "✓ Готово" : "✎ Ред.";
            SubHeaderStatusText.Text = enable ? "режим правки" : "beta 0.3";

            if (enable && _isDrawerOpen)
                CloseSettingsDrawer();
        }

        private void EditModeBtn_Click(object sender, RoutedEventArgs e)
        {
            SetEditMode(EditModeBtn.IsChecked == true);
        }

        private void MenuEditMode_Click(object sender, RoutedEventArgs e)
        {
            if (!this.IsVisible) ToggleOverlay();
            SetEditMode(!IsEditMode);
        }

        private static bool IsClickInsideTaggedButton(DependencyObject? source, DependencyObject stopAt, params string[] tags)
        {
            while (source != null && source != stopAt)
            {
                if (source is Button b && b.Tag is string t && tags.Contains(t))
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        // Живое перетаскивание плиток (iOS Drag & Drop)
        private void Tile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsEditMode || sender is not Button tileBtn || tileBtn.Tag is not TileViewModel tile)
                return;

            if (IsClickInsideTaggedButton(e.OriginalSource as DependencyObject, tileBtn, "QuickDeleteBtn"))
                return;

            _tileDragStartPoint = e.GetPosition(TilesScrollViewer);
            _tileDragPointerOffset = e.GetPosition(tileBtn);
            _potentialDraggedTile = tile;
        }

        private void Tile_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isTileDragging && sender is Button tileButton)
            {
                Point dropPoint = e.GetPosition(TilesScrollViewer);
                Point windowPoint = e.GetPosition(this);
                TileViewModel? draggedTile = _potentialDraggedTile;

                try
                {
                    UpdateTileDrag(dropPoint);
                    tileButton.ReleaseMouseCapture();
                    ResetTileDrag(tileButton);

                    MoveTileToCategoryAtPoint(draggedTile, windowPoint);

                    SaveConfiguration();
                }
                finally
                {
                    tileButton.ReleaseMouseCapture();
                    if (_isTileDragging)
                        ResetTileDrag(tileButton);
                }

                e.Handled = true;
                return;
            }

            _potentialDraggedTile = null;
        }

        private void Tile_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not Button tileButton)
                return;

            if (_isTileDragging)
            {
                Point pointer = e.GetPosition(TilesScrollViewer);
                UpdateTileDrag(pointer);
                e.Handled = true;
                return;
            }

            if (!IsEditMode || _isInternalDragging || _potentialDraggedTile == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            Point pos = e.GetPosition(TilesScrollViewer);
            if (Math.Abs(pos.X - _tileDragStartPoint.X) > 6 || Math.Abs(pos.Y - _tileDragStartPoint.Y) > 6)
            {
                if (!tileButton.CaptureMouse()) return;

                _isInternalDragging = true;
                _isTileDragging = true;
                _suppressDeactivateHide = true;
                _draggedTileButton = tileButton;
                _tileLayoutPosition = tileButton.TranslatePoint(new Point(0, 0), TilesScrollViewer);
                Panel.SetZIndex(tileButton, 10);
                tileButton.Cursor = Cursors.SizeAll;
                e.Handled = true;
            }
        }

        private void Tile_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_isTileDragging && sender is Button tileButton)
                ResetTileDrag(tileButton);
        }

        private void UpdateTileDrag(Point pointer)
        {
            if (_draggedTileButton == null) return;

            Point targetPosition = new(
                pointer.X - _tileDragPointerOffset.X,
                pointer.Y - _tileDragPointerOffset.Y);
            MoveTileToDropPosition(new Point(
                targetPosition.X + _draggedTileButton.ActualWidth / 2,
                targetPosition.Y + _draggedTileButton.ActualHeight / 2));

            _draggedTileButton.RenderTransform = new TranslateTransform(
                targetPosition.X - _tileLayoutPosition.X,
                targetPosition.Y - _tileLayoutPosition.Y);
        }

        private void ResetTileDrag(Button tileButton)
        {
            tileButton.RenderTransform = Transform.Identity;
            tileButton.RenderTransformOrigin = new Point(0.5, 0.5);
            tileButton.Cursor = Cursors.Hand;
            Panel.SetZIndex(tileButton, 0);

            _potentialDraggedTile = null;
            _draggedTileButton = null;
            _isTileDragging = false;
            _isInternalDragging = false;
            _suppressDeactivateHide = false;
        }

        private void MoveTileToDropPosition(Point dropPoint)
        {
            if (_potentialDraggedTile == null)
                return;

            ObservableCollection<TileViewModel>? activeTiles = GetActiveTiles();
            if (activeTiles == null) return;

            int oldIndex = activeTiles.IndexOf(_potentialDraggedTile);
            if (oldIndex < 0) return;

            int nearestIndex = -1;
            Point nearestCenter = default;
            double nearestDistanceSquared = double.MaxValue;

            for (int i = 0; i < activeTiles.Count; i++)
            {
                TileViewModel candidate = activeTiles[i];
                if (ReferenceEquals(candidate, _potentialDraggedTile)) continue;

                if (TilesContainer.ItemContainerGenerator.ContainerFromItem(candidate) is not FrameworkElement container ||
                    TilesContainer.ItemTemplate?.FindName("TileButton", container) is not Button button)
                {
                    continue;
                }

                Point center = button.TranslatePoint(
                    new Point(button.ActualWidth / 2, button.ActualHeight / 2),
                    TilesScrollViewer);
                double deltaX = center.X - dropPoint.X;
                double deltaY = center.Y - dropPoint.Y;
                double distanceSquared = deltaX * deltaX + deltaY * deltaY;

                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearestIndex = i;
                    nearestCenter = center;
                }
            }

            if (nearestIndex < 0) return;

            bool insertAfter = dropPoint.X > nearestCenter.X ||
                (Math.Abs(dropPoint.X - nearestCenter.X) <= 12 && dropPoint.Y > nearestCenter.Y);
            int targetIndex = nearestIndex + (insertAfter ? 1 : 0);
            if (targetIndex > oldIndex) targetIndex--;
            targetIndex = Math.Clamp(targetIndex, 0, activeTiles.Count - 1);

            if (targetIndex != oldIndex)
            {
                Button? draggedButton = _draggedTileButton;
                if (draggedButton != null)
                    draggedButton.RenderTransform = Transform.Identity;

                activeTiles.Move(oldIndex, targetIndex);

                if (draggedButton != null)
                {
                    TilesContainer.UpdateLayout();
                    _tileLayoutPosition = draggedButton.TranslatePoint(
                        new Point(0, 0),
                        TilesScrollViewer);
                }
            }
        }

        private bool MoveTileToCategoryAtPoint(TileViewModel? tile, Point point)
        {
            if (tile == null || CategoriesContainer.ItemsSource is not IEnumerable<RadioButton> categoryButtons)
                return false;

            foreach (RadioButton button in categoryButtons)
            {
                if (button.Tag is not string targetCategory || targetCategory == _activeCategory)
                    continue;

                Point pointInButton = this.TranslatePoint(point, button);
                if (pointInButton.X < 0 || pointInButton.Y < 0 ||
                    pointInButton.X > button.ActualWidth || pointInButton.Y > button.ActualHeight)
                {
                    continue;
                }

                ObservableCollection<TileViewModel>? sourceTiles = GetActiveTiles();
                if (sourceTiles == null ||
                    !_categoriesMap.TryGetValue(targetCategory, out ObservableCollection<TileViewModel>? targetTiles) ||
                    targetTiles.Any(existing => string.Equals(existing.Path, tile.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                if (!sourceTiles.Remove(tile))
                    return true;

                targetTiles.Add(tile);
                SelectCategory(targetCategory);
                return true;
            }

            return false;
        }

        private void QuickDeleteTileBtn_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is TileViewModel tile)
            {
                var active = GetActiveTiles();
                if (active != null && active.Remove(tile))
                    SaveConfiguration();
            }
        }

        // --- РЕДАКТИРОВАНИЕ И ПЕРЕТАСКИВАНИЕ КАТЕГОРИЙ ---

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
                    Tag = categoryName,
                    AllowDrop = true
                };

                var cm = new ContextMenu();
                var renItem = new MenuItem { Header = "Переименовать категорию", Tag = categoryName };
                renItem.Click += (_, _) => PromptRenameCategory(categoryName);

                var delItem = new MenuItem { Header = "Удалить категорию", Tag = categoryName };
                delItem.Click += (_, _) => PromptDeleteCategory(categoryName);

                cm.Items.Add(renItem);
                cm.Items.Add(new Separator());
                cm.Items.Add(delItem);
                radioBtn.ContextMenu = cm;

                radioBtn.Checked += (s, _) =>
                {
                    if (s is RadioButton rb && rb.Tag is string selectedCat)
                        SwitchActiveCategoryCollection(selectedCat);
                };

                // Перетаскивание самих вкладок категорий + сброс плиток на вкладку
                radioBtn.PreviewMouseLeftButtonDown += CategoryTab_PreviewMouseLeftButtonDown;
                radioBtn.PreviewMouseMove += CategoryTab_PreviewMouseMove;
                radioBtn.PreviewMouseLeftButtonUp += (_, _) => _potentialDraggedCategory = null;
                radioBtn.DragEnter += CategoryTab_DragEnter;
                radioBtn.Drop += CategoryTab_Drop;

                tabButtons.Add(radioBtn);
            }

            CategoriesContainer.ItemsSource = tabButtons;
        }

        private void CategoryTab_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsEditMode || sender is not RadioButton rb || rb.Tag is not string catName) return;

            if (IsClickInsideTaggedButton(e.OriginalSource as DependencyObject, rb, "CatRenameBtn", "CatDeleteBtn"))
                return;

            _catDragStartPoint = e.GetPosition(this);
            _potentialDraggedCategory = catName;
        }

        private void CategoryTab_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!IsEditMode || _isInternalDragging || _potentialDraggedCategory == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            Point pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _catDragStartPoint.X) > 6 || Math.Abs(pos.Y - _catDragStartPoint.Y) > 6)
            {
                string draggedCat = _potentialDraggedCategory;
                _isInternalDragging = true;
                _suppressDeactivateHide = true;

                try
                {
                    var data = new DataObject(DRAG_CATEGORY_FORMAT, draggedCat);
                    DragDrop.DoDragDrop(CategoriesContainer, data, DragDropEffects.Move);
                }
                finally
                {
                    _potentialDraggedCategory = null;
                    _isInternalDragging = false;
                    _suppressDeactivateHide = false;
                }
            }
        }

        private void CategoryTab_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DRAG_CATEGORY_FORMAT) ||
                e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.Move;
                e.Handled = true;
            }
        }

        private void CategoryTab_Drop(object sender, DragEventArgs e)
        {
            if (sender is not RadioButton rb || rb.Tag is not string targetCat) return;

            // 1. Смена порядка категорий перетаскиванием
            if (e.Data.GetDataPresent(DRAG_CATEGORY_FORMAT) && e.Data.GetData(DRAG_CATEGORY_FORMAT) is string sourceCat)
            {
                e.Handled = true;
                int oldIdx = _categoryOrder.IndexOf(sourceCat);
                int newIdx = _categoryOrder.IndexOf(targetCat);
                if (oldIdx >= 0 && newIdx >= 0 && oldIdx != newIdx)
                {
                    _categoryOrder.RemoveAt(oldIdx);
                    _categoryOrder.Insert(newIdx, sourceCat);
                    BuildCategoryTabsUI();
                    SelectCategory(_activeCategory);
                    SaveConfiguration();
                }
                return;
            }

            if (!_categoriesMap.TryGetValue(targetCat, out var targetCol)) return;

            // Сброс внешних ярлыков/файлов прямо на вкладку категории
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                e.Handled = true;
                bool added = false;
                foreach (string path in files)
                {
                    if (!File.Exists(path) && !Directory.Exists(path)) continue;
                    if (targetCol.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

                    targetCol.Add(new TileViewModel
                    {
                        Name = GetDefaultDisplayName(path),
                        Path = path,
                        CustomIconPath = string.Empty,
                        IconSource = ResolveTileIcon(path, null)
                    });
                    added = true;
                }

                if (added)
                {
                    SelectCategory(targetCat);
                    SaveConfiguration();
                }
            }
        }

        private void Categories_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_categoryOrder.Count <= 1) return;
            e.Handled = true;
            int idx = _categoryOrder.IndexOf(_activeCategory);
            if (idx < 0) return;

            int next = e.Delta < 0
                ? (idx + 1) % _categoryOrder.Count
                : (idx - 1 + _categoryOrder.Count) % _categoryOrder.Count;

            SelectCategory(_categoryOrder[next]);
        }

        private void AddCategory_Click(object sender, RoutedEventArgs e)
        {
            _suppressDeactivateHide = true;
            try
            {
                var dlg = new RenameWindow("", "Новая категория", "Название категории:") { Owner = this };
                if (dlg.ShowDialog() == true)
                {
                    string name = dlg.NewName;
                    if (_categoriesMap.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
                        return;

                    var col = new ObservableCollection<TileViewModel>();
                    col.CollectionChanged += (_, _) => UpdateEmptyHint();

                    _categoriesMap[name] = col;
                    _categoryOrder.Add(name);

                    BuildCategoryTabsUI();
                    SelectCategory(name);
                    SaveConfiguration();
                }
            }
            finally
            {
                _suppressDeactivateHide = false;
                this.Activate();
            }
        }

        private void CategoryInlineRename_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is DependencyObject d)
            {
                RadioButton? rb = FindParent<RadioButton>(d);
                if (rb?.Tag is string catName)
                    PromptRenameCategory(catName);
            }
        }

        private void CategoryInlineDelete_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is DependencyObject d)
            {
                RadioButton? rb = FindParent<RadioButton>(d);
                if (rb?.Tag is string catName)
                    PromptDeleteCategory(catName);
            }
        }

        private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T typed) return typed;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        private void PromptRenameCategory(string oldName)
        {
            _suppressDeactivateHide = true;
            try
            {
                var dlg = new RenameWindow(oldName, "Переименовать категорию", "Новое название:") { Owner = this };
                if (dlg.ShowDialog() == true && !string.Equals(oldName, dlg.NewName, StringComparison.Ordinal))
                {
                    string newName = dlg.NewName;
                    if (_categoriesMap.ContainsKey(newName)) return;

                    var tiles = _categoriesMap[oldName];
                    _categoriesMap.Remove(oldName);
                    _categoriesMap[newName] = tiles;

                    int idx = _categoryOrder.IndexOf(oldName);
                    if (idx >= 0) _categoryOrder[idx] = newName;
                    if (_activeCategory == oldName) _activeCategory = newName;

                    BuildCategoryTabsUI();
                    SelectCategory(_activeCategory);
                    SaveConfiguration();
                }
            }
            finally
            {
                _suppressDeactivateHide = false;
                this.Activate();
            }
        }

        private void PromptDeleteCategory(string catName)
        {
            if (_categoryOrder.Count <= 1 || !_categoriesMap.TryGetValue(catName, out var col))
                return;

            if (col.Count > 0)
            {
                _suppressDeactivateHide = true;
                try
                {
                    var res = MessageBox.Show(
                        $"Удалить категорию «{catName}» вместе с её плитками ({col.Count} шт.)?",
                        "Nook",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (res != MessageBoxResult.Yes) return;
                }
                finally
                {
                    _suppressDeactivateHide = false;
                    this.Activate();
                }
            }

            _categoriesMap.Remove(catName);
            _categoryOrder.Remove(catName);

            BuildCategoryTabsUI();
            if (_activeCategory == catName)
                SelectCategory(_categoryOrder.First());
            else
                SelectCategory(_activeCategory);

            SaveConfiguration();
        }

        // --- ВЫЕЗЖАЮЩАЯ ПАНЕЛЬ НАСТРОЕК И ЗАПИСЬ ХОТКЕЯ ---

        private void OpenSettingsDrawer()
        {
            if (_isDrawerOpen) return;
            SetEditMode(false);
            _isDrawerOpen = true;

            SyncSettingsUI();

            SettingsScrim.Visibility = Visibility.Visible;
            SettingsDrawer.Visibility = Visibility.Visible;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(260);

            SettingsScrim.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration));
            DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(430, 0, duration) { EasingFunction = ease });
        }

        private void CloseSettingsDrawer(bool instant = false)
        {
            if (!_isDrawerOpen) return;
            _isDrawerOpen = false;
            _isRecordingHotkey = false;
            RecordHotkeyButton.Content = _design.HotkeyDisplay;

            if (instant)
            {
                SettingsScrim.BeginAnimation(UIElement.OpacityProperty, null);
                DrawerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                DrawerTranslate.X = 430;
                SettingsScrim.Visibility = Visibility.Collapsed;
                SettingsDrawer.Visibility = Visibility.Collapsed;
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var duration = TimeSpan.FromMilliseconds(210);

            var scrimFade = new DoubleAnimation(SettingsScrim.Opacity, 0, duration);
            var slideOut = new DoubleAnimation(DrawerTranslate.X, 430, duration) { EasingFunction = ease };

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

            _isRecordingHotkey = false;
            if (RecordHotkeyButton != null) RecordHotkeyButton.Content = _design.HotkeyDisplay;

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

            foreach (var rb in BgModesPanel.Children.OfType<RadioButton>())
                rb.IsChecked = string.Equals(rb.Tag as string, _design.BackgroundMode, StringComparison.OrdinalIgnoreCase);

            int blur = Math.Clamp(_design.BlurOpacity, 0, 100);
            BlurSlider.Value = blur;
            BlurValueText.Text = $"{blur}%";

            ShowClockToggle.IsChecked = _design.ShowClock;
            HideOnDeactivateToggle.IsChecked = _design.HideOnDeactivate;
            HideAfterLaunchToggle.IsChecked = _design.HideAfterLaunch;
            SmoothAnimToggle.IsChecked = _design.SmoothAnimations;
            RecordHotkeyButton.Content = _design.HotkeyDisplay;

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

        private void BgMode_Click(object sender, RoutedEventArgs e)
        {
            if (_isSyncingUI || sender is not RadioButton rb || rb.Tag is not string mode) return;
            _design.BackgroundMode = mode;
            UpdateBackgroundAnimations();
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
            _design.ShowClock = ShowClockToggle.IsChecked == true;
            _design.HideOnDeactivate = HideOnDeactivateToggle.IsChecked == true;
            _design.HideAfterLaunch = HideAfterLaunchToggle.IsChecked == true;
            _design.SmoothAnimations = SmoothAnimToggle.IsChecked == true;

            HeaderClockPanel.Visibility = _design.ShowClock ? Visibility.Visible : Visibility.Collapsed;
            SaveConfiguration();
        }

        private void RecordHotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            _isRecordingHotkey = true;
            RecordHotkeyButton.Content = "Нажмите комбинацию... (Esc — отмена)";
        }

        private void ResetHotkeyBtn_Click(object sender, RoutedEventArgs e)
        {
            _isRecordingHotkey = false;
            _design.HotkeyModifiers = MOD_CONTROL | MOD_ALT;
            _design.HotkeyVirtualKey = 0x20;
            _design.HotkeyDisplay = "Ctrl + Alt + Space";

            RegisterCurrentHotkey(showWarningOnFail: true);
            RecordHotkeyButton.Content = _design.HotkeyDisplay;
            TrayIcon.ToolTipText = $"Nook • Beta 0.3 ({_design.HotkeyDisplay})";
            SaveConfiguration();
        }

        private bool RegisterCurrentHotkey(bool showWarningOnFail)
        {
            if (_windowHandle == IntPtr.Zero) return false;

            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            bool ok = RegisterHotKey(
                _windowHandle,
                HOTKEY_ID,
                _design.HotkeyModifiers | MOD_NOREPEAT,
                _design.HotkeyVirtualKey);

            if (!ok && showWarningOnFail)
            {
                MessageBox.Show($"Хоткей {_design.HotkeyDisplay} уже занят другой программой.\nВы можете назначить другую клавишу в Настройках → Основные.",
                                "Nook", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return ok;
        }

        private void HandleHotkeyRecording(KeyEventArgs e)
        {
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape)
            {
                _isRecordingHotkey = false;
                RecordHotkeyButton.Content = _design.HotkeyDisplay;
                return;
            }

            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                return;

            uint mods = 0;
            var parts = new List<string>();

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { mods |= MOD_CONTROL; parts.Add("Ctrl"); }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))     { mods |= MOD_ALT;     parts.Add("Alt"); }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))   { mods |= MOD_SHIFT;   parts.Add("Shift"); }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) { mods |= MOD_WIN;     parts.Add("Win"); }

            bool isFnKey = key >= Key.F1 && key <= Key.F12;
            if (mods == 0 && !isFnKey)
            {
                RecordHotkeyButton.Content = "Зажмите Ctrl, Alt, Shift или F1–F12";
                return;
            }

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            parts.Add(key == Key.Space ? "Space" : key.ToString());
            string newDisplay = string.Join(" + ", parts);

            uint prevMods = _design.HotkeyModifiers;
            uint prevVk = _design.HotkeyVirtualKey;
            string prevDisplay = _design.HotkeyDisplay;

            _design.HotkeyModifiers = mods;
            _design.HotkeyVirtualKey = vk;
            _design.HotkeyDisplay = newDisplay;

            if (RegisterCurrentHotkey(showWarningOnFail: false))
            {
                RecordHotkeyButton.Content = _design.HotkeyDisplay;
                TrayIcon.ToolTipText = $"Nook • Beta 0.3 ({_design.HotkeyDisplay})";
                SaveConfiguration();
            }
            else
            {
                _design.HotkeyModifiers = prevMods;
                _design.HotkeyVirtualKey = prevVk;
                _design.HotkeyDisplay = prevDisplay;
                RegisterCurrentHotkey(showWarningOnFail: false);
                RecordHotkeyButton.Content = $"{newDisplay} занят! ({prevDisplay})";
            }

            _isRecordingHotkey = false;
        }

        private void AutoStartToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isSyncingUI) return;
            SetAutoStart(AutoStartToggle.IsChecked == true);
            AutoStartMenuItem.IsChecked = IsAutoStartEnabled();
        }

        private void ResetDesign_Click(object sender, RoutedEventArgs e)
        {
            _design = new DesignSettings();
            ApplyPalette();
            UpdateBackgroundAnimations();
            RegisterCurrentHotkey(showWarningOnFail: false);
            SyncSettingsUI();
            SaveConfiguration();
        }

private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
        {
            _suppressDeactivateHide = true;
            try
            {
                Directory.CreateDirectory(_dataDir);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{_dataDir}\"",
                    UseShellExecute = true
                });
            }
            finally
            {
                _suppressDeactivateHide = false;
            }
        }

        // --- АНИМАЦИЯ ПОЯВЛЕНИЯ И СКРЫТИЯ ОКНА ---

        private void PlayShowAnimation()
        {
            _isAnimatingHide = false;

            if (!_design.SmoothAnimations)
            {
                this.BeginAnimation(Window.OpacityProperty, null);
                OverlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                OverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
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

            SetEditMode(false);
            CloseSettingsDrawer(instant: true);

            if (!_design.SmoothAnimations)
            {
                StopBackgroundAnimations();
                _clockTimer.Stop();
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
                    _clockTimer.Stop();
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
                _isAnimatingHide = false;
                if (_design.SmoothAnimations) this.Opacity = 0;

                this.Show();
                this.Activate();
                this.Focus();

                UpdateClockText();
                _clockTimer.Start();
                UpdateBackgroundAnimations();
                PlayShowAnimation();
            }
        }

        // --- АВТОЗАПУСК С WINDOWS ---

        private bool IsAutoStartEnabled()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return false;

                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RUN_REGISTRY_KEY, false);
                string? val = key?.GetValue(APP_REGISTRY_NAME) as string;
                return !string.IsNullOrEmpty(val) && val.Contains(exePath, StringComparison.OrdinalIgnoreCase);
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

                key.DeleteValue("FastPush", false);

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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

        // --- КОНФИГУРАЦИЯ И КАТЕГОРИИ (%LocalAppData%\Nook\config.json) ---

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

        private void SelectCategory(string categoryName)
        {
            if (CategoriesContainer.ItemsSource is IEnumerable<RadioButton> buttons)
            {
                RadioButton? match = buttons.FirstOrDefault(b => string.Equals(b.Tag as string, categoryName, StringComparison.Ordinal));
                if (match != null)
                    match.IsChecked = true;
            }
        }

        private void SwitchActiveCategoryCollection(string categoryName)
        {
            if (!_categoriesMap.TryGetValue(categoryName, out var tilesCollection)) return;

            _activeCategory = categoryName;
            ICollectionView tilesView = CollectionViewSource.GetDefaultView(tilesCollection);
            tilesView.Filter = item =>
                item is TileViewModel tile &&
                (string.IsNullOrWhiteSpace(TileSearchTextBox.Text) ||
                 tile.Name.Contains(TileSearchTextBox.Text, StringComparison.OrdinalIgnoreCase) ||
                 tile.Path.Contains(TileSearchTextBox.Text, StringComparison.OrdinalIgnoreCase));
            TilesContainer.ItemsSource = tilesView;
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

        private void TileSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TilesContainer.ItemsSource is ICollectionView tilesView)
                tilesView.Refresh();
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
                Directory.CreateDirectory(_dataDir);

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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
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
                    Directory.CreateDirectory(_dataDir);
                    var saveOptions = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic)
                    };
                    File.WriteAllText(_configPath, JsonSerializer.Serialize(defaultConfig, saveOptions));
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Не удалось создать config.json:\n{ex.Message}",
                                    "Nook", MessageBoxButton.OK, MessageBoxImage.Error);
                }
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

                return JsonSerializer.Deserialize<AppConfig>(jsonContent, readOptions)
                    ?? throw new JsonException("Файл конфигурации не содержит объекта настроек.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось прочитать config.json:\n{ex.Message}\n\nБудут загружены настройки по умолчанию.",
                                "Nook", MessageBoxButton.OK, MessageBoxImage.Warning);
                return CreateDefaultConfig();
            }
        }

        private AppConfig CreateDefaultConfig()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string downloadsPath = System.IO.Path.Combine(userProfile, "Downloads");

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
            string trimmedPath = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (Directory.Exists(path))
            {
                string folderName = System.IO.Path.GetFileName(trimmedPath);
                return string.IsNullOrEmpty(folderName) ? path : folderName;
            }

            string fileName = System.IO.Path.GetFileNameWithoutExtension(trimmedPath);
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
                    bitmap.UriSource = new Uri(System.IO.Path.GetFullPath(customIconPath), UriKind.Absolute);
                    bitmap.DecodePixelWidth = 128;
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

        // --- РУЧНОЕ ДОБАВЛЕНИЕ, DRAG & DROP ИЗ ПРОВОДНИКА И УПРАВЛЕНИЕ ПЛИТКАМИ ---

        private void AddTileManual_Click(object sender, RoutedEventArgs e)
        {
            var activeTiles = GetActiveTiles();
            if (activeTiles == null) return;

            _suppressDeactivateHide = true;
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Выберите программы или ярлыки",
                    Filter = "Программы и ярлыки (*.exe;*.lnk;*.url;*.bat;*.cmd)|*.exe;*.lnk;*.url;*.bat;*.cmd|Все файлы (*.*)|*.*",
                    DereferenceLinks = false,
                    Multiselect = true
                };

                if (dlg.ShowDialog(this) == true)
                {
                    bool added = false;
                    foreach (string path in dlg.FileNames)
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
                        added = true;
                    }

                    if (added)
                    {
                        SaveConfiguration();
                        UpdateEmptyHint();
                        if (_isDrawerOpen) CloseSettingsDrawer();
                    }
                }
            }
            finally
            {
                _suppressDeactivateHide = false;
                this.Activate();
                this.Focus();
            }
        }

        private void AddFolderManual_Click(object sender, RoutedEventArgs e)
        {
            var activeTiles = GetActiveTiles();
            if (activeTiles == null) return;

            _suppressDeactivateHide = true;
            try
            {
                var dlg = new OpenFolderDialog
                {
                    Title = "Выберите папку для добавления на панель",
                    Multiselect = true
                };

                if (dlg.ShowDialog(this) == true)
                {
                    bool added = false;
                    foreach (string folder in dlg.FolderNames)
                    {
                        if (!Directory.Exists(folder)) continue;
                        if (activeTiles.Any(t => string.Equals(t.Path, folder, StringComparison.OrdinalIgnoreCase))) continue;

                        activeTiles.Add(new TileViewModel
                        {
                            Name = GetDefaultDisplayName(folder),
                            Path = folder,
                            CustomIconPath = string.Empty,
                            IconSource = ResolveTileIcon(folder, null)
                        });
                        added = true;
                    }

                    if (added)
                    {
                        SaveConfiguration();
                        UpdateEmptyHint();
                        if (_isDrawerOpen) CloseSettingsDrawer();
                    }
                }
            }
            finally
            {
                _suppressDeactivateHide = false;
                this.Activate();
                this.Focus();
            }
        }

        private void Overlay_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DRAG_CATEGORY_FORMAT))
                return;

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

            if (e.Data.GetDataPresent(DRAG_CATEGORY_FORMAT))
                return;

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

                if (addedAny)
                {
                    SaveConfiguration();
                    UpdateEmptyHint();
                }
            }
        }

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
            if (tile != null) PromptRenameTile(tile);
        }

        private void EditTile_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? tile = GetTileFromMenuItem(sender);
            if (tile == null) return;

            _suppressDeactivateHide = true;
            try
            {
                var dlg = new RenameWindow(
                    tile.Name,
                    "Редактировать плитку",
                    "Название:",
                    showPathEditor: true,
                    currentPath: tile.Path)
                {
                    Owner = this
                };

                if (dlg.ShowDialog() == true)
                {
                    tile.Name = dlg.NewName;
                    tile.Path = dlg.NewPath;
                    tile.IconSource = ResolveTileIcon(tile.Path, tile.CustomIconPath);
                    SaveConfiguration();
                }
            }
            finally
            {
                _suppressDeactivateHide = false;
                this.Activate();
            }
        }

        private void PromptRenameTile(TileViewModel tile)
        {
            _suppressDeactivateHide = true;
            try
            {
                var dlg = new RenameWindow(tile.Name, "Переименовать плитку", "Новое название:") { Owner = this };
                if (dlg.ShowDialog() == true)
                {
                    tile.Name = dlg.NewName;
                    SaveConfiguration();
                }
            }
            finally
            {
                _suppressDeactivateHide = false;
                this.Activate();
            }
        }

        private void ChangeTileIcon_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? tile = GetTileFromMenuItem(sender);
            if (tile == null) return;

            _suppressDeactivateHide = true;
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Выберите иконку для плитки",
                    Filter = "Изображения и иконки (*.png;*.ico;*.jpg;*.jpeg;*.webp)|*.png;*.ico;*.jpg;*.jpeg;*.webp|Все файлы (*.*)|*.*"
                };

                if (dlg.ShowDialog(this) == true)
                {
                    tile.CustomIconPath = dlg.FileName;
                    tile.IconSource = ResolveTileIcon(tile.Path, tile.CustomIconPath);
                    SaveConfiguration();
                }
            }
            finally
            {
                _suppressDeactivateHide = false;
                this.Activate();
            }
        }

        private void ResetTileIcon_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? tile = GetTileFromMenuItem(sender);
            if (tile == null) return;

            tile.CustomIconPath = string.Empty;
            tile.IconSource = ResolveTileIcon(tile.Path, null);
            SaveConfiguration();
        }

        private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? tile = GetTileFromMenuItem(sender);
            if (tile == null) return;

            if (Directory.Exists(tile.Path))
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{tile.Path}\"", UseShellExecute = true });
            else if (File.Exists(tile.Path))
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{tile.Path}\"", UseShellExecute = true });
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
            if (sender is not Button clickedButton || clickedButton.Tag is not TileViewModel tile)
                return;

            // В режиме правки клик по плитке не запускает игру случайно, а открывает переименование
            if (IsEditMode)
            {
                if (!_isInternalDragging)
                    PromptRenameTile(tile);
                return;
            }

            if (LaunchItem(tile.Path) && _design.HideAfterLaunch)
                AnimateAndHide();
        }

        private bool LaunchItem(string path)
        {
            _suppressDeactivateHide = true;
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
                    return true;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                };

                if (File.Exists(path))
                {
                    string? workingDir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
                        startInfo.WorkingDirectory = workingDir;
                }

                Process.Start(startInfo);
                return true;
            }
            catch (Win32Exception winEx) when (winEx.NativeErrorCode == 1223)
            {
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть объект:\n{path}\n\nОшибка: {ex.Message}",
                                "Nook", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                _suppressDeactivateHide = false;
            }
        }

        // --- СКРЫТИЕ ПРИ ПОТЕРЕ ФОКУСА, ХОТКЕИ И ЖИЗНЕННЫЙ ЦИКЛ ---

        private void HandleFocusLost()
        {
            if (_design.HideOnDeactivate &&
                !IsEditMode &&
                !_isDrawerOpen &&
                !_suppressDeactivateHide &&
                !_isExplicitExit &&
                this.IsVisible)
            {
                AnimateAndHide();
            }
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            HandleFocusLost();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            HandleFocusLost();
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

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (_isRecordingHotkey)
            {
                HandleHotkeyRecording(e);
                return;
            }
            base.OnPreviewKeyDown(e);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (_isRecordingHotkey)
            {
                HandleHotkeyRecording(e);
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (IsEditMode) SetEditMode(false);
                else if (_isDrawerOpen) CloseSettingsDrawer();
                else AnimateAndHide();
                return;
            }

            if (!IsEditMode && !_isDrawerOpen && Keyboard.Modifiers == ModifierKeys.None && e.Key >= Key.D1 && e.Key <= Key.D9)
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
            _clockTimer.Stop();
            _source?.RemoveHook(HwndHook);
            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            TrayIcon.Dispose();
            base.OnClosed(e);
        }
    }
}
