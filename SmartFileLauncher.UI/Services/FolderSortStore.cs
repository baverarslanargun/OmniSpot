using System.IO;
using System.Text.Json;
using SmartFileLauncher.Core.Filtering;

namespace SmartFileLauncher.UI.Services;

internal sealed record FolderSort(string Path, ItemSort Sort);

internal sealed class FolderSortStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly Action<string>? _log;

    internal FolderSortStore(string path, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _log = log;
    }

    internal IReadOnlyList<FolderSort> Load()
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<FolderSort>();
        }

        try
        {
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path));
            var folders = new List<FolderSort>();
            foreach (var entry in document?.Folders ?? [])
            {
                if (entry?.Path is { } path &&
                    Enum.TryParse<SortField>(entry.Sort, out var field) &&
                    Enum.IsDefined(field) &&
                    field != SortField.Relevance)
                {
                    folders.Add(new FolderSort(path, new ItemSort(field, entry.Descending)));
                }
            }

            return folders;
        }
        catch (Exception ex) when (
            ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log?.Invoke($"⚠️ Klasör sıralamaları okunamadı: {ex.Message}");
            return Array.Empty<FolderSort>();
        }
    }

    internal void Save(IReadOnlyList<FolderSort> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var temporaryPath = _path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var document = new Document
            {
                Folders = folders
                    .Select(folder => (Entry?)new Entry
                    {
                        Path = folder.Path,
                        Sort = folder.Sort.Field.ToString(),
                        Descending = folder.Sort.Descending
                    })
                    .ToList()
            };
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, SerializerOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"⚠️ Klasör sıralamaları kaydedilemedi: {ex.Message}");
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class Document
    {
        public List<Entry?>? Folders { get; set; }
    }

    private sealed class Entry
    {
        public string? Path { get; set; }

        public string? Sort { get; set; }

        public bool Descending { get; set; }
    }
}
