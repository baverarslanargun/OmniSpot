using System.Diagnostics;
using SmartFileLauncher.Core.Application.Settings;

namespace SmartFileLauncher.Core.Search.Paged;

internal static class IndexSorter
{
    internal static int Sort<T>(T[] values, Comparison<T> compare, CancellationToken ct, IndexBuildOptions? options = null)
    {
        ct.ThrowIfCancellationRequested();
        if (values.Length < 2) return 1;
        var workers = options is { AdaptiveSort: true } ? SelectWorkers(values, compare, options.SortMethod, ct)
            : options?.SortWorkers ?? 1;
        Execute(values, compare, workers, options?.SortMethod ?? IndexSortMethod.Merge, ct);
        ct.ThrowIfCancellationRequested();
        return workers;
    }

    private static int SelectWorkers<T>(T[] values, Comparison<T> compare, IndexSortMethod method, CancellationToken ct)
    {
        var count = Math.Min(values.Length, 262144);
        var sample = new T[count];
        for (var i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            sample[i] = values[(int)((long)i * values.Length / count)];
        }
        var warmCount = Math.Min(count, method == IndexSortMethod.Pivot ? 16384 : 4096);
        Execute(sample.AsSpan(0, warmCount).ToArray(), compare, 1, method, ct);
        Execute(sample.AsSpan(0, warmCount).ToArray(), compare, 2, method, ct);
        var selected = 1;
        var best = double.PositiveInfinity;
        foreach (var workers in new[] { 1, 2, 4, 8, 16 })
        {
            if (workers > Environment.ProcessorCount || workers > count) break;
            ct.ThrowIfCancellationRequested();
            var copy = (T[])sample.Clone();
            var clock = Stopwatch.StartNew();
            Execute(copy, compare, workers, method, ct);
            var elapsed = clock.Elapsed.TotalMilliseconds;
            if (elapsed < best * .97) { best = elapsed; selected = workers; }
        }
        return selected;
    }

    private static void Execute<T>(T[] values, Comparison<T> compare, int workers, IndexSortMethod method, CancellationToken ct)
    {
        workers = Math.Min(Math.Clamp(workers, 1, 16), Math.Max(1, values.Length));
        if (workers == 1) { RangeSort(values, 0, values.Length, compare, ct); return; }
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct };
        if (method == IndexSortMethod.Pivot)
        {
            var ranges = new List<(int Start, int Length)> { (0, values.Length) };
            var target = Math.Min(64, workers * 4);
            while (ranges.Count < target)
            {
                ct.ThrowIfCancellationRequested();
                var flags = new bool[ranges.Count];
                foreach (var at in Enumerable.Range(0, ranges.Count).Where(i => ranges[i].Length >= 8192)
                    .OrderByDescending(i => ranges[i].Length).Take(target - ranges.Count)) flags[at] = true;
                if (!flags.Any(value => value)) break;
                var splits = new (int Start, int Length)[ranges.Count][];
                Parallel.For(0, ranges.Count, parallel, i =>
                {
                    var range = ranges[i];
                    if (!flags[i]) { splits[i] = [range]; return; }
                    var comparer = CheckedComparer(compare, ct);
                    var sample = new T[9];
                    for (var at = 0; at < 9; at++) sample[at] = values[range.Start + (int)((long)at * (range.Length - 1) / 8)];
                    RangeSort(sample, 0, sample.Length, compare, ct);
                    var pivot = sample[4];
                    var left = range.Start;
                    var end = range.Start + range.Length;
                    var right = end - 1;
                    while (left <= right)
                    {
                        ct.ThrowIfCancellationRequested();
                        while (left < end && comparer.Compare(values[left], pivot) < 0) left++;
                        while (right >= range.Start && comparer.Compare(values[right], pivot) > 0) right--;
                        if (left > right) break;
                        (values[left], values[right]) = (values[right], values[left]);
                        left++; right--;
                    }
                    splits[i] = left == range.Start || left == end ? [range]
                        : [(range.Start, left - range.Start), (left, end - left)];
                });
                var next = splits.SelectMany(split => split).ToList();
                if (next.Count == ranges.Count) break;
                ranges = next;
            }
            Parallel.ForEach(ranges, parallel, range => RangeSort(values, range.Start, range.Length, compare, ct));
            return;
        }
        var starts = new int[workers + 1];
        for (var i = 0; i <= workers; i++) starts[i] = (int)((long)i * values.Length / workers);
        Parallel.For(0, workers, parallel, i => RangeSort(values, starts[i], starts[i + 1] - starts[i], compare, ct));
        var merged = new T[values.Length];
        var heads = new PriorityQueue<(int At, int End), T>(CheckedComparer(compare, ct));
        for (var i = 0; i < workers; i++) heads.Enqueue((starts[i], starts[i + 1]), values[starts[i]]);
        for (var i = 0; i < merged.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            var head = heads.Dequeue();
            merged[i] = values[head.At];
            if (++head.At < head.End) heads.Enqueue(head, values[head.At]);
        }
        merged.CopyTo(values, 0);
    }

    private static IComparer<T> CheckedComparer<T>(Comparison<T> compare, CancellationToken ct)
    {
        var count = 0;
        return Comparer<T>.Create((a, b) => { if ((++count & 4095) == 0) ct.ThrowIfCancellationRequested(); return compare(a, b); });
    }

    private static void RangeSort<T>(T[] values, int start, int length, Comparison<T> compare, CancellationToken ct)
    {
        try { Array.Sort(values, start, length, CheckedComparer(compare, ct)); }
        catch (InvalidOperationException) when (ct.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); throw; }
        ct.ThrowIfCancellationRequested();
    }
}
