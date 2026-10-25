using System.Collections.Generic;
using System.Windows.Media;

namespace FastPush
{
    // Корневой объект JSON-файла
    public class AppConfig
    {
        public List<TileConfig> Tiles { get; set; } = new();
    }

    // Описание одной плитки внутри JSON
    public class TileConfig
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
    }

    // Готовая модель для отрисовки в WPF (уже с загруженной картинкой ImageSource)
    public class TileViewModel
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public ImageSource? IconSource { get; set; }
    }
}