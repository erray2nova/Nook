using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace FastPush
{
    public class AppConfig
    {
        public DesignSettings Settings { get; set; } = new();
        public List<CategoryConfig> Categories { get; set; } = new();

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<TileConfig>? Tiles { get; set; }
    }

    public class DesignSettings
    {
        // Black | White | Espresso | Matcha
        public string Theme { get; set; } = "Black";
        public string AccentColor { get; set; } = "#EC4899";

        // 0% (прозрачное размытое стекло) .. 100% (плотный матовый корпус)
        public int BlurOpacity { get; set; } = 72;

        public double TileSize { get; set; } = 150;
        public double TileSpacing { get; set; } = 14;
        public int TileOpacity { get; set; } = 100;
        public string GridPreset { get; set; } = "Standard";
        public double WindowWidth { get; set; } = 1280;
        public double WindowHeight { get; set; } = 720;

        // Legacy single-mode setting retained for older configuration files.
        public string BackgroundMode { get; set; } = "Combo";
        public List<string>? BackgroundEffects { get; set; }

        public bool ShowClock { get; set; } = true;
        public bool HideOnDeactivate { get; set; } = true;
        public bool HideAfterLaunch { get; set; } = true;
        public bool SmoothAnimations { get; set; } = true;

        // Глобальный хоткей (по умолчанию Ctrl + Alt + Space)
        public uint HotkeyModifiers { get; set; } = 0x0002 | 0x0001;
        public uint HotkeyVirtualKey { get; set; } = 0x20;
        public string HotkeyDisplay { get; set; } = "Ctrl + Alt + Space";
    }

    public class CategoryConfig
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public List<TileConfig> Tiles { get; set; } = new();
        public Dictionary<int, List<string>> ColumnLayouts { get; set; } = new();
    }

    public class TileConfig
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid? ParentId { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public List<TileConfig> Children { get; set; } = new();
        public int ColumnSpan { get; set; } = 1;
        public bool ShowLabel { get; set; } = true;
        public string LabelPlacement { get; set; } = "Inside";
        public string LabelAlignment { get; set; } = "Center";
    }

    public class TileViewModel : INotifyPropertyChanged
    {
        private string _type = TileTypes.Application;
        private string _name = string.Empty;
        private string _path = string.Empty;
        private ImageSource? _iconSource;
        private int _columnSpan = 1;
        private bool _showLabel = true;
        private string _labelPlacement = "Inside";
        private string _labelAlignment = "Center";
        private double _displayWidth = 150;
        private string _searchLocation = string.Empty;
        private bool _isSearchResult;

        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid? ParentId { get; set; }
        public ObservableCollection<TileViewModel> Children { get; } = new();

        public string Type
        {
            get => _type;
            set { if (_type != value) { _type = value; OnPropertyChanged(); } }
        }

        public bool IsInternalFolder => string.Equals(Type, TileTypes.InternalFolder, StringComparison.Ordinal);
        public bool IsBackNavigation => string.Equals(Type, TileTypes.Back, StringComparison.Ordinal);
        public bool IsWide => ColumnSpan > 1;

        public int ColumnSpan
        {
            get => _columnSpan;
            set { if (_columnSpan != value) { _columnSpan = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsWide)); } }
        }

        public bool ShowLabel
        {
            get => _showLabel;
            set { if (_showLabel != value) { _showLabel = value; OnPropertyChanged(); } }
        }

        public string LabelPlacement
        {
            get => _labelPlacement;
            set { if (_labelPlacement != value) { _labelPlacement = value; OnPropertyChanged(); } }
        }

        public string LabelAlignment
        {
            get => _labelAlignment;
            set { if (_labelAlignment != value) { _labelAlignment = value; OnPropertyChanged(); } }
        }

        public double DisplayWidth
        {
            get => _displayWidth;
            set { if (Math.Abs(_displayWidth - value) > 0.1) { _displayWidth = value; OnPropertyChanged(); } }
        }

        public string SearchLocation
        {
            get => _searchLocation;
            set { if (_searchLocation != value) { _searchLocation = value; OnPropertyChanged(); } }
        }

        public bool IsSearchResult
        {
            get => _isSearchResult;
            set { if (_isSearchResult != value) { _isSearchResult = value; OnPropertyChanged(); } }
        }

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        public string Path
        {
            get => _path;
            set { if (_path != value) { _path = value; OnPropertyChanged(); } }
        }

        public string CustomIconPath { get; set; } = string.Empty;

        public ImageSource? IconSource
        {
            get => _iconSource;
            set { if (_iconSource != value) { _iconSource = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public static class TileTypes
    {
        public const string Application = "Application";
        public const string File = "File";
        public const string Folder = "Folder";
        public const string InternalFolder = "InternalFolder";
        public const string Back = "Back";
    }
}