using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.ExceptionServices;
using SmartFileLauncher.Core.Index;

namespace SmartFileLauncher.Core.Indexing.Folders;

internal readonly record struct FolderItem(string Name, FileAttributes Attributes, long SizeBytes, long Modified, long Created)
{
    internal bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    internal bool Descends => IsDirectory && (Attributes & FileAttributes.ReparsePoint) == 0;

    internal IndexEntry ToEntry(ulong reference, ulong parent) =>
        new(reference, parent, Name, Attributes, IsDirectory ? 0 : SizeBytes, Modified, Created);
}

internal sealed class FolderReferences(ulong next)
{
    private long _next = checked((long)Math.Max(next, FolderVolumeScanner.FirstReference));

    internal ulong Take() => (ulong)(Interlocked.Increment(ref _next) - 1);
}

internal static class FolderVolumeScanner
{
    internal const ulong RootReference = 5;
    internal const ulong FirstReference = 16;
    private const int ProgressStep = 16384;

    private static readonly EnumerationOptions Listing = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024
    };

    internal static IReadOnlyList<string> Roots(string volume, IReadOnlyList<string>? scope)
    {
        if (scope is null) return [volume];
        var result = new List<string>();
        foreach (var path in scope.Select(Full).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length))
            if (!result.Any(root => Contains(root, path))) result.Add(path);
        return result;
    }

    internal static bool Contains(string root, string path)
    {
        if (string.Equals(Key(root), Key(path), StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static string Full(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + Path.DirectorySeparatorChar : trimmed;
    }

    internal static string? Parent(string path) => Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));

    private static string Key(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    internal static List<IndexEntry> Scan(string volume, IReadOnlyList<string>? scope, FolderReferences ids,
        Action<int>? progress, CancellationToken cancellationToken)
    {
        var entries = new List<IndexEntry> { new(RootReference, RootReference, ".", FileAttributes.Directory, 0, 0) };
        var known = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase) { [Key(volume)] = RootReference };
        var starts = new List<(string Path, ulong Reference)>();
        foreach (var root in Roots(volume, scope))
        {
            var reference = Ensure(root, known, entries, ids);
            if (reference != 0) starts.Add((root, reference));
        }
        Walk(starts, entries, ids, progress, cancellationToken);
        return entries;
    }

    private static ulong Ensure(string path, Dictionary<string, ulong> known, List<IndexEntry> entries, FolderReferences ids)
    {
        if (known.TryGetValue(Key(path), out var existing)) return existing;
        var parentPath = Parent(path);
        if (parentPath is null) return 0;
        var parent = Ensure(parentPath, known, entries, ids);
        if (parent == 0) return 0;
        var info = new DirectoryInfo(path);
        FileAttributes attributes;
        long modified, created;
        try
        {
            if (!info.Exists) return 0;
            attributes = info.Attributes | FileAttributes.Directory;
            modified = info.LastWriteTimeUtc.Ticks;
            created = info.CreationTimeUtc.Ticks;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        var reference = ids.Take();
        entries.Add(new(reference, parent, info.Name, attributes, 0, modified, created));
        known[Key(path)] = reference;
        return reference;
    }

    internal static bool TryRead(string path, List<FolderItem> items)
    {
        items.Clear();
        try
        {
            var enumerable = new FileSystemEnumerable<FolderItem>(path,
                static (ref FileSystemEntry entry) => new FolderItem(entry.FileName.ToString(), entry.Attributes, entry.Length,
                    entry.LastWriteTimeUtc.UtcTicks, entry.CreationTimeUtc.UtcTicks), Listing);
            foreach (var item in enumerable) items.Add(item);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            items.Clear();
            return false;
        }
    }

    internal static void Walk(IReadOnlyList<(string Path, ulong Reference)> starts, List<IndexEntry> output, FolderReferences ids,
        Action<int>? progress, CancellationToken cancellationToken)
    {
        if (starts.Count == 0) return;
        var pending = new ConcurrentQueue<(string Path, ulong Reference)>(starts);
        var outstanding = (long)starts.Count;
        var total = 0;
        var reported = 0;
        var workers = Math.Clamp(Environment.ProcessorCount, 1, 8);
        var results = new List<IndexEntry>[workers];
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
            {
                var local = new List<IndexEntry>();
                results[worker] = local;
                var items = new List<FolderItem>();
                var spin = new SpinWait();
                try
                {
                    while (true)
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        if (!pending.TryDequeue(out var folder))
                        {
                            if (Interlocked.Read(ref outstanding) == 0) return;
                            spin.SpinOnce(sleep1Threshold: 10);
                            continue;
                        }
                        spin.Reset();
                        var before = local.Count;
                        if (TryRead(folder.Path, items))
                            foreach (var item in items)
                            {
                                var reference = ids.Take();
                                local.Add(item.ToEntry(reference, folder.Reference));
                                if (!item.Descends) continue;
                                Interlocked.Increment(ref outstanding);
                                pending.Enqueue((Path.Join(folder.Path, item.Name), reference));
                            }
                        Interlocked.Decrement(ref outstanding);
                        var count = Interlocked.Add(ref total, local.Count - before);
                        if (progress is null || count - Volatile.Read(ref reported) < ProgressStep) continue;
                        Volatile.Write(ref reported, count);
                        progress(count);
                    }
                }
                catch
                {
                    stop.Cancel();
                    throw;
                }
            });
        }
        catch (AggregateException failure)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cause = failure.InnerExceptions.FirstOrDefault(inner => inner is not OperationCanceledException) ?? failure.InnerExceptions[0];
            ExceptionDispatchInfo.Throw(cause);
        }
        foreach (var list in results)
            if (list is not null) output.AddRange(list);
    }
}
