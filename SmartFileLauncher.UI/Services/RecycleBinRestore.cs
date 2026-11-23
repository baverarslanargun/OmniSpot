using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SmartFileLauncher.UI.Services;

internal sealed record RecycledEntry(string Folder, string Name, DateTime DeletedUtc, object? Item = null);

internal sealed record RecycleRestoreResult(
    IReadOnlyList<string> Restored,
    IReadOnlyList<string> NotFound,
    IReadOnlyList<string> Occupied)
{
    internal static RecycleRestoreResult Empty(IReadOnlyList<string> paths) => new([], paths, []);
}

internal static class RecycleBinRestore
{
    private const int RecycleBinNamespace = 10;
    private const string RestoreVerb = "undelete";
    private static readonly TimeSpan ClockSlack = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestoreWait = TimeSpan.FromSeconds(5);

    internal static RecycleRestoreResult Restore(IReadOnlyList<string> paths, DateTime deletedAfterUtc)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Geri Dönüşüm Kutusu yalnız STA iş parçacığından okunabilir.");
        var shellType = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("Windows kabuğu kullanılamıyor.");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Windows kabuğu başlatılamadı.");
        var comObjects = new List<object> { shell };
        try
        {
            var bin = shell.Namespace(RecycleBinNamespace);
            if (bin == null) return RecycleRestoreResult.Empty(paths);
            comObjects.Add(bin);
            var items = bin.Items();
            comObjects.Add(items);
            var entries = new List<RecycledEntry>();
            int count = items.Count;
            for (var index = 0; index < count; index++)
            {
                var item = items.Item(index);
                if (item == null) continue;
                comObjects.Add(item);
                RecycledEntry? entry = ReadEntry(item);
                if (entry != null) entries.Add(entry);
            }

            var matches = Match(entries, paths, deletedAfterUtc);
            var occupied = new List<string>();
            var pending = new List<string>();
            foreach (var path in paths)
            {
                if (!matches.TryGetValue(path, out var entry)) continue;
                if (Exists(path))
                {
                    occupied.Add(path);
                    continue;
                }
                ((dynamic)entry.Item!).InvokeVerb(RestoreVerb);
                pending.Add(path);
            }

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < RestoreWait && pending.Any(path => !Exists(path))) Thread.Sleep(50);
            var restored = pending.Where(Exists).ToArray();
            var notFound = paths.Where(path => !restored.Contains(path) && !occupied.Contains(path)).ToArray();
            return new RecycleRestoreResult(restored, notFound, occupied);
        }
        finally
        {
            for (var index = comObjects.Count - 1; index >= 0; index--)
            {
                if (Marshal.IsComObject(comObjects[index])) Marshal.FinalReleaseComObject(comObjects[index]);
            }
        }
    }

    internal static IReadOnlyDictionary<string, RecycledEntry> Match(
        IEnumerable<RecycledEntry> entries,
        IEnumerable<string> paths,
        DateTime deletedAfterUtc)
    {
        var floor = deletedAfterUtc - ClockSlack;
        var candidates = entries.Where(entry => entry.DeletedUtc >= floor).ToList();
        var used = new HashSet<RecycledEntry>(ReferenceEqualityComparer.Instance);
        var matches = new Dictionary<string, RecycledEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var trimmed = path.TrimEnd('\\', '/');
            var folder = Path.GetDirectoryName(trimmed);
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name)) continue;
            var best = candidates
                .Where(entry => !used.Contains(entry) &&
                    string.Equals(entry.Folder.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.DeletedUtc)
                .FirstOrDefault();
            if (best == null) continue;
            used.Add(best);
            matches[path] = best;
        }
        return matches;
    }

    private static RecycledEntry? ReadEntry(dynamic item)
    {
        object? folder = item.ExtendedProperty("System.Recycle.DeletedFrom");
        object? name = item.ExtendedProperty("System.FileName");
        object? deleted = item.ExtendedProperty("System.Recycle.DateDeleted");
        return folder is string folderText && name is string nameText && deleted is DateTime time
            ? new RecycledEntry(folderText, nameText, LatestUtcReading(time), (object)item)
            : null;
    }

    internal static DateTime LatestUtcReading(DateTime shellTime)
    {
        var asUtc = DateTime.SpecifyKind(shellTime, DateTimeKind.Utc);
        var fromLocal = DateTime.SpecifyKind(shellTime, DateTimeKind.Local).ToUniversalTime();
        return asUtc > fromLocal ? asUtc : fromLocal;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
