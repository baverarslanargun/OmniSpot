using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using OmniSpot.Benchmarking.Profiling;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace OmniSpot.Benchmarking.Measurements;

internal sealed record TokenRepresentationSample(
    string Variant,
    int Order,
    long? RetainedBytes);

internal sealed record TokenRepresentationVariant(
    string Variant,
    string Scope,
    long? MedianRetainedBytes,
    int MeasuredSampleCount,
    int SampleCount);

internal sealed record TokenRepresentationFacts(
    int NodeCount,
    int DistinctItemCount,
    int UniqueTokenCount,
    long TokenOccurrenceCount,
    long DistinctTokenLinkCount,
    int MaxTokensPerItem,
    long EnumerationMilliseconds);

internal sealed record TokenRepresentationComparison(
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
    TokenRepresentationFacts Facts,
    IReadOnlyList<TokenRepresentationSample> Samples,
    IReadOnlyList<TokenRepresentationVariant> Variants,
    double? ArrayChangePercent,
    double? PooledArrayChangePercent,
    IReadOnlyList<string> AcceptanceFailures);

/// <summary>
/// `_tokensByPath` değerlerinin temsilini eşleştirilmiş (ABBA) ölçer. Üç aday
/// aynı `SearchItem` dizisi canlı tutularak, her biri kendi token string'lerini
/// üreterek kurulur; ölçülen şey her temsilin **marjinal** kalıcı maliyetidir ve
/// `realtree --breakdown` içindeki `token_sets` aşamasıyla aynı muhasebeyi
/// kullanır.
///
/// Ölçümden önce doğruluk kapısı çalışır: aday temsiller, üretimin
/// `OrdinalIgnoreCase` benzersiz küme semantiğini öğe öğe korumak zorundadır.
/// Kapı düşerse sayılar üretilir ama kabul edilmez.
/// </summary>
internal static class TokenRepresentationRunner
{
    private const string HashSetVariant = "hashset";
    private const string ArrayVariant = "array";
    private const string PooledArrayVariant = "pooled_array";

    private static readonly StringComparer TokenComparer = StringComparer.OrdinalIgnoreCase;

    private static readonly string[][] RoundOrders =
    [
        [HashSetVariant, ArrayVariant, PooledArrayVariant, PooledArrayVariant, ArrayVariant, HashSetVariant],
        [PooledArrayVariant, ArrayVariant, HashSetVariant, HashSetVariant, ArrayVariant, PooledArrayVariant]
    ];

    internal static TokenRepresentationComparison Run(
        IReadOnlyList<FileSystemNode> nodes,
        int rounds,
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

        var items = ToDistinctItems(nodes);
        Warmup(items, tokenizer);
        cancellationToken.ThrowIfCancellationRequested();

        var (facts, failures) = Verify(items, tokenizer, nodes.Count, enumerationMilliseconds);
        cancellationToken.ThrowIfCancellationRequested();

        var samples = new List<TokenRepresentationSample>();
        var order = 0;
        for (var round = 0; round < rounds; round++)
        {
            foreach (var variant in RoundOrders[round % RoundOrders.Length])
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

                samples.Add(new TokenRepresentationSample(
                    variant,
                    order++,
                    MeasureRetained(() => Build(variant, items, tokenizer))));
            }
        }

        var variants = new[]
        {
            Summarize(HashSetVariant, "ImmutableHashSet<string> (üretim)", samples),
            Summarize(ArrayVariant, "string[] (OrdinalIgnoreCase benzersiz)", samples),
            Summarize(
                PooledArrayVariant,
                "string[] + paylaşılan token havuzu (bilgi amaçlı)",
                samples)
        };

        var baseline = variants.Single(variant => variant.Variant == HashSetVariant);
        var environment = environmentCapture.Complete();
        if (environment.Labels.Contains("frekans-kaymasi", StringComparer.Ordinal))
        {
            failures.Add(
                "Koşum içinde CPU frekansı veya PROCTHROTTLEMAX politikası kaydı " +
                "(`frekans-kaymasi`); sonuç kabul kapısını geçemez.");
        }

        if (variants.Any(variant => variant.MedianRetainedBytes is null))
        {
            failures.Add(
                "En az bir temsilin kalıcı maliyeti ölçülemedi (GC gürültüsü " +
                "tahsisi aştı); karşılaştırma eksiktir.");
        }

        return new TokenRepresentationComparison(
            MeasurementConstants.SchemaMajor,
            MeasurementConstants.SchemaMinor,
            MeasurementConstants.ContractVersion,
            MeasurementConstants.ToolVersion,
            "instrumented_profiler",
            "Gerçek ağaç, bellek içi, eşleştirilmiş ABBA. Üç temsil aynı öğe " +
            "dizisi üzerinde kendi token string'lerini üretir; ölçülen marjinal " +
            "kalıcı maliyettir. Ad/token/path diske yazılmaz.",
            started.ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            rounds,
            environment,
            facts,
            samples,
            variants,
            ChangePercent(baseline, variants.Single(variant => variant.Variant == ArrayVariant)),
            ChangePercent(baseline, variants.Single(variant => variant.Variant == PooledArrayVariant)),
            failures);
    }

    private static double? ChangePercent(
        TokenRepresentationVariant baseline,
        TokenRepresentationVariant candidate) =>
        baseline.MedianRetainedBytes is > 0 && candidate.MedianRetainedBytes is not null
            ? MeasurementStatistics.ChangePercent(
                baseline.MedianRetainedBytes.Value,
                candidate.MedianRetainedBytes.Value)
            : null;

    /// <summary>
    /// Ölçülemeyen örnek medyana katılmaz; hiçbiri ölçülemediyse medyan
    /// `null`'dır. Negatif GC deltasını ölçülmüş değer gibi raporlamak, bu
    /// araçta daha önce düzeltilmiş bir kusurdu.
    /// </summary>
    private static TokenRepresentationVariant Summarize(
        string variant,
        string scope,
        IReadOnlyList<TokenRepresentationSample> samples)
    {
        var all = samples.Where(sample => sample.Variant == variant).ToArray();
        var measured = all
            .Where(sample => sample.RetainedBytes is not null)
            .Select(sample => (double)sample.RetainedBytes!.Value)
            .ToArray();
        return new TokenRepresentationVariant(
            variant,
            scope,
            measured.Length == 0 ? null : (long)MeasurementStatistics.Median(measured),
            measured.Length,
            all.Length);
    }

    private static void Warmup(SearchItem[] items, ITokenizer tokenizer)
    {
        var subset = items.Take(Math.Min(20_000, items.Length)).ToArray();
        GC.KeepAlive(BuildHashSets(subset, tokenizer));
        GC.KeepAlive(BuildArrays(subset, tokenizer));
        GC.KeepAlive(BuildPooledArrays(subset, tokenizer, out _));
    }

    private static object Build(string variant, SearchItem[] items, ITokenizer tokenizer)
    {
        switch (variant)
        {
            case HashSetVariant:
                return BuildHashSets(items, tokenizer);
            case ArrayVariant:
                return BuildArrays(items, tokenizer);
            default:
                var pooled = BuildPooledArrays(items, tokenizer, out var pool);
                return new object[] { pooled, pool };
        }
    }

    private static long? MeasureRetained(Func<object> build)
    {
        Settle();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var result = build();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        var after = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(result);
        var retained = after - before;
        return retained > 0 ? retained : null;
    }

    private static void Settle()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    }

    /// <summary>
    /// Kabul kapısı: aday temsiller üretimin küme semantiğini birebir
    /// korumalıdır. Sayı eşitliği tek başına yetmez; üyelik `OrdinalIgnoreCase`
    /// ile karşılaştırılır ve dizide yinelenen token aranır.
    /// </summary>
    private static (TokenRepresentationFacts Facts, List<string> Failures) Verify(
        SearchItem[] items,
        ITokenizer tokenizer,
        int nodeCount,
        long enumerationMilliseconds)
    {
        var failures = new List<string>();
        var sets = BuildHashSets(items, tokenizer);
        var arrays = BuildArrays(items, tokenizer);
        var pooled = BuildPooledArrays(items, tokenizer, out var pool);

        var arrayMismatch = 0;
        var arrayDuplicate = 0;
        var pooledMismatch = 0;
        var notInterned = 0;
        var occurrences = 0L;
        var links = 0L;
        var maxTokens = 0;
        for (var index = 0; index < items.Length; index++)
        {
            occurrences += tokenizer.Tokenize(items[index].Name).Count();
            links += sets[index].Count;
            if (sets[index].Count > maxTokens)
            {
                maxTokens = sets[index].Count;
            }

            if (!SetEquals(sets[index], arrays[index]))
            {
                arrayMismatch++;
            }

            if (HasDuplicate(arrays[index]))
            {
                arrayDuplicate++;
            }

            if (!SetEquals(sets[index], pooled[index]))
            {
                pooledMismatch++;
            }

            foreach (var token in pooled[index])
            {
                if (!pool.TryGetValue(token, out var canonical) ||
                    !ReferenceEquals(canonical, token))
                {
                    notInterned++;
                }
            }
        }

        if (arrayMismatch > 0)
        {
            failures.Add(
                "`array` temsili öğe token kümesini değiştirdi: " +
                arrayMismatch.ToString(CultureInfo.InvariantCulture) + " öğe.");
        }

        if (arrayDuplicate > 0)
        {
            failures.Add(
                "`array` temsilinde OrdinalIgnoreCase yinelenen token var: " +
                arrayDuplicate.ToString(CultureInfo.InvariantCulture) + " öğe.");
        }

        if (pooledMismatch > 0)
        {
            failures.Add(
                "`pooled_array` temsili öğe token kümesini değiştirdi: " +
                pooledMismatch.ToString(CultureInfo.InvariantCulture) + " öğe.");
        }

        if (notInterned > 0)
        {
            failures.Add(
                "`pooled_array` token'ları havuzla paylaşılmıyor: " +
                notInterned.ToString(CultureInfo.InvariantCulture) + " başvuru.");
        }

        var facts = new TokenRepresentationFacts(
            nodeCount,
            items.Length,
            pool.Count,
            occurrences,
            links,
            maxTokens,
            enumerationMilliseconds);
        GC.KeepAlive(sets);
        GC.KeepAlive(arrays);
        GC.KeepAlive(pooled);
        return (facts, failures);
    }

    private static bool SetEquals(ImmutableHashSet<string> expected, string[] actual)
    {
        if (expected.Count != actual.Length)
        {
            return false;
        }

        foreach (var token in actual)
        {
            if (!expected.Contains(token))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasDuplicate(string[] tokens)
    {
        for (var left = 0; left < tokens.Length; left++)
        {
            for (var right = left + 1; right < tokens.Length; right++)
            {
                if (TokenComparer.Equals(tokens[left], tokens[right]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ImmutableHashSet<string>[] BuildHashSets(
        SearchItem[] items,
        ITokenizer tokenizer)
    {
        var sets = new ImmutableHashSet<string>[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            sets[index] = tokenizer.Tokenize(items[index].Name).ToImmutableHashSet(TokenComparer);
        }

        return sets;
    }

    /// <summary>
    /// Öğe başına token sayısı küçüktür (B-1: medyan `2`, `p99 = 10`), bu yüzden
    /// benzersizlik ayrı bir küme tahsis etmeden doğrusal taramayla korunur.
    /// </summary>
    private static string[][] BuildArrays(SearchItem[] items, ITokenizer tokenizer)
    {
        var arrays = new string[items.Length][];
        var buffer = new List<string>();
        for (var index = 0; index < items.Length; index++)
        {
            buffer.Clear();
            foreach (var token in tokenizer.Tokenize(items[index].Name))
            {
                var seen = false;
                for (var existing = 0; existing < buffer.Count; existing++)
                {
                    if (TokenComparer.Equals(buffer[existing], token))
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

            arrays[index] = buffer.ToArray();
        }

        return arrays;
    }

    /// <summary>
    /// `array` ile aynı kap, farkı token string'lerinin tekilleştirilmesi.
    /// Havuz ölçüme dahil edilir; üretimde havuzdaki benzersiz string'ler
    /// `_pathsByToken` anahtarlarıyla paylaşılacağı için gerçek marjinal kazanç
    /// burada ölçülenden **daha büyüktür**, küçük değil.
    /// </summary>
    private static string[][] BuildPooledArrays(
        SearchItem[] items,
        ITokenizer tokenizer,
        out Dictionary<string, string> pool)
    {
        pool = new Dictionary<string, string>(TokenComparer);
        var arrays = new string[items.Length][];
        var buffer = new List<string>();
        for (var index = 0; index < items.Length; index++)
        {
            buffer.Clear();
            foreach (var token in tokenizer.Tokenize(items[index].Name))
            {
                if (!pool.TryGetValue(token, out var canonical))
                {
                    canonical = token;
                    pool[token] = canonical;
                }

                if (!buffer.Contains(canonical))
                {
                    buffer.Add(canonical);
                }
            }

            arrays[index] = buffer.ToArray();
        }

        return arrays;
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
            .GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();
}

internal static class TokenRepresentationSummaryFormatter
{
    internal static string Format(TokenRepresentationComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var builder = new StringBuilder();
        var facts = comparison.Facts;
        builder.AppendLine("_tokensByPath değer temsili — eşleştirilmiş ABBA karşılaştırma");
        builder.AppendLine(
            "  ağaç: " + Count(facts.NodeCount) + " düğüm, " +
            Count(facts.DistinctItemCount) + " tekil öğe, tarama " +
            (facts.EnumerationMilliseconds / 1000d).ToString("N1", CultureInfo.InvariantCulture) + " sn");
        builder.AppendLine(
            "  token: " + Count(facts.UniqueTokenCount) + " benzersiz, " +
            Count(facts.DistinctTokenLinkCount) + " bağlantı, " +
            Count(facts.TokenOccurrenceCount) + " ham geçiş, öğe başına max " +
            Count(facts.MaxTokensPerItem));
        builder.AppendLine(
            "  koşum: " + comparison.Samples.Count.ToString(CultureInfo.InvariantCulture) +
            " (ABBA eşleştirilmiş, process içi)");
        builder.AppendLine();
        builder.AppendLine("  temsil          kalıcı MiB   örnek   kapsam");
        foreach (var variant in comparison.Variants)
        {
            builder.AppendLine(
                "  " + variant.Variant.PadRight(15) +
                Optional(variant.MedianRetainedBytes).PadLeft(10) +
                (variant.MeasuredSampleCount.ToString(CultureInfo.InvariantCulture) + "/" +
                    variant.SampleCount.ToString(CultureInfo.InvariantCulture)).PadLeft(8) +
                "   " + variant.Scope);
        }

        builder.AppendLine();
        builder.AppendLine(
            "  array değişimi:        " + Percent(comparison.ArrayChangePercent) +
            "   (hedef temsil)");
        builder.AppendLine(
            "  pooled_array değişimi: " + Percent(comparison.PooledArrayChangePercent) +
            "   (bilgi amaçlı, havuz dahil)");
        if (comparison.AcceptanceFailures.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("  KABUL KAPISI:");
            foreach (var failure in comparison.AcceptanceFailures)
            {
                builder.AppendLine("    - " + failure);
            }
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine("  Kabul kapısı geçildi: her iki aday da küme semantiğini korudu.");
        }

        return builder.ToString();
    }

    private static string Count(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Optional(long? bytes) =>
        bytes is null ? "ölçülemedi" : Mib(bytes.Value);

    private static string Percent(double? value) =>
        value is null
            ? "hesaplanamadı"
            : value.Value.ToString("N2", CultureInfo.InvariantCulture) + "%";

    private static string Mib(double bytes) =>
        (bytes / 1_048_576d).ToString("N1", CultureInfo.InvariantCulture);
}
