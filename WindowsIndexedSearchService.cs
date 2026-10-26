using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace FastPush
{
    public sealed record WindowsSearchEntry(string Name, string Path, string Kind, string DisplayPath = "");

    public static class WindowsIndexedSearchService
    {
        private const int MaximumResults = 60;

        public static Task<IReadOnlyList<WindowsSearchEntry>> SearchAsync(
            string query,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Task.FromResult<IReadOnlyList<WindowsSearchEntry>>(Array.Empty<WindowsSearchEntry>());

            return Task.Run(() => Search(query.Trim(), cancellationToken), cancellationToken);
        }

        private static IReadOnlyList<WindowsSearchEntry> Search(string query, CancellationToken cancellationToken)
        {
            Type? connectionType = Type.GetTypeFromProgID("ADODB.Connection");
            if (connectionType == null)
                throw new InvalidOperationException("Компонент доступа к индексу Windows Search недоступен.");

            object? connection = null;
            object? recordset = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                connection = Activator.CreateInstance(connectionType)
                    ?? throw new InvalidOperationException("Не удалось подключиться к индексу Windows Search.");

                dynamic adoConnection = connection;
                adoConnection.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");

                string[] searchTerms = query
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(EscapeLikeQuery)
                    .Where(term => !string.IsNullOrWhiteSpace(term))
                    .ToArray();
                if (searchTerms.Length == 0)
                    return Array.Empty<WindowsSearchEntry>();

                string nameCondition = string.Join(" AND ",
                    searchTerms.Select(term => $"System.ItemName LIKE '%{term}%'"));
                string sql = $"SELECT TOP {MaximumResults} System.ItemName, System.ItemUrl, System.ItemPathDisplay, System.Kind " +
                    "FROM SystemIndex " +
                    $"WHERE {nameCondition} " +
                    "ORDER BY System.ItemName";
                recordset = adoConnection.Execute(sql);
                dynamic rows = recordset;
                var results = new List<WindowsSearchEntry>(MaximumResults);
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                while (!rows.EOF && results.Count < MaximumResults)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = ReadField(rows, "System.ItemName");
                    string displayPath = ReadField(rows, "System.ItemPathDisplay");
                    string path = GetFileSystemPath(ReadField(rows, "System.ItemUrl"));
                    string kind = ReadField(rows, "System.Kind");
                    if (string.IsNullOrWhiteSpace(path))
                        path = displayPath;
                    if (!string.IsNullOrWhiteSpace(path) && seenPaths.Add(path))
                        results.Add(new WindowsSearchEntry(
                            string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileName(path) : name,
                            path,
                            kind,
                            displayPath));
                    rows.MoveNext();
                }

                return results;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "Не удалось выполнить поиск в индексе Windows Search. Проверьте, что служба Windows Search включена.",
                    ex);
            }
            finally
            {
                ReleaseComObject(recordset);
                ReleaseComObject(connection);
            }
        }

        private static string EscapeLikeQuery(string query)
        {
            return query.Replace("'", "''", StringComparison.Ordinal)
                .Replace("%", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("[", string.Empty, StringComparison.Ordinal)
                .Replace("]", string.Empty, StringComparison.Ordinal);
        }

        private static string GetFileSystemPath(string itemUrl)
        {
            if (string.IsNullOrWhiteSpace(itemUrl))
                return string.Empty;

            if (Uri.TryCreate(itemUrl, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                return uri.LocalPath;

            return itemUrl;
        }

        private static string ReadField(dynamic recordset, string fieldName)
        {
            object? value = recordset.Fields.Item(fieldName).Value;
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static void ReleaseComObject(object? value)
        {
            if (value != null && Marshal.IsComObject(value))
                Marshal.FinalReleaseComObject(value);
        }
    }
}
