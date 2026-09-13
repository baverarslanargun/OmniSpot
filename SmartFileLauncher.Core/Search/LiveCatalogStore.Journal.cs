using System.Globalization;
using System.Text.Json;

namespace SmartFileLauncher.Core.Search;

internal sealed partial class LiveCatalogStore
{
    private sealed record PageChange(int Index, LivePages.PageReference Page);
    private sealed record AreaDifference(string Name, int Length, int Count, PageChange[] Changes);
    private sealed record FrameDifference(int DeltaVersion, long FromSequence, LiveCatalog.Frame Header, AreaDifference[] Areas);

    private static string CommitPath(string directory, long sequence) => Path.Combine(directory, "commit-" + sequence.ToString("D20", CultureInfo.InvariantCulture) + ".bin");
    private static IEnumerable<(long Sequence, string Path)> CommitFiles(string directory) =>
        Directory.EnumerateFiles(directory, "commit-*.bin", SearchOption.TopDirectoryOnly)
            .Select(path => (Sequence: long.Parse(Path.GetFileNameWithoutExtension(path).AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture), Path: path))
            .OrderBy(item => item.Sequence);
    private static FrameDifference ReadDifference(string path) =>
        JsonSerializer.Deserialize<FrameDifference>(ReadChecked(path)) ?? throw new InvalidDataException("Live başlık farkı yok.");
    private static FrameDifference Difference(LiveCatalog.Frame previous, LiveCatalog.Frame current) =>
        new(1, previous.Sequence, current with { Areas = [] }, current.Areas.Select((area, index) =>
            new AreaDifference(area.Name, area.Length, area.Pages.Length, area.Pages.Select((page, id) => new PageChange(id, page))
                .Where(change => change.Index >= previous.Areas[index].Pages.Length || change.Page != previous.Areas[index].Pages[change.Index]).ToArray())).ToArray());
    private static LiveCatalog.Frame ApplyDifference(LiveCatalog.Frame previous, FrameDifference difference)
    {
        var header = difference.Header;
        if (difference.DeltaVersion != 1 || difference.FromSequence != previous.Sequence || header.Sequence != checked(previous.Sequence + 1) ||
            header.Root != previous.Root || header.IndexedUtc != previous.IndexedUtc || header.Contract != previous.Contract ||
            header.Areas.Length != 0 || difference.Areas.Length != previous.Areas.Length)
            throw new InvalidDataException("Live başlık farkının sırası veya kökü geçersiz.");
        var areas = new LiveCatalog.PageArea[previous.Areas.Length];
        for (var index = 0; index < areas.Length; index++)
        {
            var source = previous.Areas[index]; var delta = difference.Areas[index];
            if (delta.Name != source.Name || delta.Length < 0 || delta.Count != (delta.Length + (long)LivePages.PageSize - 1) / LivePages.PageSize)
                throw new InvalidDataException("Live başlık farkının sayfa sayısı geçersiz.");
            var pages = new LivePages.PageReference[delta.Count];
            Array.Copy(source.Pages, pages, Math.Min(source.Pages.Length, pages.Length));
            var last = -1;
            foreach (var change in delta.Changes)
            {
                if (change.Index <= last || change.Index >= pages.Length || change.Page is null) throw new InvalidDataException("Live başlık farkının sayfası geçersiz.");
                pages[change.Index] = change.Page; last = change.Index;
            }
            if (pages.Any(page => page is null)) throw new InvalidDataException("Live başlık farkında eksik sayfa var.");
            areas[index] = new(delta.Name, delta.Length, pages);
        }
        return header with { Areas = areas };
    }
    private void Checkpoint()
    {
        WriteHead(_directory, _frame); _checkpointSequence = _frame.Sequence;
        foreach (var file in CommitFiles(_directory))
            if (file.Sequence <= _checkpointSequence) File.Delete(file.Path);
    }
}
