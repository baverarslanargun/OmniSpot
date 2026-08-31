using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using OmniSpot.Benchmarking.Profiling;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace OmniSpot.Benchmarking.Measurements;

internal sealed record RealTreeRunSample(
    string Variant,
    int Order,
    double Nanoseconds,
    long AllocatedBytes);

internal sealed record RealTreeVariantResult(
    string Variant,
    double MedianNanoseconds,
    double MedianAllocatedBytes,
    long RetainedBytes);

internal sealed record RealTreeTreeFacts(
    int NodeCount,
    int DistinctItemCount,
    int UniqueTokenCount,
    long TokenToItemLinkCount,
    int SingletonTokenCount,
    double SingletonTokenRatio,
    int MaxDocumentFrequency,
    int ParentLinkedItemCount,
    long EnumerationMilliseconds);

internal sealed record RealTreeComparison(
    int SchemaMajor,
    int SchemaMinor,
    string ContractVersion,
    string ToolVersion,
    string Lane,
    string Note,
    long StartedUnixSeconds,
    long CompletedUnixSeconds,
    int Rounds,
    ProfileEnvironment Environment,
    RealTreeTreeFacts TreeFacts,
    IReadOnlyList<RealTreeRunSample> Samples,
    IReadOnlyList<RealTreeVariantResult> Variants,
    double AllocationChangePercent,
    double TimeChangePercent,
    bool MeetsAllocationBar,
    double AllocationBarPercent,
    IReadOnlyList<string> AcceptanceFailures);

internal static class RealTreeComparisonRunner
{
    private const string LegacyVariant = "legacy";
    private const string BuilderVariant = "builder";

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    internal static RealTreeComparison Run(
        IReadOnlyList<FileSystemNode> nodes,
        int rounds,
        double allocationBarPercent,
        long enumerationMilliseconds,
        TimeSpan hardStop,
        ProfileEnvironmentCapture environmentCapture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(environmentCapture);
        var started = DateTimeOffset.UtcNow;
        var deadline = Stopwatch.StartNew();
        var tokenizer = new BasicTokenizer();

        Warmup(nodes, tokenizer, cancellationToken);
        var (facts, failures) = Verify(nodes, tokenizer, enumerationMilliseconds);
        cancellationToken.ThrowIfCancellationRequested();

        var samples = new List<RealTreeRunSample>();
        var order = 0;
        for (var round = 0; round < rounds; round++)
        {
            foreach (var variant in round % 2 == 0
                ? new[] { LegacyVariant, BuilderVariant, BuilderVariant, LegacyVariant }
                : new[] { BuilderVariant, LegacyVariant, LegacyVariant, BuilderVariant })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.Elapsed > hardStop)
                {
                    failures.Add(
                        "Sert süre sınırı aşıldı, koşum erken kesildi: " +
                        hardStop.TotalSeconds.ToString("N0", CultureInfo.InvariantCulture) + " sn.");
                    round = rounds;
                    break;
                }

                var (elapsed, allocated) = Measure(() => Build(variant, nodes, tokenizer));
                samples.Add(new RealTreeRunSample(variant, order++, elapsed, allocated));
            }
        }

        var variants = new[] { LegacyVariant, BuilderVariant }
            .Select(variant => new RealTreeVariantResult(
                variant,
                Median(samples, variant, sample => sample.Nanoseconds),
                Median(samples, variant, sample => sample.AllocatedBytes),
                MeasureRetained(variant, nodes, tokenizer)))
            .ToArray();

        var legacy = variants.Single(variant => variant.Variant == LegacyVariant);
        var builder = variants.Single(variant => variant.Variant == BuilderVariant);
        var allocationChange = MeasurementStatistics.ChangePercent(
            legacy.MedianAllocatedBytes,
            builder.MedianAllocatedBytes);
        var timeChange = MeasurementStatistics.ChangePercent(
            legacy.MedianNanoseconds,
            builder.MedianNanoseconds);
        var environment = environmentCapture.Complete();
        if (environment.Labels.Contains("frekans-kaymasi", StringComparer.Ordinal))
        {
            failures.Add(
                "Koşum içinde CPU frekansı veya PROCTHROTTLEMAX politikası kaydı " +
                "(`frekans-kaymasi`); sonuç kabul kapısını geçemez.");
        }

        return new RealTreeComparison(
            MeasurementConstants.SchemaMajor,
            MeasurementConstants.SchemaMinor,
            MeasurementConstants.ContractVersion,
            MeasurementConstants.ToolVersion,
            "instrumented_profiler",
            "Gerçek ağaç, bellek içi, eşleştirilmiş ABBA. legacy = değişiklik " +
            "öncesi Create replikası, builder = üretim. Ad/token/path diske yazılmaz.",
            started.ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            rounds,
            environment,
            facts,
            samples,
            variants,
            allocationChange,
            timeChange,
            allocationChange <= -allocationBarPercent && failures.Count == 0,
            allocationBarPercent,
            failures);
    }

    private static void Warmup(
        IReadOnlyList<FileSystemNode> nodes,
        ITokenizer tokenizer,
        CancellationToken cancellationToken)
    {
        var subset = nodes.Take(Math.Min(20_000, nodes.Count)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        GC.KeepAlive(Build(LegacyVariant, subset, tokenizer));
        GC.KeepAlive(Build(BuilderVariant, subset, tokenizer));
    }

    private static object Build(
        string variant,
        IReadOnlyList<FileSystemNode> nodes,
        ITokenizer tokenizer) =>
        variant == LegacyVariant
            ? LegacyCreate(nodes, tokenizer)
            : SearchState.Create(nodes, tokenizer);

    private static (double Nanoseconds, long AllocatedBytes) Measure(Func<object> run)
    {
        Settle();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var startedTicks = Stopwatch.GetTimestamp();
        var result = run();
        var elapsedTicks = Stopwatch.GetTimestamp() - startedTicks;
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(result);
        return (
            elapsedTicks * 1_000_000_000d / Stopwatch.Frequency,
            allocatedAfter - allocatedBefore);
    }

    private static long MeasureRetained(
        string variant,
        IReadOnlyList<FileSystemNode> nodes,
        ITokenizer tokenizer)
    {
        Settle();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var result = Build(variant, nodes, tokenizer);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        var after = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(result);
        return after - before;
    }

    private static void Settle()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    }

    private static double Median(
        IReadOnlyList<RealTreeRunSample> samples,
        string variant,
        Func<RealTreeRunSample, double> selector)
    {
        var values = samples
            .Where(sample => sample.Variant == variant)
            .Select(selector)
            .ToArray();
        return values.Length == 0 ? 0 : MeasurementStatistics.Median(values);
    }

    private static (RealTreeTreeFacts Facts, List<string> Failures) Verify(
        IReadOnlyList<FileSystemNode> nodes,
        ITokenizer tokenizer,
        long enumerationMilliseconds)
    {
        var failures = new List<string>();
        var legacy = LegacyCreate(nodes, tokenizer);
        var builder = SearchState.Create(nodes, tokenizer);
        if (legacy.Items.Count != builder.ItemCount)
        {
            failures.Add(
                "Öğe sayısı farklı: legacy " +
                legacy.Items.Count.ToString(CultureInfo.InvariantCulture) + " != builder " +
                builder.ItemCount.ToString(CultureInfo.InvariantCulture));
        }

        var mismatchCount = 0;
        var singletonCount = 0;
        var maxDocumentFrequency = 0;
        var linkCount = 0L;
        foreach (var (token, paths) in legacy.PathsByToken)
        {
            linkCount += paths.Count;
            if (paths.Count == 1)
            {
                singletonCount++;
            }

            if (paths.Count > maxDocumentFrequency)
            {
                maxDocumentFrequency = paths.Count;
            }

            if (!PostingsEqual(paths, builder.Get(token)))
            {
                mismatchCount++;
            }
        }

        if (mismatchCount > 0)
        {
            failures.Add(
                "Posting kümesi uyuşmayan token: " +
                mismatchCount.ToString(CultureInfo.InvariantCulture));
        }

        var itemPaths = legacy.Items.Keys.ToHashSet(PathComparer);
        var facts = new RealTreeTreeFacts(
            nodes.Count,
            legacy.Items.Count,
            legacy.PathsByToken.Count,
            linkCount,
            singletonCount,
            legacy.PathsByToken.Count == 0
                ? 0
                : (double)singletonCount / legacy.PathsByToken.Count,
            maxDocumentFrequency,
            legacy.Items.Values.Count(item =>
                item.ParentPath != null && itemPaths.Contains(item.ParentPath)),
            enumerationMilliseconds);
        return (facts, failures);
    }

    private static LegacyState LegacyCreate(
        IReadOnlyList<FileSystemNode> nodes,
        ITokenizer tokenizer)
    {
        var sourceItems = nodes.Select(ToItem)
            .GroupBy(item => item.FullPath, PathComparer)
            .Select(group => group.Last())
            .ToArray();
        var items = ImmutableDictionary.CreateBuilder<string, SearchItem>(PathComparer);
        var pathsByToken = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(PathComparer);
        var tokensByPath = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(PathComparer);
        foreach (var item in sourceItems)
        {
            var tokens = tokenizer.Tokenize(item.Name).ToImmutableHashSet(PathComparer);
            items[item.FullPath] = item;
            tokensByPath[item.FullPath] = tokens;
            foreach (var token in tokens)
            {
                pathsByToken.TryGetValue(token, out var paths);
                pathsByToken[token] = (paths ?? ImmutableHashSet.Create<string>(PathComparer))
                    .Add(item.FullPath);
            }
        }

        var childrenByPath = LegacyBuildChildrenByPath(items.Values, items.Keys);
        return new LegacyState(
            items,
            pathsByToken,
            tokensByPath,
            items.ToImmutable(),
            pathsByToken.ToImmutable(),
            tokensByPath.ToImmutable(),
            childrenByPath);
    }

    private static ImmutableDictionary<string, ImmutableHashSet<string>> LegacyBuildChildrenByPath(
        IEnumerable<SearchItem> items,
        IEnumerable<string> itemPaths)
    {
        var paths = itemPaths.ToHashSet(PathComparer);
        var childrenByPath = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(PathComparer);
        foreach (var item in items)
        {
            if (item.ParentPath == null || !paths.Contains(item.ParentPath))
            {
                continue;
            }

            childrenByPath.TryGetValue(item.ParentPath, out var children);
            childrenByPath[item.ParentPath] = (children ?? ImmutableHashSet.Create<string>(PathComparer))
                .Add(item.FullPath);
        }

        return childrenByPath.ToImmutable();
    }

    private static bool PostingsEqual(
        ImmutableHashSet<string> expected,
        IReadOnlyCollection<SearchItem> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        var seen = new HashSet<string>(PathComparer);
        foreach (var item in actual)
        {
            if (!expected.Contains(item.FullPath) || !seen.Add(item.FullPath))
            {
                return false;
            }
        }

        return true;
    }

    private static SearchItem ToItem(FileSystemNode node)
    {
        var metadata = node.Metadata;
        return new SearchItem(
            node.Name,
            node.FullPath,
            node.IsDirectory,
            metadata?.SizeBytes,
            metadata?.CreatedTime,
            metadata?.LastWriteTime,
            metadata?.OpenCount ?? 0,
            node.Parent?.FullPath);
    }

    private sealed record LegacyState(
        ImmutableDictionary<string, SearchItem>.Builder Items,
        ImmutableDictionary<string, ImmutableHashSet<string>>.Builder PathsByToken,
        ImmutableDictionary<string, ImmutableHashSet<string>>.Builder TokensByPath,
        ImmutableDictionary<string, SearchItem> FrozenItems,
        ImmutableDictionary<string, ImmutableHashSet<string>> FrozenPathsByToken,
        ImmutableDictionary<string, ImmutableHashSet<string>> FrozenTokensByPath,
        ImmutableDictionary<string, ImmutableHashSet<string>> ChildrenByPath);
}

internal static class RealTreeLoader
{
    internal static (IReadOnlyList<FileSystemNode> Nodes, long ElapsedMilliseconds) Load(
        IReadOnlyList<ProfileRootRequest> roots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var stopwatch = Stopwatch.StartNew();
        var nodes = new List<FileSystemNode>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadRoot(request.Path, nodes, visited, cancellationToken);
        }

        stopwatch.Stop();
        return (nodes, stopwatch.ElapsedMilliseconds);
    }

    private static void LoadRoot(
        string requestedPath,
        List<FileSystemNode> nodes,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        string rootPath;
        try
        {
            rootPath = Path.GetFullPath(requestedPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (!Directory.Exists(rootPath) || !visited.Add(rootPath))
        {
            return;
        }

        var rootNode = new FileSystemNode(new DirectoryInfo(rootPath).Name, rootPath, isDirectory: true);
        nodes.Add(rootNode);
        var pending = new Queue<FileSystemNode>();
        pending.Enqueue(rootNode);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Dequeue();
            try
            {
                foreach (var entry in EnumerateImmediateChildren(directory.FullPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!visited.Add(entry.FullPath))
                    {
                        continue;
                    }

                    var child = new FileSystemNode(entry.Name, entry.FullPath, entry.IsDirectory);
                    directory.AddChild(child);
                    nodes.Add(child);
                    if (entry.IsDirectory && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Enqueue(child);
                    }
                }
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
            }
        }
    }

    private static FileSystemEnumerable<EntrySnapshot> EnumerateImmediateChildren(string directoryPath)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        return new FileSystemEnumerable<EntrySnapshot>(
            directoryPath,
            static (ref FileSystemEntry entry) => new EntrySnapshot(
                entry.FileName.ToString(),
                entry.ToFullPath(),
                entry.IsDirectory,
                entry.Attributes),
            options);
    }

    private readonly record struct EntrySnapshot(
        string Name,
        string FullPath,
        bool IsDirectory,
        FileAttributes Attributes);
}

internal static class RealTreeSummaryFormatter
{
    internal static string Format(RealTreeComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var builder = new StringBuilder();
        var facts = comparison.TreeFacts;
        builder.AppendLine("Gerçek ağaç — legacy / builder eşleştirilmiş karşılaştırma");
        builder.AppendLine(
            "  ağaç: " + Count(facts.NodeCount) + " düğüm, " +
            Count(facts.DistinctItemCount) + " tekil öğe, tarama " +
            (facts.EnumerationMilliseconds / 1000d).ToString("N1", CultureInfo.InvariantCulture) + " sn");
        builder.AppendLine(
            "  token: " + Count(facts.UniqueTokenCount) + " benzersiz, " +
            Count(facts.TokenToItemLinkCount) + " bağlantı, singleton oranı " +
            facts.SingletonTokenRatio.ToString("P2", CultureInfo.InvariantCulture) +
            ", max df " + Count(facts.MaxDocumentFrequency));
        builder.AppendLine("  parent bağlı öğe: " + Count(facts.ParentLinkedItemCount));
        builder.AppendLine(
            "  koşum: " + comparison.Samples.Count.ToString(CultureInfo.InvariantCulture) +
            " (ABBA eşleştirilmiş, process içi)");
        builder.AppendLine();
        builder.AppendLine("  varyant       alloc MiB      süre ms   kalıcı MiB");
        foreach (var variant in comparison.Variants)
        {
            builder.AppendLine(
                "  " + variant.Variant.PadRight(12) +
                Mib(variant.MedianAllocatedBytes).PadLeft(10) +
                (variant.MedianNanoseconds / 1_000_000d).ToString("N0", CultureInfo.InvariantCulture).PadLeft(13) +
                Mib(variant.RetainedBytes).PadLeft(13));
        }

        builder.AppendLine();
        builder.AppendLine(
            "  allocation değişimi: " +
            comparison.AllocationChangePercent.ToString("N2", CultureInfo.InvariantCulture) + "%" +
            "   (kapı: −" +
            comparison.AllocationBarPercent.ToString("N0", CultureInfo.InvariantCulture) + "%)");
        builder.AppendLine(
            "  süre değişimi:       " +
            comparison.TimeChangePercent.ToString("N2", CultureInfo.InvariantCulture) + "%   (ikincil)");
        builder.AppendLine();
        builder.AppendLine(
            comparison.MeetsAllocationBar
                ? "  SONUÇ: kapı GEÇİLDİ — builder yeterli."
                : "  SONUÇ: kapı GEÇİLMEDİ.");
        if (comparison.AcceptanceFailures.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("  DOĞRULUK KAPISI:");
            foreach (var failure in comparison.AcceptanceFailures)
            {
                builder.AppendLine("    - " + failure);
            }
        }

        return builder.ToString();
    }

    private static string Count(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Mib(double bytes) =>
        (bytes / 1_048_576d).ToString("N1", CultureInfo.InvariantCulture);
}
