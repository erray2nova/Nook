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
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Resources;
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

        public static readonly DependencyProperty TileSizeProperty =
            DependencyProperty.Register(nameof(TileSize), typeof(double), typeof(MainWindow), new PropertyMetadata(150.0));

        public static readonly DependencyProperty TileMarginProperty =
            DependencyProperty.Register(nameof(TileMargin), typeof(Thickness), typeof(MainWindow), new PropertyMetadata(new Thickness(14)));

        public bool IsEditMode
        {
            get => (bool)GetValue(IsEditModeProperty);
            set => SetValue(IsEditModeProperty, value);
        }

        public double TileSize
        {
            get => (double)GetValue(TileSizeProperty);
            set => SetValue(TileSizeProperty, value);
        }

        public Thickness TileMargin
        {
            get => (Thickness)GetValue(TileMarginProperty);
            set => SetValue(TileMarginProperty, value);
        }

        // --- 1. WIN API: ГЛОБАЛЬНЫЙ ХОТКЕЙ ---
        private const int HOTKEY_ID = 9000;
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

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
        private bool _isApplyingLayoutProfile;
        private uint _acrylicTintBgr = 0x130E0E;
        private int _activeGridColumns;
        private readonly DispatcherTimer _resizeSaveTimer = new();
        private CancellationTokenSource? _searchCancellation;
        private string? _activeSearchQuery;
        private string? _searchError;
        private bool _isSearchLoading;
        private int _searchRequestId;

        // Перетаскивание плиток и вкладок
        private Point _tileDragStartPoint;
        private Point _tileDragPointerOffset;
        private Point _tileLayoutPosition;
        private TileViewModel? _potentialDraggedTile;
        private ObservableCollection<TileViewModel>? _tileDragSourceCollection;
        private Button? _draggedTileButton;
        private bool _isTileDragging;
        private bool _isInternalDragging = false;

        private Point _catDragStartPoint;
        private string? _potentialDraggedCategory;

        // Background animation elements and clock.
        private readonly List<(TranslateTransform MoveY, TranslateTransform SwayX, double SpeedSec, double SwayAmount, double StartY)> _bokehParticles = new();
        private readonly List<TranslateTransform> _cometTransforms = new();
        private readonly List<(TranslateTransform X, TranslateTransform Y, double XAmount, double YAmount, double Duration)> _floatingLights = new();
        private readonly List<(FrameworkElement Element, ScaleTransform Scale, double Duration, double Delay)> _sparkles = new();
        private readonly List<(FrameworkElement Element, double XRatio, double YRatio)> _responsiveBackgroundElements = new();
        private readonly DispatcherTimer _clockTimer = new();
        private static readonly string[] BackgroundEffectNames =
        {
            "Aurora", "Grid", "Waves", "Bokeh", "Comets", "FloatingLights", "Pulse", "Sparkles"
        };

        private DesignSettings _design = new();
        private readonly Dictionary<string, ObservableCollection<TileViewModel>> _categoriesMap = new();
        private readonly Dictionary<string, Guid> _categoryIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<int, List<string>>> _categoryLayouts = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _categoryOrder = new();
        private readonly List<TileViewModel> _navigationStack = new();
        private readonly ObservableCollection<TileViewModel> _globalSearchResults = new();
        private string _activeCategory = string.Empty;
        private bool _isGlobalSearchActive;

        public MainWindow()
        {
            // Хранение конфига строго в %LocalAppData%\Nook\config.json
            _dataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nook");
            Directory.CreateDirectory(_dataDir);
            _configPath = System.IO.Path.Combine(_dataDir, "config.json");
            MigrateLegacyConfigIfNeeded();

            InitializeComponent();
            StartMenuSearchService.RefreshIndexInBackground();
            _resizeSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
            _resizeSaveTimer.Tick += (_, _) =>
            {
                _resizeSaveTimer.Stop();
                SaveConfiguration();
            };
            Icon = LoadNookIcon();
            TrayIcon.Icon = LoadNookTrayIcon();
            InitializeBokehParticles();
            InitializeAdditionalBackgroundEffects();

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

            Loaded += (_, _) => UpdateActiveGridColumnProfile();
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

            ApplyTileOpacity("TileSurfaceBrush");
            ApplyTileOpacity("TileHoverSurfaceBrush");

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
            TrayIcon.ToolTipText = $"Nook • v1.0 ({_design.HotkeyDisplay})";

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

        private void ApplyTileOpacity(string key)
        {
            if (Application.Current.Resources[key] is not SolidColorBrush source) return;

            byte alpha = (byte)Math.Clamp(
                (int)Math.Round(source.Color.A * _design.TileOpacity / 100.0),
                0,
                255);
            SetSolidBrush(key, Color.FromArgb(alpha, source.Color.R, source.Color.G, source.Color.B));
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

        // --- 8 СОЧЕТАЕМЫХ ЭФФЕКТОВ ЗАДНЕГО ПЛАНА ---

        private void InitializeBokehParticles()
        {
            double[] xRatios = { 0.08, 0.23, 0.39, 0.55, 0.72, 0.86, 0.94 };
            double[] sizes = { 26, 42, 32, 54, 28, 46, 34 };
            double[] speeds = { 18, 23, 20, 26, 17, 22, 25 };
            double[] sways = { 18, -22, 16, -20, 21, -17, 19 };
            double[] startOffsets = { 540, 380, 610, 250, 470, 320, 580 };

            for (int i = 0; i < xRatios.Length; i++)
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
                    Opacity = 0.48,
                    RenderTransform = group,
                    CacheMode = new BitmapCache(0.35)
                };
                circle.SetResourceReference(Shape.FillProperty, "OrbSecondaryBrush");

                Canvas.SetLeft(circle, xRatios[i] * 1280);
                Canvas.SetTop(circle, 0);
                BokehCanvas.Children.Add(circle);
                _responsiveBackgroundElements.Add((circle, xRatios[i], 0));

                _bokehParticles.Add((moveY, swayX, speeds[i], sways[i], startOffsets[i]));
            }
        }

        private void InitializeAdditionalBackgroundEffects()
        {
            double[] cometTops = { 0.2, 0.52, 0.78 };
            double[] cometWidths = { 190, 250, 160 };
            for (int i = 0; i < cometTops.Length; i++)
            {
                var moveX = new TranslateTransform();
                var transformGroup = new TransformGroup();
                transformGroup.Children.Add(new RotateTransform(-12 + i * 7));
                transformGroup.Children.Add(moveX);
                var streak = new Rectangle
                {
                    Width = cometWidths[i],
                    Height = 1.5,
                    Opacity = 0.24,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = transformGroup,
                    Fill = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0.5),
                        EndPoint = new Point(1, 0.5),
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                            new GradientStop(Color.FromArgb(190, 255, 255, 255), 1)
                        }
                    }
                };
                Canvas.SetLeft(streak, -cometWidths[i]);
                Canvas.SetTop(streak, cometTops[i] * 720);
                CometsCanvas.Children.Add(streak);
                _responsiveBackgroundElements.Add((streak, -cometWidths[i] / 1280, cometTops[i]));
                _cometTransforms.Add(moveX);
            }

            double[] lightX = { 0.09, 0.28, 0.49, 0.74, 0.91 };
            double[] lightY = { 0.18, 0.7, 0.25, 0.57, 0.36 };
            for (int i = 0; i < lightX.Length; i++)
            {
                var moveX = new TranslateTransform();
                var moveY = new TranslateTransform();
                var transform = new TransformGroup();
                transform.Children.Add(moveX);
                transform.Children.Add(moveY);

                var light = new Ellipse
                {
                    Width = 20 + i % 3 * 10,
                    Height = 20 + i % 3 * 10,
                    Opacity = 0.26,
                    RenderTransform = transform
                };
                light.SetResourceReference(Shape.FillProperty, "OrbSecondaryBrush");
                light.CacheMode = new BitmapCache(0.35);
                Canvas.SetLeft(light, lightX[i] * 1280);
                Canvas.SetTop(light, lightY[i] * 720);
                FloatingLightsCanvas.Children.Add(light);
                _responsiveBackgroundElements.Add((light, lightX[i], lightY[i]));
                _floatingLights.Add((moveX, moveY, 18 + i % 3 * 8, 14 + i % 4 * 5, 9 + i % 3 * 2));
            }

            PulseGlow.SetResourceReference(Shape.FillProperty, "OrbPrimaryBrush");

            double[] sparkleX = { 0.08, 0.2, 0.34, 0.47, 0.61, 0.73, 0.86, 0.95, 0.28, 0.68 };
            double[] sparkleY = { 0.12, 0.35, 0.68, 0.2, 0.82, 0.48, 0.15, 0.62, 0.9, 0.32 };
            for (int i = 0; i < sparkleX.Length; i++)
            {
                var scale = new ScaleTransform(0.45, 0.45);
                var sparkle = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M5,0 L6.4,3.6 10,5 6.4,6.4 5,10 3.6,6.4 0,5 3.6,3.6 Z"),
                    Width = 10,
                    Height = 10,
                    Opacity = 0.12,
                    RenderTransform = scale,
                    RenderTransformOrigin = new Point(0.5, 0.5)
                };
                sparkle.SetResourceReference(Shape.FillProperty, "AccentBrush");
                Canvas.SetLeft(sparkle, sparkleX[i] * 1280);
                Canvas.SetTop(sparkle, sparkleY[i] * 720);
                SparklesCanvas.Children.Add(sparkle);
                _responsiveBackgroundElements.Add((sparkle, sparkleX[i], sparkleY[i]));
                _sparkles.Add((sparkle, scale, 2.8 + i % 4 * 0.7, i % 6 * 0.6));
            }
        }

        private void UpdateBackgroundEffectLayout()
        {
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : Height;
            double scaleX = width / 1280;
            double scaleY = height / 720;

            foreach (var item in _responsiveBackgroundElements)
            {
                Canvas.SetLeft(item.Element, item.XRatio * 1280 * scaleX);
                Canvas.SetTop(item.Element, item.YRatio * 720 * scaleY);
            }
        }

        private List<string> GetBackgroundEffects()
        {
            if (_design.BackgroundEffects == null)
            {
                _design.BackgroundEffects = _design.BackgroundMode switch
                {
                    "Aurora" => new List<string> { "Aurora" },
                    "Grid" => new List<string> { "Grid" },
                    "Waves" => new List<string> { "Waves" },
                    "Bokeh" => new List<string> { "Bokeh" },
                    "None" => new List<string>(),
                    _ => new List<string> { "Aurora", "Grid" }
                };
            }

            var allowedEffects = new HashSet<string>(BackgroundEffectNames, StringComparer.OrdinalIgnoreCase);
            _design.BackgroundEffects = _design.BackgroundEffects
                .Where(allowedEffects.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToList();
            _design.BackgroundMode = "Custom";
            return _design.BackgroundEffects;
        }

        private void UpdateBackgroundAnimations()
        {
            StopBackgroundAnimations();
            UpdateBackgroundEffectLayout();

            List<string> effects = GetBackgroundEffects();
            bool useBubbles = effects.Contains("Aurora", StringComparer.OrdinalIgnoreCase);
            bool useGrid = effects.Contains("Grid", StringComparer.OrdinalIgnoreCase);
            bool useWaves = effects.Contains("Waves", StringComparer.OrdinalIgnoreCase);
            bool useBokeh = effects.Contains("Bokeh", StringComparer.OrdinalIgnoreCase);
            bool useComets = effects.Contains("Comets", StringComparer.OrdinalIgnoreCase);
            bool useFloatingLights = effects.Contains("FloatingLights", StringComparer.OrdinalIgnoreCase);
            bool usePulse = effects.Contains("Pulse", StringComparer.OrdinalIgnoreCase);
            bool useSparkles = effects.Contains("Sparkles", StringComparer.OrdinalIgnoreCase);

            BubblesCanvas.Visibility = useBubbles ? Visibility.Visible : Visibility.Collapsed;
            GridPatternLayer.Visibility = useGrid ? Visibility.Visible : Visibility.Collapsed;
            WavesLayer.Visibility = useWaves ? Visibility.Visible : Visibility.Collapsed;
            BokehCanvas.Visibility = useBokeh ? Visibility.Visible : Visibility.Collapsed;
            CometsCanvas.Visibility = useComets ? Visibility.Visible : Visibility.Collapsed;
            FloatingLightsCanvas.Visibility = useFloatingLights ? Visibility.Visible : Visibility.Collapsed;
            PulseCanvas.Visibility = usePulse ? Visibility.Visible : Visibility.Collapsed;
            SparklesCanvas.Visibility = useSparkles ? Visibility.Visible : Visibility.Collapsed;

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
                var loop = new DoubleAnimation(0, 64, TimeSpan.FromSeconds(28)) { RepeatBehavior = RepeatBehavior.Forever };
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
                double height = Math.Max(ActualHeight, Height);
                foreach (var p in _bokehParticles)
                {
                    var floatUp = new DoubleAnimation(height + p.StartY * 0.2, -70, TimeSpan.FromSeconds(p.SpeedSec))
                    {
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    p.MoveY.BeginAnimation(TranslateTransform.YProperty, floatUp);
                    p.SwayX.BeginAnimation(TranslateTransform.XProperty, CreateWaveAnim(-p.SwayAmount, p.SwayAmount, p.SpeedSec * 0.45, sine));
                }
            }

            if (useComets)
            {
                double travelDistance = Math.Max(ActualWidth, Width) * 1.6 + 300;
                for (int i = 0; i < _cometTransforms.Count; i++)
                {
                    var comet = new DoubleAnimation(0, travelDistance, TimeSpan.FromSeconds(11 + i % 3 * 3))
                    {
                        BeginTime = TimeSpan.FromSeconds(i * 3.8),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    _cometTransforms[i].BeginAnimation(TranslateTransform.XProperty, comet);
                }
            }

            if (useFloatingLights)
            {
                foreach (var light in _floatingLights)
                {
                    light.X.BeginAnimation(TranslateTransform.XProperty, CreateWaveAnim(-light.XAmount, light.XAmount, light.Duration, sine));
                    light.Y.BeginAnimation(TranslateTransform.YProperty, CreateWaveAnim(-light.YAmount, light.YAmount, light.Duration * 1.3, sine));
                }
            }

            if (usePulse)
            {
                PulseGlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateWaveAnim(0.84, 1.04, 11, sine));
                PulseGlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateWaveAnim(0.84, 1.04, 11, sine));
                PulseGlow.BeginAnimation(UIElement.OpacityProperty, CreateWaveAnim(0.12, 0.3, 11, sine));
            }

            if (useSparkles)
            {
                foreach (var sparkle in _sparkles)
                {
                    var twinkle = CreateWaveAnim(0.06, 0.72, sparkle.Duration, sine);
                    twinkle.BeginTime = TimeSpan.FromSeconds(sparkle.Delay);
                    sparkle.Element.BeginAnimation(UIElement.OpacityProperty, twinkle);
                    sparkle.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateWaveAnim(0.35, 1.2, sparkle.Duration, sine));
                    sparkle.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateWaveAnim(0.35, 1.2, sparkle.Duration, sine));
                }
            }
        }

        private static void StartPulseRing(Ellipse ring, ScaleTransform scale, double delaySec)
        {
            var dur = TimeSpan.FromSeconds(10);
            var begin = TimeSpan.FromSeconds(delaySec);

            var scaleAnim = new DoubleAnimation(0.18, 2.7, dur)
            {
                BeginTime = begin,
                RepeatBehavior = RepeatBehavior.Forever
            };
            var fadeAnim = new DoubleAnimation(0.2, 0.0, dur)
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

            foreach (TranslateTransform comet in _cometTransforms)
                comet.BeginAnimation(TranslateTransform.XProperty, null);

            foreach (var light in _floatingLights)
            {
                light.X.BeginAnimation(TranslateTransform.XProperty, null);
                light.Y.BeginAnimation(TranslateTransform.YProperty, null);
            }

            PulseGlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PulseGlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PulseGlow.BeginAnimation(UIElement.OpacityProperty, null);

            foreach (var sparkle in _sparkles)
            {
                sparkle.Element.BeginAnimation(UIElement.OpacityProperty, null);
                sparkle.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                sparkle.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
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
            SubHeaderStatusText.Text = enable ? "режим правки" : "v1.0";

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

        private static bool IsClickInsideTaggedButton(
            DependencyObject? source,
            DependencyObject stopAt,
            string tag,
            string? secondaryTag = null)
        {
            while (source != null && source != stopAt)
            {
                if (source is Button button && button.Tag is string buttonTag &&
                    (string.Equals(buttonTag, tag, StringComparison.Ordinal) ||
                     (secondaryTag != null && string.Equals(buttonTag, secondaryTag, StringComparison.Ordinal))))
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        // Живое перетаскивание плиток (iOS Drag & Drop)
        private void Tile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsEditMode || _isGlobalSearchActive || sender is not Button tileBtn || tileBtn.Tag is not TileViewModel tile)
                return;

            if (IsClickInsideTaggedButton(e.OriginalSource as DependencyObject, tileBtn, "QuickDeleteBtn"))
                return;

            _tileDragStartPoint = e.GetPosition(TilesScrollViewer);
            _tileDragPointerOffset = e.GetPosition(tileBtn);
            _potentialDraggedTile = tile;
            _tileDragSourceCollection = GetActiveTiles();
        }

        private void Tile_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isTileDragging && sender is Button tileButton)
            {
                Point dropPoint = e.GetPosition(TilesScrollViewer);
                Point windowPoint = e.GetPosition(this);
                TileViewModel? draggedTile = _potentialDraggedTile;
                TileViewModel? folderTarget = FindFolderTargetAtPoint(draggedTile, dropPoint);

                try
                {
                    if (folderTarget != null && draggedTile != null && !IsDescendantOf(draggedTile, folderTarget))
                    {
                        ObservableCollection<TileViewModel>? source = _tileDragSourceCollection;
                        if (source != null && source.Remove(draggedTile))
                        {
                            draggedTile.ParentId = folderTarget.Id;
                            folderTarget.Children.Add(draggedTile);
                        }
                    }
                    else
                    {
                        UpdateTileDrag(dropPoint);
                    }
                    tileButton.ReleaseMouseCapture();
                    ResetTileDrag(tileButton);

                    if (folderTarget == null)
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
            _tileDragSourceCollection = null;
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

            if (!IsEditMode || _isGlobalSearchActive || _isInternalDragging || _potentialDraggedTile == null || e.LeftButton != MouseButtonState.Pressed)
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
            _tileDragSourceCollection = null;
            _draggedTileButton = null;
            _isTileDragging = false;
            _isInternalDragging = false;
            _suppressDeactivateHide = false;
        }

        private void MoveTileToDropPosition(Point dropPoint)
        {
            if (_potentialDraggedTile == null)
                return;

            ObservableCollection<TileViewModel>? activeTiles = _tileDragSourceCollection;
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

                ObservableCollection<TileViewModel>? sourceTiles = _tileDragSourceCollection ?? GetActiveTiles();
                if (sourceTiles == null ||
                    !_categoriesMap.TryGetValue(targetCategory, out ObservableCollection<TileViewModel>? targetTiles) ||
                    (!tile.IsInternalFolder && targetTiles.Any(existing => !existing.IsInternalFolder &&
                        string.Equals(existing.Path, tile.Path, StringComparison.OrdinalIgnoreCase))))
                {
                    return true;
                }

                if (!sourceTiles.Remove(tile))
                    return true;

                targetTiles.Add(tile);
                tile.ParentId = null;
                SelectCategory(targetCategory);
                return true;
            }

            return false;
        }

        private TileViewModel? FindFolderTargetAtPoint(TileViewModel? draggedTile, Point point)
        {
            ObservableCollection<TileViewModel>? activeTiles = _tileDragSourceCollection;
            if (draggedTile == null || activeTiles == null) return null;

            foreach (TileViewModel candidate in activeTiles)
            {
                if (ReferenceEquals(candidate, draggedTile) || !candidate.IsInternalFolder ||
                    TilesContainer.ItemContainerGenerator.ContainerFromItem(candidate) is not FrameworkElement container ||
                    TilesContainer.ItemTemplate?.FindName("TileButton", container) is not Button button)
                    continue;

                Point topLeft = button.TranslatePoint(new Point(0, 0), TilesScrollViewer);
                Rect bounds = new(topLeft, new Size(button.ActualWidth, button.ActualHeight));
                if (bounds.Contains(point))
                    return candidate;
            }
            return null;
        }

        private static bool IsDescendantOf(TileViewModel possibleAncestor, TileViewModel tile)
        {
            if (ReferenceEquals(possibleAncestor, tile)) return true;
            return tile.Children.Any(child => IsDescendantOf(possibleAncestor, child));
        }

        private void QuickDeleteTileBtn_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement fe && fe.DataContext is TileViewModel tile)
            {
                var active = FindTileCollection(tile);
                if (active != null && active.Remove(tile))
                {
                    RefreshTileSearchIfActive();
                    SaveConfiguration();
                }
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
                    added |= AddExternalTile(targetCol, path, null);

                if (added)
                {
                    RefreshTileDisplayMetrics();
                    RefreshTileSearchIfActive();
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
                    _categoryIds[name] = Guid.NewGuid();
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
                    if (_categoryIds.Remove(oldName, out Guid categoryId))
                        _categoryIds[newName] = categoryId;

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
            _categoryIds.Remove(catName);
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

            List<string> selectedEffects = GetBackgroundEffects();
            foreach (ToggleButton button in BgEffectsPanel.Children.OfType<ToggleButton>())
                button.IsChecked = button.Tag is string effect &&
                    selectedEffects.Contains(effect, StringComparer.OrdinalIgnoreCase);
            BgEffectsLimitText.Text = selectedEffects.Count == 2
                ? "Выбраны 2 эффекта"
                : "Выберите не более двух эффектов";

            foreach (var rb in GridPresetsPanel.Children.OfType<RadioButton>())
                rb.IsChecked = string.Equals(rb.Tag as string, _design.GridPreset, StringComparison.OrdinalIgnoreCase);

            int blur = Math.Clamp(_design.BlurOpacity, 0, 100);
            BlurSlider.Value = blur;
            BlurValueText.Text = $"{blur}%";

            TileSizeSlider.Value = _design.TileSize;
            TileSpacingSlider.Value = _design.TileSpacing;
            TileOpacitySlider.Value = _design.TileOpacity;
            TileSizeValueText.Text = $"{_design.TileSize:0}";
            TileSpacingValueText.Text = $"{_design.TileSpacing:0}";
            TileOpacityValueText.Text = $"{_design.TileOpacity}%";
            TileSize = _design.TileSize;
            TileMargin = new Thickness(_design.TileSpacing);

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

        private void BgEffect_Click(object sender, RoutedEventArgs e)
        {
            if (_isSyncingUI || sender is not ToggleButton changedButton) return;

            ToggleButton[] selectedButtons = BgEffectsPanel.Children
                .OfType<ToggleButton>()
                .Where(button => button.IsChecked == true)
                .ToArray();
            if (selectedButtons.Length > 2)
            {
                changedButton.IsChecked = false;
                BgEffectsLimitText.Text = "Можно выбрать максимум два эффекта";
                return;
            }

            _design.BackgroundEffects = selectedButtons
                .Where(button => button.Tag is string)
                .Select(button => button.Tag)
                .OfType<string>()
                .ToList();
            _design.BackgroundMode = "Custom";
            BgEffectsLimitText.Text = selectedButtons.Length == 2
                ? "Выбраны 2 эффекта"
                : "Выберите не более двух эффектов";
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

        private void GridPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_isSyncingUI || sender is not RadioButton button || button.Tag is not string preset)
                return;

            _design.GridPreset = preset;
            (double size, double spacing) = preset switch
            {
                "Compact" => (126, 9),
                "Dense" => (104, 5),
                _ => (150, 14)
            };

            _isSyncingUI = true;
            TileSizeSlider.Value = size;
            TileSpacingSlider.Value = spacing;
            _isSyncingUI = false;
            ApplyGridSettings(size, spacing);
        }

        private void GridSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSyncingUI || TileSizeSlider == null || TileSpacingSlider == null) return;

            _design.GridPreset = "Custom";
            _design.TileSize = TileSizeSlider.Value;
            _design.TileSpacing = TileSpacingSlider.Value;
            TileSizeValueText.Text = $"{_design.TileSize:0}";
            TileSpacingValueText.Text = $"{_design.TileSpacing:0}";
            ApplyGridSettings(_design.TileSize, _design.TileSpacing);
        }

        private void ApplyGridSettings(double size, double spacing)
        {
            size = Math.Clamp(double.IsFinite(size) ? size : 150, 100, 180);
            spacing = Math.Clamp(double.IsFinite(spacing) ? spacing : 14, 1, 24);
            _design.TileSize = size;
            _design.TileSpacing = spacing;
            TileSize = size;
            TileMargin = new Thickness(spacing);
            TileSizeValueText.Text = $"{size:0}";
            TileSpacingValueText.Text = $"{spacing:0}";
            RefreshTileDisplayMetrics();
            TilesContainer.UpdateLayout();
            UpdateActiveGridColumnProfile();
            SaveConfiguration();
        }

        private void TileOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSyncingUI) return;

            _design.TileOpacity = (int)Math.Round(e.NewValue);
            TileOpacityValueText.Text = $"{_design.TileOpacity}%";
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
            ManualHotkeyPanel.Visibility = Visibility.Collapsed;
            _isRecordingHotkey = true;
            RecordHotkeyButton.Content = "Нажмите комбинацию... (Esc — отмена)";
        }

        private void ManualHotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            _isRecordingHotkey = false;
            RecordHotkeyButton.Content = _design.HotkeyDisplay;
            ManualHotkeyTextBox.Text = _design.HotkeyDisplay;
            ManualHotkeyPanel.Visibility = Visibility.Visible;
            ManualHotkeyTextBox.Focus();
            ManualHotkeyTextBox.SelectAll();
        }

        private void CancelManualHotkey_Click(object sender, RoutedEventArgs e)
        {
            ManualHotkeyPanel.Visibility = Visibility.Collapsed;
        }

        private void ManualHotkeyTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SaveManualHotkey_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelManualHotkey_Click(sender, e);
                e.Handled = true;
            }
        }

        private void SaveManualHotkey_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseHotkey(ManualHotkeyTextBox.Text, out uint modifiers, out uint virtualKey, out string display))
            {
                MessageBox.Show("Введите сочетание из одной или нескольких клавиш-модификаторов (Ctrl, Alt, Shift, Win) и одной основной клавиши. Например: Shift + Win + A.",
                    "Некорректное сочетание", MessageBoxButton.OK, MessageBoxImage.Warning);
                ManualHotkeyTextBox.Focus();
                ManualHotkeyTextBox.SelectAll();
                return;
            }

            uint previousModifiers = _design.HotkeyModifiers;
            uint previousVirtualKey = _design.HotkeyVirtualKey;
            string previousDisplay = _design.HotkeyDisplay;

            _design.HotkeyModifiers = modifiers;
            _design.HotkeyVirtualKey = virtualKey;
            _design.HotkeyDisplay = display;

            if (!RegisterCurrentHotkey(showWarningOnFail: false))
            {
                _design.HotkeyModifiers = previousModifiers;
                _design.HotkeyVirtualKey = previousVirtualKey;
                _design.HotkeyDisplay = previousDisplay;
                RegisterCurrentHotkey(showWarningOnFail: false);
                MessageBox.Show($"Windows не разрешила зарегистрировать сочетание «{display}». Возможно, оно занято другой программой или зарезервировано системой. Прежний хоткей сохранён.",
                    "Не удалось назначить хоткей", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RecordHotkeyButton.Content = display;
            TrayIcon.ToolTipText = $"Nook • v1.0 ({display})";
            ManualHotkeyPanel.Visibility = Visibility.Collapsed;
            SaveConfiguration();
        }

        private static bool TryParseHotkey(string input, out uint modifiers, out uint virtualKey, out string display)
        {
            modifiers = 0;
            virtualKey = 0;
            display = string.Empty;

            string[] tokens = input.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
                return false;

            var modifierNames = new List<string>(4);
            for (int i = 0; i < tokens.Length - 1; i++)
            {
                uint modifier;
                string modifierName;
                switch (tokens[i].ToUpperInvariant())
                {
                    case "CTRL":
                    case "CONTROL":
                        modifier = MOD_CONTROL;
                        modifierName = "Ctrl";
                        break;
                    case "ALT":
                        modifier = MOD_ALT;
                        modifierName = "Alt";
                        break;
                    case "SHIFT":
                        modifier = MOD_SHIFT;
                        modifierName = "Shift";
                        break;
                    case "WIN":
                    case "WINDOWS":
                        modifier = MOD_WIN;
                        modifierName = "Win";
                        break;
                    default:
                        return false;
                }

                if ((modifiers & modifier) != 0)
                    return false;

                modifiers |= modifier;
                modifierNames.Add(modifierName);
            }

            string keyToken = tokens[^1];
            if (keyToken.Equals("SPACE", StringComparison.OrdinalIgnoreCase))
                keyToken = nameof(Key.Space);

            if (!Enum.TryParse(keyToken, ignoreCase: true, out Key key) ||
                !Enum.IsDefined(typeof(Key), key) ||
                key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
                    Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                return false;
            }

            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey == 0)
                return false;

            modifierNames.Add(key == Key.Space ? "Space" : key.ToString());
            display = string.Join(" + ", modifierNames);
            return true;
        }

        private void ResetHotkeyBtn_Click(object sender, RoutedEventArgs e)
        {
            _isRecordingHotkey = false;
            ManualHotkeyPanel.Visibility = Visibility.Collapsed;
            _design.HotkeyModifiers = MOD_CONTROL | MOD_ALT;
            _design.HotkeyVirtualKey = 0x20;
            _design.HotkeyDisplay = "Ctrl + Alt + Space";

            RegisterCurrentHotkey(showWarningOnFail: true);
            RecordHotkeyButton.Content = _design.HotkeyDisplay;
            TrayIcon.ToolTipText = $"Nook • v1.0 ({_design.HotkeyDisplay})";
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
            bool windowsKeyDown = Keyboard.Modifiers.HasFlag(ModifierKeys.Windows) ||
                (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 ||
                (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { mods |= MOD_CONTROL; parts.Add("Ctrl"); }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))     { mods |= MOD_ALT;     parts.Add("Alt"); }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))   { mods |= MOD_SHIFT;   parts.Add("Shift"); }
            if (windowsKeyDown)                                   { mods |= MOD_WIN;     parts.Add("Win"); }

            bool isFnKey = key >= Key.F1 && key <= Key.F12;
            if (mods == 0 && !isFnKey)
            {
                RecordHotkeyButton.Content = "Зажмите Ctrl, Alt, Shift, Win или F1–F12";
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
                TrayIcon.ToolTipText = $"Nook • v1.0 ({_design.HotkeyDisplay})";
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
            _design.TileSize = Math.Clamp(double.IsFinite(_design.TileSize) ? _design.TileSize : 150, 100, 180);
            _design.TileSpacing = Math.Clamp(double.IsFinite(_design.TileSpacing) ? _design.TileSpacing : 14, 1, 24);
            _design.TileOpacity = Math.Clamp(_design.TileOpacity, 30, 100);
            _design.GridPreset ??= "Standard";
            TileSize = _design.TileSize;
            TileMargin = new Thickness(_design.TileSpacing);
            Width = Math.Clamp(double.IsFinite(_design.WindowWidth) ? _design.WindowWidth : 1280,
                MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
            Height = Math.Clamp(double.IsFinite(_design.WindowHeight) ? _design.WindowHeight : 720,
                MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
            ApplyPalette();

            _categoriesMap.Clear();
            _categoryIds.Clear();
            _categoryLayouts.Clear();
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

            var usedTileIds = new HashSet<Guid>();
            foreach (CategoryConfig? category in config.Categories)
            {
                if (category == null) continue;
                string catName = string.IsNullOrWhiteSpace(category.Name) ? "Общее" : category.Name.Trim();
                if (_categoriesMap.ContainsKey(catName)) continue;
                _categoryIds[catName] = category.Id == Guid.Empty ? Guid.NewGuid() : category.Id;

                var collection = new ObservableCollection<TileViewModel>();
                collection.CollectionChanged += (_, _) => UpdateEmptyHint();
                _categoryLayouts[catName] = category.ColumnLayouts?
                    .Where(profile => profile.Key > 0 && profile.Value != null)
                    .ToDictionary(
                        profile => profile.Key,
                        profile => profile.Value.Where(path => !string.IsNullOrWhiteSpace(path)).ToList())
                    ?? new Dictionary<int, List<string>>();

                if (category.Tiles != null)
                {
                    foreach (TileConfig? tile in category.Tiles)
                    {
                        if (tile == null) continue;
                        TileViewModel? loadedTile = LoadTileConfiguration(tile, null, usedTileIds, 0);
                        if (loadedTile != null)
                            collection.Add(loadedTile);
                    }
                }

                _categoriesMap[catName] = collection;
                _categoryOrder.Add(catName);
            }

            BuildCategoryTabsUI();
            _navigationStack.Clear();
            string targetTab = _categoriesMap.ContainsKey(_activeCategory) ? _activeCategory : _categoryOrder.First();
            SelectCategory(targetTab);
            RefreshTileDisplayMetrics();
        }

        private TileViewModel? LoadTileConfiguration(TileConfig config, Guid? parentId, HashSet<Guid> usedIds, int depth)
        {
            config.Children ??= new List<TileConfig>();
            string type = string.IsNullOrWhiteSpace(config.Type)
                ? InferTileType(config.Path)
                : config.Type;
            if (type is not (TileTypes.Application or TileTypes.File or TileTypes.Folder or TileTypes.InternalFolder))
                type = InferTileType(config.Path);
            if (type != TileTypes.InternalFolder && string.IsNullOrWhiteSpace(config.Path))
                return null;

            Guid id = config.Id;
            if (id == Guid.Empty || !usedIds.Add(id))
            {
                do { id = Guid.NewGuid(); }
                while (!usedIds.Add(id));
            }
            string path = config.Path ?? string.Empty;
            var tile = new TileViewModel
            {
                Id = id,
                ParentId = parentId,
                Type = type,
                Name = string.IsNullOrWhiteSpace(config.Name)
                    ? (type == TileTypes.InternalFolder ? "Новая папка" : GetDefaultDisplayName(path))
                    : config.Name.Trim(),
                Path = path,
                CustomIconPath = config.Icon ?? string.Empty,
                ColumnSpan = Math.Clamp(config.ColumnSpan, 1, 2),
                ShowLabel = config.ShowLabel,
                LabelPlacement = config.LabelPlacement is "Inside" or "Below" ? config.LabelPlacement : "Inside",
                LabelAlignment = config.LabelAlignment is "Left" or "Center" or "Right" ? config.LabelAlignment : "Center",
                IconSource = type == TileTypes.InternalFolder
                    ? (string.IsNullOrWhiteSpace(config.Icon) ? LoadNookIcon() : ResolveTileIcon(path, config.Icon))
                    : ResolveTileIcon(path, config.Icon)
            };

            foreach (TileConfig child in config.Children)
            {
                TileViewModel? loadedChild = LoadTileConfiguration(child, tile.Id, usedIds, depth + 1);
                if (loadedChild != null)
                    tile.Children.Add(loadedChild);
            }
            return tile;
        }

        private static string InferTileType(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                return TileTypes.Folder;

            bool fileExists = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            string extension = System.IO.Path.GetExtension(path ?? string.Empty);
            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".url", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
                return TileTypes.Application;

            return fileExists || !string.IsNullOrEmpty(extension) ? TileTypes.File : TileTypes.Folder;
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

            if (!string.Equals(_activeCategory, categoryName, StringComparison.OrdinalIgnoreCase))
            {
                SaveActiveLayoutProfile();
                _navigationStack.Clear();
            }

            _activeCategory = categoryName;
            _activeGridColumns = GetCurrentGridColumnCount();
            ApplyLayoutProfile(categoryName, _activeGridColumns);
            ShowActiveTiles();

            if (_design.SmoothAnimations)
            {
                TilesContainer.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(140)));
            }
        }

        private ObservableCollection<TileViewModel>? GetActiveTiles()
        {
            if (_navigationStack.Count > 0)
                return _navigationStack[^1].Children;

            return _categoriesMap.TryGetValue(_activeCategory, out var list) ? list : null;
        }

        private void ShowActiveTiles()
        {
            if (!string.IsNullOrWhiteSpace(TileSearchTextBox.Text))
            {
                ShowGlobalSearchResults(TileSearchTextBox.Text);
                return;
            }

            CancelSystemSearch();
            ClearSearchMetadata();
            _isGlobalSearchActive = false;
            ObservableCollection<TileViewModel>? tiles = GetActiveTiles();
            if (tiles == null) return;

            tiles.CollectionChanged -= ActiveTiles_CollectionChanged;
            tiles.CollectionChanged += ActiveTiles_CollectionChanged;
            ICollectionView tilesView = CollectionViewSource.GetDefaultView(tiles);
            tilesView.Filter = null;
            TilesContainer.ItemsSource = tilesView;
            UpdateFolderNavigation();
            UpdateEmptyHint();
        }

        private void ShowGlobalSearchResults(string query)
        {
            _isGlobalSearchActive = true;
            TilesContainer.ItemsSource = _globalSearchResults;
            string normalizedQuery = query.Trim();
            if (string.Equals(_activeSearchQuery, normalizedQuery, StringComparison.Ordinal))
            {
                UpdateEmptyHint();
                return;
            }

            CancelSystemSearch();
            _activeSearchQuery = normalizedQuery;
            _searchError = null;
            _isSearchLoading = true;
            _globalSearchResults.Clear();
            UpdateFolderNavigation();
            UpdateEmptyHint();

            _searchCancellation = new CancellationTokenSource();
            int requestId = ++_searchRequestId;
            _ = SearchWindowsAsync(normalizedQuery, requestId, _searchCancellation.Token);
        }

        private async Task SearchWindowsAsync(string query, int requestId, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(300, cancellationToken);
                IReadOnlyList<WindowsSearchEntry> indexedResults =
                    await WindowsIndexedSearchService.SearchAsync(query, cancellationToken);

                if (cancellationToken.IsCancellationRequested || requestId != _searchRequestId)
                    return;

                string swappedQuery = StartMenuSearchService.SwapKeyboardLayout(query);
                var results = new Dictionary<string, (WindowsSearchEntry Entry, int Score)>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (WindowsSearchEntry entry in indexedResults)
                {
                    int score = StartMenuSearchService.ScoreMatch(
                        query, swappedQuery, entry.Name, entry.Path);
                    if (score > 0)
                        results[entry.Path] = (entry, score);
                }

                foreach (StartMenuAppEntry app in StartMenuSearchService.CachedApps)
                {
                    int score = StartMenuSearchService.ScoreMatch(
                        query, swappedQuery, app.Name, app.ShortcutPath);
                    if (score > 0 && !results.ContainsKey(app.ShortcutPath))
                    {
                        results[app.ShortcutPath] = (
                            new WindowsSearchEntry(app.Name, app.ShortcutPath, "application"),
                            score);
                    }
                }

                _globalSearchResults.Clear();
                foreach ((WindowsSearchEntry entry, int _) in results.Values
                    .OrderByDescending(result => result.Score)
                    .ThenBy(result => result.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Take(60))
                {
                    bool isFolder = entry.Kind.Contains("folder", StringComparison.OrdinalIgnoreCase) ||
                        Directory.Exists(entry.Path);
                    _globalSearchResults.Add(new TileViewModel
                    {
                        Name = entry.Name,
                        Path = entry.Path,
                        Type = isFolder ? TileTypes.Folder : InferTileType(entry.Path),
                        IsSearchResult = true,
                        SearchLocation = string.IsNullOrWhiteSpace(entry.DisplayPath)
                            ? "Этот компьютер"
                            : entry.DisplayPath,
                        IconSource = ResolveTileIcon(entry.Path, null)
                    });
                }

                _searchError = null;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested || requestId != _searchRequestId)
                    return;

                _searchError = ex.Message;
                _globalSearchResults.Clear();
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested && requestId == _searchRequestId)
                {
                    _isSearchLoading = false;
                    _searchCancellation?.Dispose();
                    _searchCancellation = null;
                    UpdateEmptyHint();
                }
            }
        }

        private void ClearSearchMetadata()
        {
            foreach (TileViewModel tile in _globalSearchResults)
            {
                tile.SearchLocation = string.Empty;
                tile.IsSearchResult = false;
            }
            _globalSearchResults.Clear();
        }

        private void CancelSystemSearch()
        {
            _searchRequestId++;
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = null;
            _activeSearchQuery = null;
            _isSearchLoading = false;
            _searchError = null;
        }

        private void ActiveTiles_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateEmptyHint();
        }

        private void UpdateFolderNavigation()
        {
            bool nested = _navigationStack.Count > 0;
            FolderNavigationBar.Visibility = nested && !_isGlobalSearchActive ? Visibility.Visible : Visibility.Collapsed;
            FolderBreadcrumbText.Text = nested
                ? string.Join("  /  ", new[] { _activeCategory }.Concat(_navigationStack.Select(folder => folder.Name)))
                : string.Empty;
        }

        private void NavigateBack_Click(object sender, RoutedEventArgs e)
        {
            if (_navigationStack.Count == 0) return;
            _navigationStack.RemoveAt(_navigationStack.Count - 1);
            ShowActiveTiles();
        }

        private void NavigateIntoFolder(TileViewModel folder)
        {
            if (!folder.IsInternalFolder) return;
            _navigationStack.Add(folder);
            ShowActiveTiles();
        }

        private void NavigateToSearchFolder(TileViewModel target)
        {
            foreach ((string categoryName, ObservableCollection<TileViewModel> roots) in _categoriesMap)
            {
                var folderPath = new List<TileViewModel>();
                if (!TryGetFolderPath(roots, target, folderPath))
                    continue;

                TileSearchTextBox.Clear();
                SelectCategory(categoryName);
                _navigationStack.Clear();
                _navigationStack.AddRange(folderPath);
                ShowActiveTiles();
                return;
            }
        }

        private void TileSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TilesContainer == null || TileSearchTextBox == null) return;
            ShowActiveTiles();
        }

        private void UpdateEmptyHint()
        {
            bool empty = _isGlobalSearchActive
                ? _globalSearchResults.Count == 0
                : GetActiveTiles() is not { Count: > 0 };
            EmptyStatePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (_isGlobalSearchActive)
            {
                EmptyStateTitle.Text = _isSearchLoading
                    ? "Поиск на компьютере…"
                    : _searchError != null
                        ? "Поиск Windows недоступен"
                        : "Ничего не найдено";
                EmptyStateDescription.Text = _isSearchLoading
                    ? "Ищем среди файлов и приложений, доступных через индекс Windows Search."
                    : _searchError
                        ?? "Проверьте раскладку клавиатуры или попробуйте другой запрос. Поиск охватывает расположения, индексируемые Windows Search.";
            }
            else
            {
                EmptyStateTitle.Text = "В этой папке пока пусто";
                EmptyStateDescription.Text = "Добавьте плитки кнопкой ниже или перетащите файлы из Проводника.";
            }
        }

        private void RefreshTileDisplayMetrics()
        {
            double spacing = _design.TileSpacing;
            void UpdateTree(IEnumerable<TileViewModel> tiles)
            {
                foreach (TileViewModel tile in tiles)
                {
                    tile.DisplayWidth = tile.ColumnSpan == 2 ? TileSize * 2 + spacing * 2 : TileSize;
                    UpdateTree(tile.Children);
                }
            }

            foreach (ObservableCollection<TileViewModel> category in _categoriesMap.Values)
                UpdateTree(category);
        }

        private int GetCurrentGridColumnCount()
        {
            double availableWidth = TilesScrollViewer?.ViewportWidth ?? 0;
            if (availableWidth <= 0)
                availableWidth = Math.Max(1, ActualWidth - 100);

            double spacing = _design.TileSpacing * 2;
            return Math.Max(1, (int)Math.Floor((availableWidth + spacing) / (TileSize + spacing)));
        }

        private void SaveActiveLayoutProfile()
        {
            if (_navigationStack.Count > 0 || _isApplyingLayoutProfile || _activeGridColumns <= 0 ||
                !_categoryLayouts.TryGetValue(_activeCategory, out Dictionary<int, List<string>>? profiles) ||
                GetActiveTiles() is not ObservableCollection<TileViewModel> tiles)
            {
                return;
            }

            profiles[_activeGridColumns] = tiles.Select(tile => tile.Id.ToString("D")).ToList();
        }

        private void ApplyLayoutProfile(string categoryName, int columns)
        {
            if (!_categoryLayouts.TryGetValue(categoryName, out Dictionary<int, List<string>>? profiles) ||
                !profiles.TryGetValue(columns, out List<string>? paths) ||
                !_categoriesMap.TryGetValue(categoryName, out ObservableCollection<TileViewModel>? tiles) ||
                tiles.Count < 2)
            {
                return;
            }

            var byPath = tiles
                .GroupBy(tile => tile.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var desiredOrder = new List<TileViewModel>(tiles.Count);
            foreach (string path in paths)
            {
                TileViewModel? tile = Guid.TryParse(path, out Guid tileId)
                    ? tiles.FirstOrDefault(candidate => candidate.Id == tileId)
                    : byPath.TryGetValue(path, out TileViewModel? legacyTile) ? legacyTile : null;
                string uniqueKey = tile?.Id.ToString("D") ?? path;
                if (tile != null && seenPaths.Add(uniqueKey))
                    desiredOrder.Add(tile);
            }
            desiredOrder.AddRange(tiles.Where(tile => seenPaths.Add(tile.Id.ToString("D"))));

            _isApplyingLayoutProfile = true;
            try
            {
                for (int targetIndex = 0; targetIndex < desiredOrder.Count; targetIndex++)
                {
                    if (ReferenceEquals(tiles[targetIndex], desiredOrder[targetIndex])) continue;
                    int currentIndex = tiles.IndexOf(desiredOrder[targetIndex]);
                    if (currentIndex >= 0)
                        tiles.Move(currentIndex, targetIndex);
                }
            }
            finally
            {
                _isApplyingLayoutProfile = false;
            }
        }

        private void UpdateActiveGridColumnProfile()
        {
            if (_isSyncingUI || string.IsNullOrEmpty(_activeCategory)) return;

            if (_navigationStack.Count > 0)
            {
                _design.WindowWidth = ActualWidth;
                _design.WindowHeight = ActualHeight;
                _resizeSaveTimer.Stop();
                _resizeSaveTimer.Start();
                return;
            }

            int columns = GetCurrentGridColumnCount();
            if (columns == _activeGridColumns) return;

            SaveActiveLayoutProfile();
            _activeGridColumns = columns;
            ApplyLayoutProfile(_activeCategory, columns);
            _design.WindowWidth = ActualWidth;
            _design.WindowHeight = ActualHeight;
            _resizeSaveTimer.Stop();
            _resizeSaveTimer.Start();
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isSyncingUI) return;

            UpdateBackgroundEffectLayout();
            UpdateActiveGridColumnProfile();
            _design.WindowWidth = ActualWidth;
            _design.WindowHeight = ActualHeight;
            _resizeSaveTimer.Stop();
            _resizeSaveTimer.Start();
        }

        private void SaveConfiguration()
        {
            try
            {
                Directory.CreateDirectory(_dataDir);
                File.WriteAllText(_configPath, JsonSerializer.Serialize(CreateConfigurationSnapshot(), CreateWriteOptions()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                MessageBox.Show($"Не удалось сохранить config.json:\n{ex.Message}",
                                "Nook", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private AppConfig CreateConfigurationSnapshot()
        {
            SaveActiveLayoutProfile();
            _design.WindowWidth = ActualWidth;
            _design.WindowHeight = ActualHeight;

            return new AppConfig
            {
                Settings = _design,
                Categories = _categoryOrder.Select(categoryName => new CategoryConfig
                {
                    Id = _categoryIds.TryGetValue(categoryName, out Guid categoryId) ? categoryId : Guid.NewGuid(),
                    Name = categoryName,
                    Tiles = _categoriesMap[categoryName].Select(tile => CreateTileConfig(tile, null)).ToList(),
                    ColumnLayouts = _categoryLayouts.TryGetValue(categoryName, out Dictionary<int, List<string>>? layouts)
                        ? layouts.ToDictionary(profile => profile.Key, profile => profile.Value.ToList())
                        : new Dictionary<int, List<string>>()
                }).ToList()
            };
        }

        private static TileConfig CreateTileConfig(TileViewModel tile, Guid? parentId)
        {
            return new TileConfig
            {
                Id = tile.Id,
                ParentId = parentId,
                Type = tile.Type,
                Name = tile.Name,
                Path = tile.Path,
                Icon = tile.CustomIconPath,
                ColumnSpan = tile.ColumnSpan,
                ShowLabel = tile.ShowLabel,
                LabelPlacement = tile.LabelPlacement,
                LabelAlignment = tile.LabelAlignment,
                Children = tile.Children.Select(child => CreateTileConfig(child, tile.Id)).ToList()
            };
        }

        private static JsonSerializerOptions CreateWriteOptions() => new()
        {
            WriteIndented = true,
            MaxDepth = 512,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic)
        };

        private static JsonSerializerOptions CreateReadOptions() => new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            MaxDepth = 512
        };

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
                JsonSerializerOptions readOptions = CreateReadOptions();

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

        private static ImageSource LoadNookIcon()
        {
            Uri resourceUri = new("pack://application:,,,/Assets/Nook.ico", UriKind.Absolute);
            StreamResourceInfo resource = Application.GetResourceStream(resourceUri)
                ?? throw new InvalidOperationException("Не удалось найти встроенную иконку Assets\\Nook.ico.");
            using (resource.Stream)
            {
                BitmapDecoder decoder = BitmapDecoder.Create(
                    resource.Stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                BitmapSource icon = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
                icon.Freeze();
                return icon;
            }
        }

        private static System.Drawing.Icon LoadNookTrayIcon()
        {
            Uri resourceUri = new("pack://application:,,,/Assets/Nook.ico", UriKind.Absolute);
            StreamResourceInfo resource = Application.GetResourceStream(resourceUri)
                ?? throw new InvalidOperationException("Не удалось найти встроенную иконку Assets\\Nook.ico.");
            using (resource.Stream)
                return new System.Drawing.Icon(resource.Stream);
        }

        // --- РУЧНОЕ ДОБАВЛЕНИЕ, DRAG & DROP ИЗ ПРОВОДНИКА И УПРАВЛЕНИЕ ПЛИТКАМИ ---

        private void AddTileManual_Click(object sender, RoutedEventArgs e)
        {
            ContextMenu? sourceMenu = GetMenuItemContextMenu(sender);
            if (sourceMenu != null)
                sourceMenu.IsOpen = false;

            string? selection = null;
            var picker = new Window
            {
                Title = "Добавить плитку",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 360,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("DrawerSurfaceBrush"),
                Foreground = (Brush)FindResource("PrimaryTextBrush")
            };
            var choices = new StackPanel { Margin = new Thickness(24) };
            choices.Children.Add(new TextBlock
            {
                Text = "Что добавить?",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 14)
            });
            AddChoice("Приложение или ярлык", "application");
            AddChoice("Файл", "file");
            AddChoice("Папку Windows", "folder");
            AddChoice("Внутреннюю папку Nook", "internal");
            picker.Content = choices;
            bool wasSuppressingHide = _suppressDeactivateHide;
            _suppressDeactivateHide = true;
            bool? accepted;
            try
            {
                accepted = picker.ShowDialog();
            }
            finally
            {
                _suppressDeactivateHide = wasSuppressingHide;
            }
            if (accepted != true) return;

            switch (selection)
            {
                case "application":
                    AddApplications_Click(this, new RoutedEventArgs());
                    break;
                case "file":
                    AddFiles_Click(this, new RoutedEventArgs());
                    break;
                case "folder":
                    AddFolderManual_Click(this, new RoutedEventArgs());
                    break;
                case "internal":
                    CreateInternalFolder_Click(this, new RoutedEventArgs());
                    break;
            }

            void AddChoice(string label, string value)
            {
                var choice = new Button
                {
                    Content = label,
                    Style = (Style)FindResource("PrimaryActionButtonStyle"),
                    Margin = new Thickness(0, 0, 0, 8),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(14, 10, 14, 10)
                };
                choice.Click += (_, _) =>
                {
                    selection = value;
                    picker.DialogResult = true;
                };
                choices.Children.Add(choice);
            }
        }

        private void AddApplications_Click(object sender, RoutedEventArgs e)
        {
            AddExternalItems(new OpenFileDialog
            {
                Title = "Выберите приложение или ярлык",
                Filter = "Программы и ярлыки (*.exe;*.lnk;*.url;*.bat;*.cmd)|*.exe;*.lnk;*.url;*.bat;*.cmd|Все файлы (*.*)|*.*",
                DereferenceLinks = false,
                Multiselect = true
            });
        }

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            AddExternalItems(new OpenFileDialog
            {
                Title = "Выберите файлы",
                Filter = "Все файлы (*.*)|*.*",
                DereferenceLinks = false,
                Multiselect = true
            });
        }

        private void AddExternalItems(OpenFileDialog dialog)
        {
            ObservableCollection<TileViewModel>? activeTiles = GetActiveTiles();
            if (activeTiles == null) return;

            _suppressDeactivateHide = true;
            try
            {
                if (dialog.ShowDialog(this) != true) return;
                foreach (string path in dialog.FileNames)
                    AddExternalTile(activeTiles, path, GetCurrentParentId());
                RefreshTileDisplayMetrics();
                RefreshTileSearchIfActive();
                SaveConfiguration();
                UpdateEmptyHint();
            }
            finally
            {
                _suppressDeactivateHide = false;
                Activate();
                Focus();
            }
        }

        private Guid? GetCurrentParentId() => _navigationStack.Count > 0 ? _navigationStack[^1].Id : null;

        private bool AddExternalTile(ObservableCollection<TileViewModel> target, string path, Guid? parentId)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            if (target.Any(tile => !tile.IsInternalFolder &&
                string.Equals(tile.Path, path, StringComparison.OrdinalIgnoreCase))) return false;

            target.Add(new TileViewModel
            {
                ParentId = parentId,
                Type = Directory.Exists(path) ? TileTypes.Folder : InferTileType(path),
                Name = GetDefaultDisplayName(path),
                Path = path,
                CustomIconPath = string.Empty,
                IconSource = ResolveTileIcon(path, null)
            });
            return true;
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
                        added |= AddExternalTile(activeTiles, folder, GetCurrentParentId());
                    }

                    if (added)
                    {
                        RefreshTileDisplayMetrics();
                        RefreshTileSearchIfActive();
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
                    addedAny |= AddExternalTile(activeTiles, path, GetCurrentParentId());
                }

                if (addedAny)
                {
                    RefreshTileDisplayMetrics();
                    RefreshTileSearchIfActive();
                    SaveConfiguration();
                    UpdateEmptyHint();
                }
            }
        }

        private TileViewModel? GetTileFromMenuItem(object sender)
        {
            ContextMenu? contextMenu = GetMenuItemContextMenu(sender);
            return contextMenu?.PlacementTarget is Button button && button.Tag is TileViewModel tile
                ? tile
                : null;
        }

        private static ContextMenu? GetMenuItemContextMenu(object sender)
        {
            if (sender is not MenuItem menuItem) return null;
            ItemsControl? owner = menuItem.Parent as ItemsControl;
            while (owner is MenuItem parentMenuItem)
                owner = parentMenuItem.Parent as ItemsControl;
            return owner as ContextMenu;
        }

        private void MoveTile_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? tile = GetTileFromMenuItem(sender);
            if (tile == null) return;
            GetMenuItemContextMenu(sender)?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            var destinations = new List<(string Label, string Category, ObservableCollection<TileViewModel> Tiles, TileViewModel? Parent)>();
            foreach (string categoryName in _categoryOrder)
            {
                if (!_categoriesMap.TryGetValue(categoryName, out ObservableCollection<TileViewModel>? roots))
                    continue;

                destinations.Add(($"{categoryName} (корень)", categoryName, roots, null));
                AddMoveDestinations(destinations, categoryName, roots, categoryName);
            }

            var dialog = new Window
            {
                Title = "Переместить плитку",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                Background = (Brush)FindResource("DrawerSurfaceBrush"),
                Foreground = (Brush)FindResource("PrimaryTextBrush")
            };
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock
            {
                Text = $"Выберите место для «{tile.Name}»",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });
            var destinationPicker = new ComboBox
            {
                ItemsSource = destinations.Select(destination => destination.Label).ToList(),
                SelectedIndex = 0,
                Margin = new Thickness(0, 0, 0, 14),
                Padding = new Thickness(8)
            };
            panel.Children.Add(destinationPicker);
            var moveButton = new Button
            {
                Content = "Переместить",
                Style = (Style)FindResource("PrimaryActionButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(14, 8, 14, 8)
            };
            moveButton.Click += (_, _) =>
            {
                if (destinationPicker.SelectedIndex < 0) return;
                var destination = destinations[destinationPicker.SelectedIndex];
                MoveTileToDestination(tile, destination.Category, destination.Tiles, destination.Parent);
                dialog.DialogResult = true;
            };
            panel.Children.Add(moveButton);
            dialog.Content = panel;

            bool wasSuppressingHide = _suppressDeactivateHide;
            _suppressDeactivateHide = true;
            try
            {
                dialog.ShowDialog();
            }
            finally
            {
                _suppressDeactivateHide = wasSuppressingHide;
            }
        }

        private static void AddMoveDestinations(
            List<(string Label, string Category, ObservableCollection<TileViewModel> Tiles, TileViewModel? Parent)> destinations,
            string categoryName,
            ObservableCollection<TileViewModel> tiles,
            string parentPath)
        {
            foreach (TileViewModel folder in tiles.Where(tile => tile.IsInternalFolder))
            {
                string folderPath = $"{parentPath}  ›  {folder.Name}";
                destinations.Add((folderPath, categoryName, folder.Children, folder));
                AddMoveDestinations(destinations, categoryName, folder.Children, folderPath);
            }
        }

        private void MoveTileToDestination(
            TileViewModel tile,
            string categoryName,
            ObservableCollection<TileViewModel> destination,
            TileViewModel? parentFolder)
        {
            ObservableCollection<TileViewModel>? source = FindTileCollection(tile);
            if (source == null) return;
            if (ReferenceEquals(source, destination)) return;
            if (parentFolder != null && IsDescendantOf(tile, parentFolder))
            {
                MessageBox.Show("Нельзя переместить папку внутрь самой себя или её дочерней папки.",
                    "Nook", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!tile.IsInternalFolder && destination.Any(existing => !ReferenceEquals(existing, tile) &&
                !existing.IsInternalFolder &&
                string.Equals(existing.Path, tile.Path, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("В выбранном месте уже есть плитка с этим путём.", "Nook",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            source.Remove(tile);
            destination.Add(tile);
            tile.ParentId = parentFolder?.Id;

            if (!string.Equals(_activeCategory, categoryName, StringComparison.OrdinalIgnoreCase))
                SelectCategory(categoryName);
            _navigationStack.Clear();
            if (parentFolder != null &&
                _categoriesMap.TryGetValue(categoryName, out ObservableCollection<TileViewModel>? roots))
            {
                var folderPath = new List<TileViewModel>();
                if (TryGetFolderPath(roots, parentFolder, folderPath))
                    _navigationStack.AddRange(folderPath);
            }

            ShowActiveTiles();
            SaveConfiguration();
        }

        private ObservableCollection<TileViewModel>? FindTileCollection(TileViewModel target)
        {
            foreach (ObservableCollection<TileViewModel> roots in _categoriesMap.Values)
            {
                ObservableCollection<TileViewModel>? found = FindTileCollection(roots, target);
                if (found != null) return found;
            }
            return null;
        }

        private static ObservableCollection<TileViewModel>? FindTileCollection(
            ObservableCollection<TileViewModel> collection,
            TileViewModel target)
        {
            if (collection.Contains(target)) return collection;
            foreach (TileViewModel tile in collection)
            {
                ObservableCollection<TileViewModel>? found = FindTileCollection(tile.Children, target);
                if (found != null) return found;
            }
            return null;
        }

        private static bool TryGetFolderPath(
            IEnumerable<TileViewModel> current,
            TileViewModel target,
            List<TileViewModel> path)
        {
            foreach (TileViewModel tile in current)
            {
                if (!tile.IsInternalFolder) continue;
                path.Add(tile);
                if (ReferenceEquals(tile, target) || TryGetFolderPath(tile.Children, target, path))
                    return true;
                path.RemoveAt(path.Count - 1);
            }
            return false;
        }

        private void CreateInternalFolder_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? selectedFolder = GetTileFromMenuItem(sender);
            ObservableCollection<TileViewModel>? target = selectedFolder?.IsInternalFolder == true
                ? selectedFolder.Children
                : GetActiveTiles();
            if (target == null) return;

            var folder = new TileViewModel
            {
                Type = TileTypes.InternalFolder,
                Name = "Новая папка",
                Path = string.Empty,
                ParentId = selectedFolder?.IsInternalFolder == true
                    ? selectedFolder.Id
                    : _navigationStack.Count > 0 ? _navigationStack[^1].Id : null,
                IconSource = LoadNookIcon()
            };
            target.Add(folder);
            if (selectedFolder?.IsInternalFolder == true)
            {
                if (_isGlobalSearchActive)
                    NavigateToSearchFolder(selectedFolder);
                else
                    NavigateIntoFolder(selectedFolder);
            }
            RefreshTileDisplayMetrics();
            PromptRenameTile(folder);
            SaveConfiguration();
        }

        private void DuplicateTile_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? source = GetTileFromMenuItem(sender);
            ObservableCollection<TileViewModel>? target = source == null ? null : FindTileCollection(source);
            if (source == null || target == null) return;

            TileViewModel duplicate = CloneTile(source, source.ParentId);
            duplicate.Name = $"{source.Name} — копия";
            target.Insert(target.IndexOf(source) + 1, duplicate);
            RefreshTileDisplayMetrics();
            RefreshTileSearchIfActive();
            SaveConfiguration();
        }

        private TileViewModel CloneTile(TileViewModel source, Guid? parentId)
        {
            var clone = new TileViewModel
            {
                ParentId = parentId,
                Type = source.Type,
                Name = source.Name,
                Path = source.Path,
                CustomIconPath = source.CustomIconPath,
                IconSource = source.IsInternalFolder && string.IsNullOrWhiteSpace(source.CustomIconPath)
                    ? LoadNookIcon()
                    : ResolveTileIcon(source.Path, source.CustomIconPath),
                ColumnSpan = source.ColumnSpan,
                ShowLabel = source.ShowLabel,
                LabelPlacement = source.LabelPlacement,
                LabelAlignment = source.LabelAlignment
            };
            foreach (TileViewModel child in source.Children)
                clone.Children.Add(CloneTile(child, clone.Id));
            return clone;
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

            if (tile.IsInternalFolder)
            {
                PromptRenameTile(tile);
                return;
            }

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
                    tile.Type = Directory.Exists(tile.Path) ? TileTypes.Folder : InferTileType(tile.Path);
                    tile.IconSource = ResolveTileIcon(tile.Path, tile.CustomIconPath);
                    RefreshTileSearchIfActive();
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
                    RefreshTileSearchIfActive();
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
            tile.IconSource = tile.IsInternalFolder ? LoadNookIcon() : ResolveTileIcon(tile.Path, null);
            SaveConfiguration();
        }

        private void RefreshTileSearchIfActive()
        {
            if (_isGlobalSearchActive && !string.IsNullOrWhiteSpace(TileSearchTextBox.Text))
                ShowGlobalSearchResults(TileSearchTextBox.Text);
        }

        private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? tile = GetTileFromMenuItem(sender);
            if (tile == null) return;

            bool isDirectory = Directory.Exists(tile.Path);
            if (!isDirectory && !File.Exists(tile.Path))
            {
                CheckTileAvailability(tile);
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = true
                };
                startInfo.ArgumentList.Add(isDirectory ? tile.Path : $"/select,{tile.Path}");
                Process.Start(startInfo);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                MessageBox.Show($"Не удалось открыть расположение плитки:\n{ex.Message}", "Nook",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CopyTilePath_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? tile = GetTileFromMenuItem(sender);
            if (tile == null) return;

            if (string.IsNullOrWhiteSpace(tile.Path))
            {
                MessageBox.Show("У этой плитки не указан путь.", "Nook",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Clipboard.SetText(tile.Path);
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                MessageBox.Show($"Не удалось скопировать путь:\n{ex.Message}", "Nook",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TileProperties_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? tile = GetTileFromMenuItem(sender);
            if (tile == null) return;

            string category = FindOwningCategory(tile) ?? "—";
            string status = tile.IsInternalFolder ? "Внутренняя страница Nook." : GetTileAvailabilityDescription(tile.Path);
            MessageBox.Show(
                $"Название: {tile.Name}\nТип: {tile.Type}\nКатегория: {category}\nПуть: {tile.Path}\nДоступность: {status}\nИконка: {(string.IsNullOrWhiteSpace(tile.CustomIconPath) ? "по умолчанию" : tile.CustomIconPath)}",
                "Свойства плитки",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void OpenWindowsProperties_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? tile = GetTileFromMenuItem(sender);
            if (tile == null || tile.IsInternalFolder)
            {
                if (tile != null)
                    TileProperties_Click(sender, e);
                return;
            }

            if (!File.Exists(tile.Path) && !Directory.Exists(tile.Path))
            {
                CheckTileAvailability(tile);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = tile.Path,
                    Verb = "properties",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
            {
                MessageBox.Show($"Не удалось открыть свойства файла Windows:\n{ex.Message}", "Nook",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RunAsAdministrator_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? tile = GetTileFromMenuItem(sender);
            if (tile == null)
                return;

            if (tile.Type != TileTypes.Application || !File.Exists(tile.Path))
            {
                MessageBox.Show(
                    "Запуск от имени администратора доступен для приложений и ярлыков приложений.",
                    "Nook",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            _suppressDeactivateHide = true;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = tile.Path,
                    Verb = "runas",
                    UseShellExecute = true
                };

                string? workingDirectory = System.IO.Path.GetDirectoryName(tile.Path);
                if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
                    startInfo.WorkingDirectory = workingDirectory;

                Process.Start(startInfo);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // The user cancelled the UAC prompt.
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
            {
                MessageBox.Show(
                    $"Не удалось запустить приложение от имени администратора:\n{ex.Message}",
                    "Nook",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _suppressDeactivateHide = false;
            }
        }

        private void CheckTileAvailability_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? tile = GetTileFromMenuItem(sender);
            if (tile != null)
                CheckTileAvailability(tile);
        }

        private void CheckTileAvailability(TileViewModel tile)
        {
            string status = tile.IsInternalFolder ? "Внутренняя папка Nook доступна." : GetTileAvailabilityDescription(tile.Path);
            MessageBox.Show(
                $"Плитка «{tile.Name}»:\n{tile.Path}\n\n{status}",
                "Проверка доступности",
                MessageBoxButton.OK,
                status == "Путь не найден." ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private static string GetTileAvailabilityDescription(string path)
        {
            if (File.Exists(path) || Directory.Exists(path))
                return "Файл или папка доступны.";

            if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) &&
                uri.IsAbsoluteUri &&
                !uri.IsFile)
            {
                return "Ссылка или URI сохранены; наличие внешнего обработчика проверить нельзя.";
            }

            return "Путь не найден.";
        }

        private string? FindOwningCategory(TileViewModel target)
        {
            foreach ((string name, ObservableCollection<TileViewModel> tiles) in _categoriesMap)
            {
                if (ContainsTile(tiles, target))
                    return name;
            }
            return null;
        }

        private static bool ContainsTile(IEnumerable<TileViewModel> tiles, TileViewModel target)
        {
            foreach (TileViewModel tile in tiles)
            {
                if (ReferenceEquals(tile, target) || ContainsTile(tile.Children, target))
                    return true;
            }
            return false;
        }

        private void ExportConfiguration_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Экспорт резервной копии Nook",
                Filter = "Файл конфигурации Nook (*.json)|*.json",
                DefaultExt = ".json",
                AddExtension = true,
                FileName = $"Nook-backup-{DateTime.Now:yyyyMMdd-HHmmss}.json"
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                string json = JsonSerializer.Serialize(CreateConfigurationSnapshot(), CreateWriteOptions());
                File.WriteAllText(dialog.FileName, json);
                MessageBox.Show("Резервная копия успешно экспортирована.", "Nook",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                MessageBox.Show($"Не удалось экспортировать конфигурацию:\n{ex.Message}", "Nook",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportConfiguration_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Импорт резервной копии Nook",
                Filter = "Файл конфигурации Nook (*.json)|*.json|Все файлы (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) != true) return;

            AppConfig importedConfig;
            try
            {
                string json = File.ReadAllText(dialog.FileName);
                importedConfig = JsonSerializer.Deserialize<AppConfig>(json, CreateReadOptions())
                    ?? throw new JsonException("Файл не содержит конфигурацию.");

                if (!ValidateImportedConfiguration(importedConfig, out string validationError))
                    throw new JsonException(validationError);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                MessageBox.Show($"Не удалось импортировать конфигурацию:\n{ex.Message}", "Nook",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            MessageBoxResult confirmation = MessageBox.Show(
                "Импорт заменит текущие категории, плитки и настройки. Перед заменой будет создана резервная копия текущего config.json. Продолжить?",
                "Импорт конфигурации",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes) return;

            string temporaryPath = System.IO.Path.Combine(_dataDir, $"config-import-{Guid.NewGuid():N}.tmp");
            string backupPath = System.IO.Path.Combine(_dataDir, $"config-backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
            bool hadExistingConfig = File.Exists(_configPath);

            try
            {
                Directory.CreateDirectory(_dataDir);
                if (hadExistingConfig)
                    File.Copy(_configPath, backupPath, overwrite: false);

                string normalizedJson = JsonSerializer.Serialize(importedConfig, CreateWriteOptions());
                File.WriteAllText(temporaryPath, normalizedJson);
                File.Move(temporaryPath, _configPath, overwrite: true);

                LoadConfiguration();
                SyncSettingsUI();
                UpdateBackgroundAnimations();
                RegisterCurrentHotkey(showWarningOnFail: true);

                string backupMessage = hadExistingConfig
                    ? $"\nТекущая конфигурация сохранена: {backupPath}"
                    : string.Empty;
                MessageBox.Show($"Конфигурация успешно импортирована.{backupMessage}", "Nook",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                try
                {
                    if (hadExistingConfig && File.Exists(backupPath))
                        File.Copy(backupPath, _configPath, overwrite: true);
                    else if (!hadExistingConfig && File.Exists(_configPath))
                        File.Delete(_configPath);

                    LoadConfiguration();
                    SyncSettingsUI();
                    UpdateBackgroundAnimations();
                    RegisterCurrentHotkey(showWarningOnFail: false);
                }
                catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException or JsonException)
                {
                    MessageBox.Show($"Не удалось завершить импорт ({ex.Message}) и восстановить прежнюю конфигурацию ({restoreException.Message}).",
                        "Nook", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                MessageBox.Show($"Импорт не выполнен. Предыдущая конфигурация восстановлена.\n{ex.Message}", "Nook",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
                {
                    MessageBox.Show($"Не удалось удалить временный файл импорта:\n{cleanupException.Message}", "Nook",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private static bool ValidateImportedConfiguration(AppConfig config, out string error)
        {
            if (config.Settings == null)
                config.Settings = new DesignSettings();

            if ((config.Categories == null || config.Categories.Count == 0) &&
                (config.Tiles == null || config.Tiles.Count == 0))
            {
                error = "В резервной копии нет ни категорий, ни плиток.";
                return false;
            }

            if (config.Categories != null)
            {
                foreach (CategoryConfig? category in config.Categories)
                {
                    if (category == null)
                    {
                        error = "В списке категорий обнаружена пустая запись.";
                        return false;
                    }

                    if (category.Tiles != null && category.Tiles.Any(tile => tile == null))
                    {
                        error = $"Категория «{category.Name}» содержит некорректную плитку.";
                        return false;
                    }

                    foreach (TileConfig tile in category.Tiles ?? new List<TileConfig>())
                    {
                        if (!ValidateTile(tile, 0, out error))
                            return false;
                    }

                    if (category.ColumnLayouts == null)
                        category.ColumnLayouts = new Dictionary<int, List<string>>();
                }
            }

            if (config.Categories == null || config.Categories.Count == 0)
            {
                foreach (TileConfig tile in config.Tiles ?? new List<TileConfig>())
                {
                    if (!ValidateTile(tile, 0, out error))
                        return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private static bool ValidateTile(TileConfig? tile, int depth, out string error)
        {
            if (tile == null)
            {
                error = "Обнаружена пустая плитка.";
                return false;
            }
            if (depth > 512)
            {
                error = "Вложенность внутренних папок превышает допустимый предел (512).";
                return false;
            }

            tile.Children ??= new List<TileConfig>();
            if (tile.Type is not ("" or TileTypes.Application or TileTypes.File or TileTypes.Folder or TileTypes.InternalFolder))
            {
                error = $"Плитка «{tile.Name}» имеет неизвестный тип «{tile.Type}».";
                return false;
            }
            if (!string.Equals(tile.Type, TileTypes.InternalFolder, StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(tile.Path))
            {
                error = $"У плитки «{tile.Name}» не указан путь.";
                return false;
            }
            if (!string.Equals(tile.Type, TileTypes.InternalFolder, StringComparison.Ordinal) && tile.Children.Count > 0)
            {
                error = $"Вложенные плитки разрешены только у внутренних папок («{tile.Name}»).";
                return false;
            }

            foreach (TileConfig child in tile.Children)
            {
                if (!ValidateTile(child, depth + 1, out error))
                    return false;
            }
            error = string.Empty;
            return true;
        }

        private void DeleteTile_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? tile = GetTileFromMenuItem(sender);
            ObservableCollection<TileViewModel>? activeTiles = tile == null ? null : FindTileCollection(tile);
            if (tile == null || activeTiles == null) return;

            activeTiles.Remove(tile);
            RefreshTileSearchIfActive();
            SaveConfiguration();
        }

        private void Tile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button clickedButton || clickedButton.Tag is not TileViewModel tile)
                return;

            if (tile.IsInternalFolder)
            {
                if (_isGlobalSearchActive)
                    NavigateToSearchFolder(tile);
                else
                    NavigateIntoFolder(tile);
                return;
            }

            if (tile.IsBackNavigation)
            {
                NavigateBack_Click(sender, e);
                return;
            }

            // В режиме правки клик по плитке не запускает игру случайно, а открывает переименование
            if (IsEditMode)
            {
                if (!_isInternalDragging && !tile.IsSearchResult)
                    PromptRenameTile(tile);
                if (!tile.IsSearchResult)
                    return;
            }

            if (LaunchItem(tile.Path) && _design.HideAfterLaunch)
                AnimateAndHide();
        }

        private void OpenTile_Click(object sender, RoutedEventArgs e)
        {
            TileViewModel? tile = GetTileFromMenuItem(sender);
            if (tile == null) return;
            if (tile.IsInternalFolder)
            {
                if (_isGlobalSearchActive)
                    NavigateToSearchFolder(tile);
                else
                    NavigateIntoFolder(tile);
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
                if (!_isDrawerOpen && _navigationStack.Count > 0) NavigateBack_Click(this, new RoutedEventArgs());
                else if (IsEditMode) SetEditMode(false);
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

        private void QuickAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string action })
                return;

            ProcessStartInfo startInfo = action switch
            {
                "TaskManager" => new ProcessStartInfo
                {
                    FileName = "taskmgr.exe",
                    UseShellExecute = true
                },
                "RecycleBin" => new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = true,
                    ArgumentList = { "shell:RecycleBinFolder" }
                },
                "Explorer" => new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = true
                },
                "WindowsSettings" => new ProcessStartInfo
                {
                    FileName = "ms-settings:",
                    UseShellExecute = true
                },
                _ => throw new InvalidOperationException($"Неизвестное быстрое действие: {action}")
            };

            try
            {
                Process.Start(startInfo);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
            {
                MessageBox.Show(
                    $"Не удалось выполнить действие:\n{ex.Message}",
                    "Nook",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
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
