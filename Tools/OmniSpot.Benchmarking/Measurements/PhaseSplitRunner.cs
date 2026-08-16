using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using OmniSpot.Benchmarking.Profiling;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace OmniSpot.Benchmarking.Measurements;

internal sealed record PhaseSplitRequest(
    int ItemCount,
    int Seed,
    int RepeatCount,
    int WarmupItemCount);

internal sealed record PhaseStageSample(
    string Stage,
    double MedianNanoseconds,
    double MedianAllocatedBytes,
    IReadOnlyList<double> Nanoseconds,
    IReadOnlyList<double> AllocatedBytes);

internal sealed record PhaseShare(
    string Phase,
    string Boundary,
    bool CoveredByR5,
    double Nanoseconds,
    double AllocatedBytes,
    double NanosecondSharePercent,
    double AllocationSharePercent,
    bool NegativeDelta);

internal sealed record PhaseFixtureFacts(
    int DistinctItemCount,
    int UniqueTokenCount,
    long TokenToItemLinkCount,
    int ParentLinkedItemCount);

internal sealed record PhaseInstrumentation(
    double StopwatchTimestampPairNanoseconds,
    double AllocationCounterPairNanoseconds);

internal sealed record PhaseSplitConfiguration(
    int ItemCount,
    int Seed,
    int RepeatCount,
    int WarmupItemCount,
    bool ForcedGcBetweenStages,
    string PercentileMethod,
    string OutlierMode);

internal sealed record PhaseSplitDocument(
    int SchemaMajor,
    int SchemaMinor,
    string ContractVersion,
    string ToolVersion,
    string Metric,
    string Lane,
    string MetricNote,
    long StartedUnixSeconds,
    long CompletedUnixSeconds,
    SearchFixtureManifest Fixture,
    ProfileEnvironment Environment,
    PhaseSplitConfiguration Configuration,
    PhaseInstrumentation Instrumentation,
    PhaseFixtureFacts FixtureFacts,
    IReadOnlyList<PhaseStageSample> Stages,
    IReadOnlyList<PhaseShare> Phases,
    IReadOnlyList<string> AcceptanceFailures);

/// <summary>
/// `SearchState.Create` fazlarının payını ölçer. Core'a ölçüm noktası koymak
/// `B-5`'e bağlı olduğundan fazlar in-situ span olarak değil, aynı fixture
/// üzerinde kümülatif replika koşumlarının farkı olarak elde edilir. Replika
/// `SearchState.Create` gövdesini birebir yansıtmalıdır; sapma
/// `production - postings` farkına yazılır ve kabul kapısı bunu yakalar.
/// </summary>
internal static class PhaseSplitRunner
{
    private const string ProductionStage = "production";
    private const string PostingsStage = "postings";

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private static readonly (string Stage, Func<IReadOnlyList<FileSystemNode>, ITokenizer, object> Run)[] StageDefinitions =
    [
        ("enumerate", static (nodes, _) => RunEnumerate(nodes)),
        ("distinct", static (nodes, _) => RunDistinct(nodes)),
        ("tokens", static (nodes, tokenizer) => RunTokens(nodes, tokenizer)),
        ("token_sets", static (nodes, tokenizer) => RunTokenSets(nodes, tokenizer)),
        (PostingsStage, static (nodes, tokenizer) => RunPostings(nodes, tokenizer)),
        (ProductionStage, static (nodes, tokenizer) => SearchState.Create(nodes, tokenizer))
    ];

    private static readonly (string Phase, string? From, string To, bool CoveredByR5)[] PhaseDefinitions =
    [
        ("enumerate", null, "enumerate", false),
        ("distinct", "enumerate", "distinct", false),
        ("tokenize", "distinct", "tokens", false),
        ("token_sets", "tokens", "token_sets", true),
        ("postings", "token_sets", PostingsStage, true),
        ("children_publish", PostingsStage, ProductionStage, true)
    ];

    internal static PhaseSplitDocument Run(
        PhaseSplitRequest request,
        ProfileEnvironmentCapture environmentCapture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environmentCapture);
        if (request.ItemCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (request.RepeatCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        var started = DateTimeOffset.UtcNow;
        var instrument = InstrumentationProbe.Measure();
        var tokenizer = new BasicTokenizer();
        var fixture = SyntheticSearchFixtureGenerator.Create(request.ItemCount, request.Seed);
        cancellationToken.ThrowIfCancellationRequested();

        RunWarmup(request, tokenizer, cancellationToken);
        var (facts, failures) = Verify(fixture.Nodes, tokenizer);
        cancellationToken.ThrowIfCancellationRequested();

        var nanoseconds = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var allocatedBytes = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var (stage, _) in StageDefinitions)
        {
            nanoseconds[stage] = [];
            allocatedBytes[stage] = [];
        }

        for (var repeat = 0; repeat < request.RepeatCount; repeat++)
        {
            foreach (var (stage, run) in StageDefinitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (elapsed, allocated) = MeasureStage(() => run(fixture.Nodes, tokenizer));
                nanoseconds[stage].Add(elapsed);
                allocatedBytes[stage].Add(allocated);
            }
        }

        var stages = StageDefinitions
            .Select(definition => new PhaseStageSample(
                definition.Stage,
                MeasurementStatistics.Median(nanoseconds[definition.Stage]),
                MeasurementStatistics.Median(allocatedBytes[definition.Stage]),
                nanoseconds[definition.Stage],
                allocatedBytes[definition.Stage]))
            .ToArray();
        var phases = BuildPhases(stages);
        var environment = environmentCapture.Complete();
        var completed = DateTimeOffset.UtcNow;
        return new PhaseSplitDocument(
            MeasurementConstants.SchemaMajor,
            MeasurementConstants.SchemaMinor,
            MeasurementConstants.ContractVersion,
            MeasurementConstants.ToolVersion,
            MeasurementConstants.MetricName,
            "instrumented_profiler",
            "Fazlar in-situ alt-span değildir; aynı fixture üzerinde kümülatif " +
            "replika koşumlarının farkıdır. In-situ ayrıştırma B-5'e bağlıdır.",
            started.ToUnixTimeSeconds(),
            completed.ToUnixTimeSeconds(),
            fixture.Manifest,
            environment,
            new PhaseSplitConfiguration(
                request.ItemCount,
                request.Seed,
                request.RepeatCount,
                request.WarmupItemCount,
                ForcedGcBetweenStages: true,
                MeasurementConstants.PercentileMethod,
                MeasurementConstants.OutlierMode),
            new PhaseInstrumentation(
                instrument.TimestampPairNanoseconds,
                instrument.AllocationPairNanoseconds),
            facts,
            stages,
            phases,
            failures);
    }

    internal static IReadOnlyList<PhaseShare> BuildPhases(IReadOnlyList<PhaseStageSample> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        var byStage = stages.ToDictionary(stage => stage.Stage, StringComparer.Ordinal);
        var totalNanoseconds = byStage[ProductionStage].MedianNanoseconds;
        var totalAllocatedBytes = byStage[ProductionStage].MedianAllocatedBytes;
        return PhaseDefinitions
            .Select(definition =>
            {
                var to = byStage[definition.To];
                var from = definition.From is null ? null : byStage[definition.From];
                var deltaNanoseconds = to.MedianNanoseconds - (from?.MedianNanoseconds ?? 0);
                var deltaAllocatedBytes = to.MedianAllocatedBytes - (from?.MedianAllocatedBytes ?? 0);
                return new PhaseShare(
                    definition.Phase,
                    definition.From is null ? definition.To : definition.From + "→" + definition.To,
                    definition.CoveredByR5,
                    deltaNanoseconds,
                    deltaAllocatedBytes,
                    totalNanoseconds > 0 ? deltaNanoseconds / totalNanoseconds * 100d : 0,
                    totalAllocatedBytes > 0 ? deltaAllocatedBytes / totalAllocatedBytes * 100d : 0,
                    deltaNanoseconds < 0 || deltaAllocatedBytes < 0);
            })
            .ToArray();
    }

    private static void RunWarmup(
        PhaseSplitRequest request,
        ITokenizer tokenizer,
        CancellationToken cancellationToken)
    {
        if (request.WarmupItemCount < 1)
        {
            return;
        }

        var warmupFixture = SyntheticSearchFixtureGenerator.Create(
            Math.Min(request.WarmupItemCount, request.ItemCount),
            request.Seed);
        foreach (var (_, run) in StageDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GC.KeepAlive(run(warmupFixture.Nodes, tokenizer));
        }
    }

    private static (double Nanoseconds, double AllocatedBytes) MeasureStage(Func<object> run)
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

    private static void Settle()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    }

    internal static (PhaseFixtureFacts Facts, IReadOnlyList<string> Failures) Verify(
        IReadOnlyList<FileSystemNode> nodes,
        ITokenizer tokenizer)
    {
        var replica = RunPostings(nodes, tokenizer);
        var state = SearchState.Create(nodes, tokenizer);
        var failures = new List<string>();
        if (replica.Items.Count != state.ItemCount)
        {
            failures.Add(
                "Replika öğe sayısı üretimden farklı: " +
                replica.Items.Count.ToString(CultureInfo.InvariantCulture) + " != " +
                state.ItemCount.ToString(CultureInfo.InvariantCulture));
        }

        var mismatchCount = 0;
        foreach (var (token, paths) in replica.PathsByToken)
        {
            if (!PostingsEqual(paths, state.Get(token)))
            {
                mismatchCount++;
            }
        }

        if (mismatchCount > 0)
        {
            failures.Add(
                "Replika ile üretimin posting kümesi uyuşmayan token sayısı: " +
                mismatchCount.ToString(CultureInfo.InvariantCulture));
        }

        var itemPaths = replica.Items.Keys.ToHashSet(PathComparer);
        var parentLinkedItemCount = replica.Items.Values
            .Count(item => item.ParentPath != null && itemPaths.Contains(item.ParentPath));
        var facts = new PhaseFixtureFacts(
            replica.Items.Count,
            replica.PathsByToken.Count,
            replica.PathsByToken.Values.Sum(set => (long)set.Count),
            parentLinkedItemCount);
        return (facts, failures);
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

    private static long RunEnumerate(IReadOnlyList<FileSystemNode> nodes)
    {
        var total = 0L;
        foreach (var node in nodes)
        {
            total += node.Name.Length;
        }

        return total;
    }

    private static SearchItem[] RunDistinct(IReadOnlyList<FileSystemNode> nodes) =>
        nodes.Select(ToItem)
            .GroupBy(item => item.FullPath, PathComparer)
            .Select(group => group.Last())
            .ToArray();

    private static long RunTokens(IReadOnlyList<FileSystemNode> nodes, ITokenizer tokenizer)
    {
        var items = RunDistinct(nodes);
        var total = 0L;
        foreach (var item in items)
        {
            foreach (var token in tokenizer.Tokenize(item.Name))
            {
                total += token.Length;
            }
        }

        return total;
    }

    /// <summary>
    /// Üretimin `SearchState.Tokenize` şeklini yansıtır: dizi,
    /// `OrdinalIgnoreCase` benzersiz, tokenizer sırası korunur.
    /// </summary>
    private static ImmutableArray<string> TokenizeReplica(
        string name,
        ITokenizer tokenizer,
        List<string> buffer)
    {
        buffer.Clear();
        foreach (var token in tokenizer.Tokenize(name))
        {
            var seen = false;
            for (var existing = 0; existing < buffer.Count; existing++)
            {
                if (PathComparer.Equals(buffer[existing], token))
                {
                    seen = true;
                    break;
                }
            }

            if (!seen)
            {
                buffer.Add(token);
            }
        }

        return [.. buffer];
    }

    private static ImmutableArray<string>[] RunTokenSets(
        IReadOnlyList<FileSystemNode> nodes,
        ITokenizer tokenizer)
    {
        var items = RunDistinct(nodes);
        var sets = new ImmutableArray<string>[items.Length];
        var buffer = new List<string>();
        for (var index = 0; index < items.Length; index++)
        {
            sets[index] = TokenizeReplica(items[index].Name, tokenizer, buffer);
        }

        return sets;
    }

    private static PostingsReplica RunPostings(
        IReadOnlyList<FileSystemNode> nodes,
        ITokenizer tokenizer)
    {
        var sourceItems = RunDistinct(nodes);
        var items = ImmutableDictionary.CreateBuilder<string, SearchItem>(PathComparer);
        var pathBuildersByToken = new Dictionary<string, ImmutableHashSet<string>.Builder>(PathComparer);
        var tokensByPath = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(PathComparer);
        var buffer = new List<string>();
        foreach (var item in sourceItems)
        {
            var tokens = TokenizeReplica(item.Name, tokenizer, buffer);
            items[item.FullPath] = item;
            tokensByPath[item.FullPath] = tokens;
            foreach (var token in tokens)
            {
                if (!pathBuildersByToken.TryGetValue(token, out var paths))
                {
                    paths = ImmutableHashSet.CreateBuilder<string>(PathComparer);
                    pathBuildersByToken[token] = paths;
                }

                paths.Add(item.FullPath);
            }
        }

        var pathsByToken = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(PathComparer);
        foreach (var (token, paths) in pathBuildersByToken)
        {
            pathsByToken[token] = paths.ToImmutable();
        }

        return new PostingsReplica(items, pathsByToken, tokensByPath);
    }

    private sealed record PostingsReplica(
        ImmutableDictionary<string, SearchItem>.Builder Items,
        ImmutableDictionary<string, ImmutableHashSet<string>>.Builder PathsByToken,
        ImmutableDictionary<string, ImmutableArray<string>>.Builder TokensByPath);
}

internal static class PhaseSplitSummaryFormatter
{
    internal static string Format(PhaseSplitDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder();
        builder.AppendLine("SearchState.Create faz dağılımı");
        builder.AppendLine(
            "  fixture: " +
            document.Fixture.ItemCount.ToString("N0", CultureInfo.InvariantCulture) +
            " öğe, seed " +
            document.Fixture.Seed.ToString(CultureInfo.InvariantCulture) +
            ", parmak izi " + document.Fixture.Fingerprint[..12]);
        builder.AppendLine(
            "  tekrar: " + document.Configuration.RepeatCount.ToString(CultureInfo.InvariantCulture) +
            " (process içi; bağımsız process örneği değildir)");
        builder.AppendLine(
            "  benzersiz token: " +
            document.FixtureFacts.UniqueTokenCount.ToString("N0", CultureInfo.InvariantCulture) +
            ", token→öğe bağlantısı: " +
            document.FixtureFacts.TokenToItemLinkCount.ToString("N0", CultureInfo.InvariantCulture) +
            ", parent bağlı öğe: " +
            document.FixtureFacts.ParentLinkedItemCount.ToString("N0", CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.AppendLine("  faz                  süre pay%   alloc pay%        süre ms       alloc MiB  R5");
        foreach (var phase in document.Phases)
        {
            builder.AppendLine(
                "  " + phase.Phase.PadRight(18) +
                Format(phase.NanosecondSharePercent).PadLeft(9) +
                Format(phase.AllocationSharePercent).PadLeft(12) +
                (phase.Nanoseconds / 1_000_000d).ToString("N1", CultureInfo.InvariantCulture).PadLeft(15) +
                (phase.AllocatedBytes / 1_048_576d).ToString("N1", CultureInfo.InvariantCulture).PadLeft(16) +
                (phase.CoveredByR5 ? "  evet" : "  hayır") +
                (phase.NegativeDelta ? "  [negatif fark]" : string.Empty));
        }

        var production = document.Stages.Single(stage => stage.Stage == "production");
        builder.AppendLine();
        builder.AppendLine(
            "  toplam (production): " +
            (production.MedianNanoseconds / 1_000_000d).ToString("N1", CultureInfo.InvariantCulture) +
            " ms, " +
            (production.MedianAllocatedBytes / 1_048_576d).ToString("N1", CultureInfo.InvariantCulture) +
            " MiB");
        builder.AppendLine("  " + document.MetricNote);
        if (document.AcceptanceFailures.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("  DOĞRULUK KAPISI GEÇİLMEDİ:");
            foreach (var failure in document.AcceptanceFailures)
            {
                builder.AppendLine("    - " + failure);
            }
        }

        return builder.ToString();
    }

    private static string Format(double value) =>
        value.ToString("N2", CultureInfo.InvariantCulture) + "%";
}
