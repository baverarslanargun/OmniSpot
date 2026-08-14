using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniSpot.Benchmarking.Profiling;

internal enum ProfileRootKind
{
    Desktop,
    Documents,
    Downloads,
    Pictures,
    Music,
    Videos,
    Custom
}

internal enum ProfileErrorKind
{
    RootUnavailable,
    RootMetadataUnavailable,
    DirectoryInaccessible,
    DirectoryEnumerationFailed,
    EntryMetadataUnavailable
}

internal sealed record ProfileRootRequest(
    string Path,
    ProfileRootKind Kind,
    int Ordinal);

internal sealed record ProfileDocument(
    int SchemaMajor,
    int SchemaMinor,
    string ProfilerVersion,
    string MetricsFingerprint,
    ProfileManifest Manifest,
    ProfileMetrics Metrics);

internal sealed record ProfileManifest(
    long StartedUnixSeconds,
    long CompletedUnixSeconds,
    long DurationMilliseconds,
    IReadOnlyList<ProfileRootMetric> Roots,
    ProfileEnvironment Environment);

internal sealed record ProfileRootMetric(
    ProfileRootKind Kind,
    int Ordinal,
    long ItemCount,
    long OverlapSkippedCount,
    long InaccessibleDirectoryCount);

internal sealed record ProfileEnvironment(
    string OsDescription,
    string FrameworkDescription,
    string? DotnetSdkVersion,
    string ProcessArchitecture,
    string ProcessorModel,
    int LogicalProcessorCount,
    long TotalAvailableMemoryBytes,
    bool ServerGc,
    string GcLatencyMode,
    bool OmniSpotProcessRunning,
    string? RepoHead,
    bool? RepoDirty,
    int? RepoDirtyEntryCount,
    string? PowerPlanGuid,
    bool? DefenderRealtimeEnabled,
    bool? WindowsSearchRunning,
    string? DiskKind);

internal sealed record ProfileMetrics(
    long TotalItemCount,
    long FileCount,
    long DirectoryCount,
    DistributionSummary Depth,
    DistributionSummary FileNameLength,
    DistributionSummary DirectoryNameLength,
    DistributionSummary FilesPerDirectory,
    DistributionSummary DirectoriesPerDirectory,
    DistributionSummary ChildrenPerDirectory,
    DistributionSummary FileSizeBytes,
    ExtensionProfile Extensions,
    TokenProfile Tokens,
    NameCultureProfile Names,
    SpecialProfile SpecialCases,
    IReadOnlyList<ProfileErrorMetric> Errors);

internal sealed record DistributionSummary(
    long Count,
    long P50,
    long P90,
    long P95,
    long P99,
    long Max,
    IReadOnlyList<HistogramBucket> Histogram);

internal sealed record HistogramBucket(
    string Label,
    long Count);

internal sealed record ExtensionProfile(
    IReadOnlyList<ExtensionMetric> Published,
    long OtherFileCount,
    long NoExtensionFileCount);

internal sealed record ExtensionMetric(
    string Extension,
    long Count,
    double FileRatio);

internal sealed record TokenProfile(
    long ItemsWithTokens,
    double ItemsWithTokensRatio,
    long UniqueTokenCount,
    long TokenItemEdgeCount,
    DistributionSummary TokensPerItem,
    DistributionSummary DocumentFrequency,
    IReadOnlyList<TokenFrequencyBucket> FanOutHistogram,
    double SingletonTokenRatio,
    double DuplicateAssignmentRatio,
    double SharedTokenEdgeRatio);

internal sealed record TokenFrequencyBucket(
    string Label,
    long TokenCount,
    long TokenItemEdgeCount);

internal sealed record NameCultureProfile(
    long TotalNameCount,
    long LetteredNameCount,
    long TurkishSpecificNameCount,
    double TurkishSpecificNameRatio,
    long DottedOrDotlessINameCount,
    double DottedOrDotlessINameRatio,
    long AllUppercaseNameCount,
    double AllUppercaseNameRatio,
    long CultureFoldDifferenceNameCount,
    double CultureFoldDifferenceNameRatio,
    long AsciiOnlyNameCount,
    double AsciiOnlyNameRatio,
    long NonAsciiNameCount,
    double NonAsciiNameRatio);

internal sealed record SpecialProfile(
    long ReparsePointCount,
    long? JunctionCount,
    long? SymbolicLinkCount,
    long? OtherReparsePointCount,
    long CaseOnlyPairCount,
    long LongPathCount,
    long WhitespaceNameCount,
    long PercentNameCount,
    long HiddenItemCount,
    double HiddenItemRatio,
    long SystemItemCount,
    double SystemItemRatio,
    long InaccessibleDirectoryCount);

internal sealed record ProfileErrorMetric(
    ProfileErrorKind Kind,
    long Count);

internal static class ProfileJson
{
    internal static JsonSerializerOptions Options { get; } = CreateOptions(writeIndented: true);

    private static JsonSerializerOptions FingerprintOptions { get; } =
        CreateOptions(writeIndented: false);

    internal static string Serialize(ProfileDocument document) =>
        JsonSerializer.Serialize(document, Options);

    internal static string SerializeMetrics(ProfileMetrics metrics) =>
        JsonSerializer.Serialize(metrics, Options);

    internal static string SerializeCanonicalMetrics(ProfileMetrics metrics) =>
        JsonSerializer.Serialize(metrics, FingerprintOptions);

    internal static string ComputeMetricsFingerprint(ProfileMetrics metrics)
    {
        var canonicalBytes = Encoding.UTF8.GetBytes(SerializeCanonicalMetrics(metrics));
        return Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = writeIndented
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
