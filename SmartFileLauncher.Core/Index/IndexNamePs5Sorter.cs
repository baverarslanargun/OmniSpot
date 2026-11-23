using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.Search.Paged;

namespace SmartFileLauncher.Core.Index;

internal static class IndexNamePs5Sorter
{
    private const int KeyBytes = 7;
    private const int ByteBits = 9;
    private const int LeafCount = 64;
    private const int BucketCount = 2 * LeafCount - 1;
    private const int Oversampling = 4;
    private const int MinimumRange = 4096;
    private const int MinimumStripe = 32768;
    private const int MaxDistributions = 48;

    private readonly record struct SortRange(int Start, int Length, int Depth, bool InScratch, int Distributions, bool Terminal)
    {
        internal bool CanDistribute => Length >= MinimumRange && !Terminal && Distributions < MaxDistributions;
    }

    internal static int Workers(IndexBuildOptions? options) => options is { AdaptiveSort: false }
        ? Math.Clamp(options.SortWorkers, 1, 16)
        : Math.Min(8, Environment.ProcessorCount);

    internal static void Sort(int[] ids, IndexNames names, int workers, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ids.Length < MinimumRange)
        {
            IndexSorter.RangeSort(ids, 0, ids.Length, names.Compare, ct);
            return;
        }

        var scratch = new int[ids.Length];
        var buckets = new byte[ids.Length];
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct };
        var root = new SortRange(0, ids.Length, 0, false, 0, false);
        if (workers == 1)
        {
            Finish(root);
            return;
        }

        var pending = new Stack<SortRange>();
        var independent = new List<SortRange>();
        var parallelThreshold = Math.Max(2 * MinimumStripe, ids.Length / workers);
        pending.Push(root);
        while (pending.TryPop(out var range))
        {
            ct.ThrowIfCancellationRequested();
            if (!range.CanDistribute || range.Length < parallelThreshold)
            {
                independent.Add(range);
                continue;
            }

            var stripes = Math.Min(workers, range.Length / MinimumStripe);
            foreach (var child in Distribute(ids, scratch, buckets, names, range, stripes, parallel))
                pending.Push(child);
        }
        Parallel.ForEach(independent, parallel, Finish);

        void Finish(SortRange first)
        {
            var ranges = new Stack<SortRange>();
            ranges.Push(first);
            while (ranges.TryPop(out var range))
            {
                ct.ThrowIfCancellationRequested();
                if (range.CanDistribute)
                {
                    foreach (var child in Distribute(ids, scratch, buckets, names, range, 1, parallel))
                        ranges.Push(child);
                    continue;
                }

                if (range.InScratch)
                    Array.Copy(scratch, range.Start, ids, range.Start, range.Length);
                if (range.Length > 1)
                    IndexSorter.RangeSort(ids, range.Start, range.Length, names.Compare, ct);
            }
        }
    }

    private static List<SortRange> Distribute(int[] ids, int[] scratch, byte[] buckets, IndexNames names,
        SortRange range, int stripes, ParallelOptions parallel)
    {
        var source = range.InScratch ? scratch : ids;
        var target = range.InScratch ? ids : scratch;
        var samples = new ulong[LeafCount * Oversampling];
        for (var i = 0; i < samples.Length; i++)
        {
            var at = range.Start + (int)((long)i * range.Length / samples.Length);
            samples[i] = ReadKey(names.Scanned(source[at]), range.Depth);
        }
        Array.Sort(samples);

        var splitters = new ulong[LeafCount - 1];
        for (var i = 0; i < splitters.Length; i++)
            splitters[i] = samples[(i + 1) * Oversampling];
        var tree = new ulong[LeafCount];
        FillTree(splitters, tree, 1);

        var cursors = new int[stripes * BucketCount];
        if (stripes == 1)
            Count(0);
        else
            Parallel.For(0, stripes, parallel, Count);

        var children = new List<SortRange>(BucketCount);
        var position = range.Start;
        for (var bucket = 0; bucket < BucketCount; bucket++)
        {
            var start = position;
            for (var stripe = 0; stripe < stripes; stripe++)
            {
                var cursor = stripe * BucketCount + bucket;
                var count = cursors[cursor];
                cursors[cursor] = position;
                position += count;
            }
            if (position == start) continue;

            var equal = (bucket & 1) != 0;
            var terminal = equal && (splitters[bucket / 2] & ((1UL << ByteBits) - 1)) == 0;
            var depth = equal ? range.Depth + KeyBytes : range.Depth;
            children.Add(new SortRange(start, position - start, depth, !range.InScratch, range.Distributions + 1, terminal));
        }

        if (stripes == 1)
            Scatter(0);
        else
            Parallel.For(0, stripes, parallel, Scatter);
        return children;

        int Boundary(int stripe) => range.Start + (int)((long)stripe * range.Length / stripes);

        void Count(int stripe)
        {
            var end = Boundary(stripe + 1);
            var offset = stripe * BucketCount;
            for (var i = Boundary(stripe); i < end; i++)
            {
                if ((i & 16383) == 0) parallel.CancellationToken.ThrowIfCancellationRequested();
                var key = ReadKey(names.Scanned(source[i]), range.Depth);
                var node = 1;
                while (node < LeafCount)
                    node = 2 * node + (key > tree[node] ? 1 : 0);
                var splitter = node - LeafCount;
                var bucket = 2 * splitter;
                if (splitter < splitters.Length && key == splitters[splitter]) bucket++;
                buckets[i] = (byte)bucket;
                cursors[offset + bucket]++;
            }
        }

        void Scatter(int stripe)
        {
            var end = Boundary(stripe + 1);
            var offset = stripe * BucketCount;
            for (var i = Boundary(stripe); i < end; i++)
            {
                if ((i & 16383) == 0) parallel.CancellationToken.ThrowIfCancellationRequested();
                target[cursors[offset + buckets[i]]++] = source[i];
            }
        }
    }

    private static void FillTree(ReadOnlySpan<ulong> splitters, ulong[] tree, int node)
    {
        if (splitters.IsEmpty) return;
        var middle = splitters.Length / 2;
        tree[node] = splitters[middle];
        FillTree(splitters[..middle], tree, 2 * node);
        FillTree(splitters[(middle + 1)..], tree, 2 * node + 1);
    }

    private static ulong ReadKey(ReadOnlySpan<byte> bytes, int depth)
    {
        if (depth >= bytes.Length) return 0;
        var chunk = bytes.Slice(depth, Math.Min(KeyBytes, bytes.Length - depth));
        var key = 0UL;
        foreach (var value in chunk)
            key = (key << ByteBits) | ((ulong)IndexNames.Upper(value) + 1);
        return key << ((KeyBytes - chunk.Length) * ByteBits);
    }
}
