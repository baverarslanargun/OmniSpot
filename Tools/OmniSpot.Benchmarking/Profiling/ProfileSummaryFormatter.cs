using System.Globalization;
using System.Text;

namespace OmniSpot.Benchmarking.Profiling;

internal static class ProfileSummaryFormatter
{
    internal const int MaximumLineCount = 120;
    internal const int MaximumUtf8Bytes = 16 * 1024;

    internal static string Format(ProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder();
        AppendLine(builder, "OmniSpot B-1 profil özeti");
        AppendLine(builder, $"Şema: {document.SchemaMajor}.{document.SchemaMinor} | profiler: {document.ProfilerVersion}");
        AppendLine(builder, $"Metrics SHA-256: {document.MetricsFingerprint}");
        AppendLine(builder, $"Süre: {document.Manifest.DurationMilliseconds} ms");
        AppendFrequencySummary(builder, document.Manifest.Environment);
        AppendLine(builder, $"Kök sayısı: {document.Manifest.Roots.Count}");

        foreach (var root in document.Manifest.Roots.Take(20))
        {
            AppendLine(builder,
                $"  {RootLabel(root)}: öğe={root.ItemCount}, overlap={root.OverlapSkippedCount}, erişilemeyen={root.InaccessibleDirectoryCount}");
        }

        if (document.Manifest.Roots.Count > 20)
        {
            AppendLine(builder, $"  ... {document.Manifest.Roots.Count - 20} kök yalnız JSON'da");
        }

        var metrics = document.Metrics;
        AppendLine(builder, "Ağaç ve metadata");
        AppendLine(builder,
            $"  toplam={metrics.TotalItemCount}, dosya={metrics.FileCount}, klasör={metrics.DirectoryCount}");
        AppendDistribution(builder, "derinlik", metrics.Depth);
        AppendDistribution(builder, "dosya adı", metrics.FileNameLength);
        AppendDistribution(builder, "klasör adı", metrics.DirectoryNameLength);
        AppendDistribution(builder, "dosya/dizin", metrics.FilesPerDirectory);
        AppendDistribution(builder, "klasör/dizin", metrics.DirectoriesPerDirectory);
        AppendDistribution(builder, "çocuk/dizin", metrics.ChildrenPerDirectory);
        AppendDistribution(builder, "dosya byte", metrics.FileSizeBytes);

        AppendTokenSummary(builder, metrics.Tokens);
        AppendNameSummary(builder, metrics.Names);
        AppendExtensionSummary(builder, metrics.Extensions, metrics.FileCount);
        AppendSpecialSummary(builder, metrics.SpecialCases);
        AppendErrorSummary(builder, metrics.Errors);

        var summary = builder.ToString().TrimEnd() + Environment.NewLine;
        if (summary.Split('\n').Length > MaximumLineCount ||
            Encoding.UTF8.GetByteCount(summary) > MaximumUtf8Bytes)
        {
            throw new InvalidOperationException("Profil özeti boyut sınırını aştı.");
        }

        return summary;
    }

    private static void AppendFrequencySummary(
        StringBuilder builder,
        ProfileEnvironment environment)
    {
        AppendLine(
            builder,
            "CPU frekansı: " +
            $"AC={Value(environment.ProcessorThrottleMaxAcStartPercent)}→" +
            $"{Value(environment.ProcessorThrottleMaxAcEndPercent)}%, " +
            $"DC={Value(environment.ProcessorThrottleMaxDcStartPercent)}→" +
            $"{Value(environment.ProcessorThrottleMaxDcEndPercent)}%, " +
            $"base={Value(environment.ProcessorNominalBaseMhz)} MHz, " +
            $"yük={Value(environment.ProcessorFrequencyStartMhz)}→" +
            $"{Value(environment.ProcessorFrequencyEndMhz)} MHz, " +
            $"kayma={Ratio(environment.ProcessorFrequencyDriftPercent)}");
        if (environment.Labels.Count > 0)
        {
            AppendLine(builder, "Ortam etiketleri: " + string.Join(", ", environment.Labels));
        }
    }

    private static string Value(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "ölçülmedi";

    private static string Ratio(double? value) =>
        value is double ratio
            ? "%" + ratio.ToString("0.00", CultureInfo.InvariantCulture)
            : "ölçülmedi";

    private static string RootLabel(ProfileRootMetric root) =>
        root.Kind == ProfileRootKind.Custom
            ? $"custom-{root.Ordinal}"
            : root.Kind.ToString().ToLowerInvariant();

    private static void AppendDistribution(
        StringBuilder builder,
        string label,
        DistributionSummary distribution)
    {
        AppendLine(builder,
            $"  {label}: p50={distribution.P50}, p95={distribution.P95}, p99={distribution.P99}, max={distribution.Max}");
    }

    private static void AppendTokenSummary(StringBuilder builder, TokenProfile tokens)
    {
        AppendLine(builder, "Token fan-out");
        AppendLine(builder,
            $"  tokenlı öğe={tokens.ItemsWithTokens} ({Percent(tokens.ItemsWithTokensRatio)}), benzersiz token={tokens.UniqueTokenCount}, bağlantı={tokens.TokenItemEdgeCount}");
        AppendDistribution(builder, "öğe başına token", tokens.TokensPerItem);
        AppendDistribution(builder, "token df", tokens.DocumentFrequency);
        AppendLine(builder,
            $"  tekil={Percent(tokens.SingletonTokenRatio)}, yinelenen bağlantı={Percent(tokens.DuplicateAssignmentRatio)}, paylaşılan bağlantı={Percent(tokens.SharedTokenEdgeRatio)}");
        foreach (var bucket in tokens.FanOutHistogram)
        {
            AppendLine(builder,
                $"  df {bucket.Label}: token={bucket.TokenCount}, bağlantı={bucket.TokenItemEdgeCount}");
        }
    }

    private static void AppendNameSummary(StringBuilder builder, NameCultureProfile names)
    {
        AppendLine(builder, "Türkçe ve harf durumu");
        AppendLine(builder,
            $"  harfli={names.LetteredNameCount}, Türkçe karakter={names.TurkishSpecificNameCount} ({Percent(names.TurkishSpecificNameRatio)})");
        AppendLine(builder,
            $"  I/İ/ı/i={names.DottedOrDotlessINameCount} ({Percent(names.DottedOrDotlessINameRatio)}), tümü büyük={names.AllUppercaseNameCount} ({Percent(names.AllUppercaseNameRatio)})");
        AppendLine(builder,
            $"  culture fold farkı={names.CultureFoldDifferenceNameCount} ({Percent(names.CultureFoldDifferenceNameRatio)})");
        AppendLine(builder,
            $"  ASCII={names.AsciiOnlyNameCount} ({Percent(names.AsciiOnlyNameRatio)}), non-ASCII={names.NonAsciiNameCount} ({Percent(names.NonAsciiNameRatio)})");
    }

    private static void AppendExtensionSummary(
        StringBuilder builder,
        ExtensionProfile extensions,
        long fileCount)
    {
        const int visibleExtensionLimit = 20;
        AppendLine(builder, "Uzantılar (yalnız gizlilik eşiğini geçenler)");
        foreach (var extension in extensions.Published.Take(visibleExtensionLimit))
        {
            AppendLine(builder,
                $"  {extension.Extension}: {extension.Count} ({Percent(extension.FileRatio)})");
        }

        var omittedExtensions = extensions.Published.Skip(visibleExtensionLimit).ToArray();
        if (omittedExtensions.Length > 0)
        {
            var omittedFileCount = omittedExtensions.Sum(extension => extension.Count);
            var omittedFileRatio = fileCount == 0
                ? 0
                : (double)omittedFileCount / fileCount;
            AppendLine(builder,
                $"  ... {omittedExtensions.Length} uzantı daha ({omittedFileCount} dosya, {Percent(omittedFileRatio)}) yalnız JSON'da");
        }

        AppendLine(builder,
            $"  other={extensions.OtherFileCount}, uzantısız={extensions.NoExtensionFileCount}");
    }

    private static void AppendSpecialSummary(StringBuilder builder, SpecialProfile special)
    {
        AppendLine(builder, "Özel durumlar");
        if (special.JunctionCount is null)
        {
            AppendLine(builder,
                $"  reparse={special.ReparsePointCount}; junction/symlink/other ayrımı ölçülmedi");
        }
        else
        {
            AppendLine(builder,
                $"  reparse={special.ReparsePointCount} (junction={special.JunctionCount}, symlink={special.SymbolicLinkCount}, other={special.OtherReparsePointCount})");
        }
        AppendLine(builder,
            $"  case-only çift={special.CaseOnlyPairCount}, uzun path={special.LongPathCount}, erişilemeyen dizin={special.InaccessibleDirectoryCount}");
        AppendLine(builder,
            $"  boşluklu ad={special.WhitespaceNameCount}, yüzde işaretli ad={special.PercentNameCount}");
        AppendLine(builder,
            $"  hidden={special.HiddenItemCount} ({Percent(special.HiddenItemRatio)}), system={special.SystemItemCount} ({Percent(special.SystemItemRatio)})");
    }

    private static void AppendErrorSummary(
        StringBuilder builder,
        IReadOnlyList<ProfileErrorMetric> errors)
    {
        AppendLine(builder, "Hatalar");
        foreach (var error in errors)
        {
            AppendLine(builder, $"  {error.Kind.ToString().ToLowerInvariant()}={error.Count}");
        }
    }

    private static string Percent(double value) =>
        value.ToString("P2", CultureInfo.InvariantCulture);

    private static void AppendLine(StringBuilder builder, string value) =>
        builder.AppendLine(value);
}
