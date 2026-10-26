using System.Collections.Generic;
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

        // Combo | Aurora | Grid | Waves | Bokeh | None
        public string BackgroundMode { get; set; } = "Combo";

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
        public string Name { get; set; } = string.Empty;
        public List<TileConfig> Tiles { get; set; } = new();
        public Dictionary<int, List<string>> ColumnLayouts { get; set; } = new();
    }

    public class TileConfig
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
    }

    public class TileViewModel : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _path = string.Empty;
        private ImageSource? _iconSource;

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
}