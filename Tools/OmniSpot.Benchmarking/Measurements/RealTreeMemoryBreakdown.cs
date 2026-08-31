using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using OmniSpot.Benchmarking.Profiling;
using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace OmniSpot.Benchmarking.Measurements;

internal sealed record MemoryStage(
    string Stage,
    string Scope,
    long? RetainedBytes,
    bool Measurable);

internal sealed record MemoryBreakdown(
    int SchemaMajor,
    int SchemaMinor,
    string ContractVersion,
    string ToolVersion,
    string Note,
    long StartedUnixSeconds,
    long CompletedUnixSeconds,
    ProfileEnvironment Environment,
    int NodeCount,
    int DistinctItemCount,
    int UniqueTokenCount,
    long TokenToItemLinkCount,
    long? FullCreateRetainedBytes,
    long? BreakdownTotalBytes,
    double? CrossCheckDeltaPercent,
    IReadOnlyList<MemoryStage> Stages,
    IReadOnlyList<MemoryStage> IndexStages,
    long? IndexStagesTotalBytes,
    long? SteadyManagedTotalBytes);

internal static class RealTreeMemoryBreakdown
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    internal static MemoryBreakdown Run(
        IReadOnlyList<FileSystemNode> nodes,
        ProfileEnvironmentCapture environmentCapture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(environmentCapture);
        var started = DateTimeOffset.UtcNow;
        var tokenizer = new BasicTokenizer();
        Warmup(nodes, tokenizer);

        var fullCreateRetained = MeasureFullCreate(nodes, tokenizer);
        cancellationToken.ThrowIfCancellationRequested();

        var stages = new List<MemoryStage>();
        var items = MeasureStage(
            stages,
            "search_items",
            "SearchItem nesneleri (ToDistinctItems çıktısı)",
            () => ToDistinctItems(nodes));

        var tokenSets = MeasureStage(
            stages,
            "token_sets",
            "öğe başına ImmutableArray<string> token dizileri",
            () => BuildTokenSets(items, tokenizer));
        cancellationToken.ThrowIfCancellationRequested();

        MeasureStage(
            stages,
            "items_by_path",
            "_itemsByPath sözlük düğümleri (SearchItem'lar hariç)",
            () => BuildItemsByPath(items));

        MeasureStage(
            stages,
            "tokens_by_path",
            "_tokensByPath sözlük düğümleri (token dizileri hariç)",
            () => BuildTokensByPath(items, tokenSets));
        cancellationToken.ThrowIfCancellationRequested();

        var postings = MeasureStage(
            stages,
            "paths_by_token",
            "_pathsByToken: sözlük + posting kümeleri (token string'leri hariç)",
            () => BuildPathsByToken(items, tokenSets));
        cancellationToken.ThrowIfCancellationRequested();

        MeasureStage(
            stages,
            "children_by_path",
            "_childrenByPath: sözlük + çocuk kümeleri",
            () => BuildChildrenByPath(items));

        GC.KeepAlive(postings);
        long? breakdownTotal = stages.All(stage => stage.Measurable)
            ? stages.Sum(stage => stage.RetainedBytes!.Value)
            : null;

        var indexStages = new List<MemoryStage>();
        var clones = MeasureStage(
            indexStages,
            "node_tree",
            "FileSystemNode nesneleri, ad ve tam yol string'leri, çocuk listeleri",
            () => CloneNodeTree(nodes));
        cancellationToken.ThrowIfCancellationRequested();

        MeasureStage(
            indexStages,
            "node_metadata",
            "dosya düğümleri için FileMetadata nesneleri",
            () => AttachMetadata(clones));

        var pathToNode = MeasureStage(
            indexStages,
            "path_to_node",
            "IndexManager._pathToNode sözlüğü (düğümler hariç)",
            () => BuildPathToNode(clones));
        cancellationToken.ThrowIfCancellationRequested();

        MeasureStage(
            indexStages,
            "metadata_map",
            "IndexManager._metadataMap sözlüğü (FileMetadata nesneleri hariç)",
            () => BuildMetadataMap(clones));

        cancellationToken.ThrowIfCancellationRequested();

        GC.KeepAlive(pathToNode);
        long? indexStagesTotal = indexStages.All(stage => stage.Measurable)
            ? indexStages.Sum(stage => stage.RetainedBytes!.Value)
            : null;
        long? steadyManagedTotal =
            fullCreateRetained is > 0 && indexStagesTotal is not null
                ? fullCreateRetained.Value + indexStagesTotal.Value
                : null;

        return new MemoryBreakdown(
            MeasurementConstants.SchemaMajor,
            MeasurementConstants.SchemaMinor,
            MeasurementConstants.ContractVersion,
            MeasurementConstants.ToolVersion,
            "Her bileşen, paylaşılan ön koşullar canlı tutularak marjinal " +
            "maliyetiyle ölçüldü. SearchState aşamalarında ad ve path string'leri " +
            "düğüm listesinden geldiği için hiçbir aşamaya yazılmaz; token " +
            "string'leri token_sets'e yazılır. IndexManager aşamaları ağacı üretim " +
            "tipiyle yeniden kurar ve ad/tam yol string'lerini node_tree'ye yazar; kurulum " +
            "mantığı yeniden uygulandığı için üretim değişirse sessizce ayrışabilir. " +
            "Ölçülemeyen aşamada değer " +
            "null'dır ve toplama katılmaz.",
            started.ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            environmentCapture.Complete(),
            nodes.Count,
            items.Length,
            postings.Count,
            postings.Values.Sum(set => (long)set.Count),
            fullCreateRetained,
            breakdownTotal,
            fullCreateRetained is > 0 && breakdownTotal is not null
                ? (double)(breakdownTotal.Value - fullCreateRetained.Value) / fullCreateRetained.Value * 100d
                : null,
            stages,
            indexStages,
            indexStagesTotal,
            steadyManagedTotal);
    }

    private static void Warmup(IReadOnlyList<FileSystemNode> nodes, ITokenizer tokenizer)
    {
        var subset = nodes.Take(Math.Min(20_000, nodes.Count)).ToArray();
        GC.KeepAlive(SearchState.Create(subset, tokenizer));
    }

    private static long? MeasureFullCreate(
        IReadOnlyList<FileSystemNode> nodes,
        ITokenizer tokenizer)
    {
        Settle();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var state = SearchState.Create(nodes, tokenizer);
        var after = MeasureSettled();
        GC.KeepAlive(state);
        var retained = after - before;
        return retained > 0 ? retained : null;
    }

    private static T MeasureStage<T>(
        List<MemoryStage> stages,
        string stage,
        string scope,
        Func<T> build)
    {
        Settle();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var result = build();
        var after = MeasureSettled();
        var retained = after - before;
        stages.Add(retained > 0
            ? new MemoryStage(stage, scope, retained, Measurable: true)
            : new MemoryStage(stage, scope, RetainedBytes: null, Measurable: false));
        return result;
    }

    private static long MeasureSettled()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static void Settle()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    }

    private static SearchItem[] ToDistinctItems(IReadOnlyList<FileSystemNode> nodes) =>
        nodes.Select(node =>
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
            })
            .GroupBy(item => item.FullPath, PathComparer)
            .Select(group => group.Last())
            .ToArray();

    private static ImmutableArray<string>[] BuildTokenSets(
        SearchItem[] items,
        ITokenizer tokenizer)
    {
        var sets = new ImmutableArray<string>[items.Length];
        var buffer = new List<string>();
        var canonicalTokens = new Dictionary<string, string>(PathComparer);
        for (var index = 0; index < items.Length; index++)
        {
            buffer.Clear();
            foreach (var token in tokenizer.Tokenize(items[index].Name))
            {
                var canonical = token;
                if (canonicalTokens.TryGetValue(token, out var shared))
                {
                    canonical = shared;
                }
                else
                {
                    canonicalTokens[token] = token;
                }

                var seen = false;
                for (var existing = 0; existing < buffer.Count; existing++)
                {
                    if (PathComparer.Equals(buffer[existing], canonical))
                    {
                        seen = true;
                        break;
                    }
                }

                if (!seen)
                {
                    buffer.Add(canonical);
                }
            }

            sets[index] = [.. buffer];
        }

        return sets;
    }

    private static ImmutableDictionary<string, SearchItem> BuildItemsByPath(SearchItem[] items)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, SearchItem>(PathComparer);
        foreach (var item in items)
        {
            builder[item.FullPath] = item;
        }

        return builder.ToImmutable();
    }

    private static ImmutableDictionary<string, ImmutableArray<string>> BuildTokensByPath(
        SearchItem[] items,
        ImmutableArray<string>[] tokenSets)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(PathComparer);
        for (var index = 0; index < items.Length; index++)
        {
            builder[items[index].FullPath] = tokenSets[index];
        }

        return builder.ToImmutable();
    }

    private static ImmutableDictionary<string, ImmutableHashSet<string>> BuildPathsByToken(
        SearchItem[] items,
        ImmutableArray<string>[] tokenSets)
    {
        var pathBuildersByToken = new Dictionary<string, ImmutableHashSet<string>.Builder>(PathComparer);
        for (var index = 0; index < items.Length; index++)
        {
            foreach (var token in tokenSets[index])
            {
                if (!pathBuildersByToken.TryGetValue(token, out var paths))
                {
                    paths = ImmutableHashSet.CreateBuilder<string>(PathComparer);
                    pathBuildersByToken[token] = paths;
                }

                paths.Add(items[index].FullPath);
            }
        }

        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(PathComparer);
        foreach (var (token, paths) in pathBuildersByToken)
        {
            builder[token] = paths.ToImmutable();
        }

        return builder.ToImmutable();
    }

    private static ImmutableDictionary<string, ImmutableHashSet<string>> BuildChildrenByPath(
        SearchItem[] items)
    {
        var paths = items.Select(item => item.FullPath).ToHashSet(PathComparer);
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(PathComparer);
        foreach (var item in items)
        {
            if (item.ParentPath == null || !paths.Contains(item.ParentPath))
            {
                continue;
            }

            builder.TryGetValue(item.ParentPath, out var children);
            builder[item.ParentPath] = (children ?? ImmutableHashSet.Create<string>(PathComparer))
                .Add(item.FullPath);
        }

        return builder.ToImmutable();
    }

    private static FileSystemNode[] CloneNodeTree(IReadOnlyList<FileSystemNode> nodes)
    {
        var clones = new FileSystemNode[nodes.Count];
        var byPath = new Dictionary<string, FileSystemNode>(nodes.Count, PathComparer);
        for (var index = 0; index < nodes.Count; index++)
        {
            var source = nodes[index];
            var clone = new FileSystemNode(
                new string(source.Name.AsSpan()),
                new string(source.FullPath.AsSpan()),
                source.IsDirectory);
            clones[index] = clone;
            byPath[clone.FullPath] = clone;
        }

        for (var index = 0; index < nodes.Count; index++)
        {
            var parentPath = nodes[index].Parent?.FullPath;
            if (parentPath != null && byPath.TryGetValue(parentPath, out var parent))
            {
                parent.AddChild(clones[index]);
            }
        }

        return clones;
    }

    private static int AttachMetadata(FileSystemNode[] clones)
    {
        var count = 0;
        foreach (var clone in clones)
        {
            if (clone.IsDirectory)
            {
                continue;
            }

            clone.Metadata = new FileMetadata
            {
                SizeBytes = 0L,
                CreatedTime = DateTime.UnixEpoch,
                LastWriteTime = DateTime.UnixEpoch,
                OpenCount = 0
            };
            count++;
        }

        return count;
    }

    private static Dictionary<string, FileSystemNode> BuildPathToNode(FileSystemNode[] clones)
    {
        var pathToNode = new Dictionary<string, FileSystemNode>(PathComparer);
        foreach (var clone in clones)
        {
            pathToNode[clone.FullPath] = clone;
        }

        return pathToNode;
    }

    private static Dictionary<string, FileMetadata> BuildMetadataMap(FileSystemNode[] clones)
    {
        var metadataMap = new Dictionary<string, FileMetadata>(PathComparer);
        foreach (var clone in clones)
        {
            if (clone.Metadata is { } metadata)
            {
                metadataMap[clone.FullPath] = metadata;
            }
        }

        return metadataMap;
    }
}

internal static class MemoryBreakdownFormatter
{
    internal static string Format(MemoryBreakdown breakdown)
    {
        ArgumentNullException.ThrowIfNull(breakdown);
        var builder = new StringBuilder();
        builder.AppendLine("SearchState canlı bellek dökümü");
        builder.AppendLine(
            "  ağaç: " + Count(breakdown.DistinctItemCount) + " öğe, " +
            Count(breakdown.UniqueTokenCount) + " token, " +
            Count(breakdown.TokenToItemLinkCount) + " bağlantı");
        builder.AppendLine();
        builder.AppendLine("  bileşen                MiB     pay%   kapsam");
        foreach (var stage in breakdown.Stages)
        {
            if (!stage.Measurable)
            {
                builder.AppendLine(
                    "  " + stage.Stage.PadRight(20) +
                    "ölçülemedi".PadLeft(8) + "        —   " + stage.Scope +
                    "  [GC gürültüsü tahsisi aştı]");
                continue;
            }

            var share = breakdown.BreakdownTotalBytes is > 0
                ? (double)stage.RetainedBytes!.Value / breakdown.BreakdownTotalBytes.Value * 100d
                : (double?)null;
            builder.AppendLine(
                "  " + stage.Stage.PadRight(20) +
                Mib(stage.RetainedBytes!.Value).PadLeft(8) +
                (share is null
                    ? "—".PadLeft(8)
                    : share.Value.ToString("N1", CultureInfo.InvariantCulture).PadLeft(8) + "%") +
                "   " + stage.Scope);
        }

        if (breakdown.Stages.Any(stage => !stage.Measurable))
        {
            builder.AppendLine();
            builder.AppendLine(
                "  UYARI: en az bir aşama ölçülemedi; döküm eksiktir ve toplam " +
                "payları olduğundan büyük gösterir. Daha büyük bir ağaçta tekrarlayın.");
        }

        builder.AppendLine();
        builder.AppendLine(
            "  döküm toplamı:        " + Optional(breakdown.BreakdownTotalBytes).PadLeft(8) + " MiB");
        builder.AppendLine(
            "  tam SearchState.Create:" + Optional(breakdown.FullCreateRetainedBytes).PadLeft(8) + " MiB");
        builder.AppendLine(
            "  çapraz denetim sapması: " +
            (breakdown.CrossCheckDeltaPercent is null
                ? "hesaplanamadı (ölçülemeyen aşama var)"
                : breakdown.CrossCheckDeltaPercent.Value.ToString("N2", CultureInfo.InvariantCulture) + "%" +
                    (Math.Abs(breakdown.CrossCheckDeltaPercent.Value) <= 10
                        ? "  (kabul edilebilir)"
                        : "  (YÜKSEK — döküm güvenilmez)")));

        if (breakdown.IndexStages.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("IndexManager bellekte tuttukları");
            builder.AppendLine("  bileşen                MiB   B/düğüm   kapsam");
            foreach (var stage in breakdown.IndexStages)
            {
                if (!stage.Measurable)
                {
                    builder.AppendLine(
                        "  " + stage.Stage.PadRight(20) +
                        "ölçülemedi".PadLeft(8) + "         —   " + stage.Scope);
                    continue;
                }

                var perNode = breakdown.NodeCount > 0
                    ? (double)stage.RetainedBytes!.Value / breakdown.NodeCount
                    : (double?)null;
                builder.AppendLine(
                    "  " + stage.Stage.PadRight(20) +
                    Mib(stage.RetainedBytes!.Value).PadLeft(8) +
                    (perNode is null
                        ? "—".PadLeft(10)
                        : perNode.Value.ToString("N0", CultureInfo.InvariantCulture).PadLeft(10)) +
                    "   " + stage.Scope);
            }

            builder.AppendLine();
            builder.AppendLine(
                "  indeks aşamaları toplamı:" + Optional(breakdown.IndexStagesTotalBytes).PadLeft(8) + " MiB");
            builder.AppendLine(
                "  kararlı yönetilen toplam:" + Optional(breakdown.SteadyManagedTotalBytes).PadLeft(8) +
                " MiB  (tam SearchState.Create + IndexManager aşamaları)");
            if (breakdown.SteadyManagedTotalBytes is > 0 && breakdown.NodeCount > 0)
            {
                builder.AppendLine(
                    "  kararlı toplam / düğüm:  " +
                    ((double)breakdown.SteadyManagedTotalBytes.Value / breakdown.NodeCount)
                        .ToString("N0", CultureInfo.InvariantCulture).PadLeft(8) + " B");
            }
        }

        return builder.ToString();
    }

    private static string Count(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Optional(long? bytes) =>
        bytes is null ? "ölçülemedi" : Mib(bytes.Value);

    private static string Mib(double bytes) =>
        (bytes / 1_048_576d).ToString("N1", CultureInfo.InvariantCulture);
}
