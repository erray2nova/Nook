using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FastPush
{
    public sealed record StartMenuAppEntry(string Name, string ShortcutPath);

    public static class StartMenuSearchService
    {
        private const string EngChars = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>~";
        private const string RusChars = "йцукенгшщзхъфывапролджэячсмитьбюёЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮЁ";

        private static readonly string[] ExcludedKeywords =
        {
            "uninstall", "удалить", "удаление", "деинстал", "readme", "справка", "help", "manual", "website"
        };

        private static volatile List<StartMenuAppEntry> _cachedApps = new();

        public static IReadOnlyList<StartMenuAppEntry> CachedApps => _cachedApps;

        public static void RefreshIndexInBackground()
        {
            Task.Run(() =>
            {
                var result = new List<StartMenuAppEntry>();
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string[] roots =
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
                };

                var enumOptions = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true
                };

                foreach (string root in roots)
                {
                    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                        continue;

                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(root, "*.*", enumOptions))
                        {
                            string ext = Path.GetExtension(file);
                            if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                                !ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            string name = Path.GetFileNameWithoutExtension(file).Trim();
                            if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
                                continue;

                            if (ExcludedKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            if (seenNames.Add(name))
                            {
                                result.Add(new StartMenuAppEntry(name, file));
                            }
                        }
                    }
                    catch { }
                }

                _cachedApps = result;
            });
        }

        // Перевод ошибочно набранной раскладки (RU <-> EN)
        public static string SwapKeyboardLayout(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            var sb = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                int engIdx = EngChars.IndexOf(c);
                if (engIdx >= 0)
                {
                    sb.Append(RusChars[engIdx]);
                    continue;
                }

                int rusIdx = RusChars.IndexOf(c);
                if (rusIdx >= 0)
                {
                    sb.Append(EngChars[rusIdx]);
                    continue;
                }

                sb.Append(c);
            }
            return sb.ToString();
        }

        // Ранжированная оценка совпадения (0 = не совпало, до 120 = точное совпадение)
        public static int ScoreMatch(string rawQuery, string swappedQuery, string title, string? secondaryText = null)
        {
            if (string.IsNullOrWhiteSpace(rawQuery) || string.IsNullOrWhiteSpace(title))
                return 0;

            int direct = ScoreSingleQuery(rawQuery, title, secondaryText);
            int swapped = !string.Equals(rawQuery, swappedQuery, StringComparison.OrdinalIgnoreCase)
                ? ScoreSingleQuery(swappedQuery, title, secondaryText) - 4
                : 0;

            return Math.Max(direct, Math.Max(0, swapped));
        }

        private static int ScoreSingleQuery(string query, string title, string? secondaryText)
        {
            if (title.Equals(query, StringComparison.OrdinalIgnoreCase))
                return 120;

            if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                return 100;

            string[] tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 1)
            {
                bool allMatch = tokens.All(t =>
                    title.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(secondaryText) && secondaryText.Contains(t, StringComparison.OrdinalIgnoreCase)));

                return allMatch ? 78 : 0;
            }

            // Совпадение с начала любого слова в названии
            string[] words = title.Split(new[] { ' ', '-', '_', '.', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Any(w => w.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
                return 85;

            if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
                return 65;

            if (!string.IsNullOrEmpty(secondaryText) && secondaryText.Contains(query, StringComparison.OrdinalIgnoreCase))
                return 35;

            return 0;
        }
    }
}