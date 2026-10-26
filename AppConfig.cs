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
        public int BlurOpacity { get; set; } = 82;
        public bool AnimateBubbles { get; set; } = true;
        public bool AnimateGrid { get; set; } = true;
        public bool HideAfterLaunch { get; set; } = true;
        public bool SmoothAnimations { get; set; } = true;
    }

    public class CategoryConfig
    {
        public string Name { get; set; } = string.Empty;
        public List<TileConfig> Tiles { get; set; } = new();
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

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Path { get; set; } = string.Empty;
        public string CustomIconPath { get; set; } = string.Empty;
        public ImageSource? IconSource { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}