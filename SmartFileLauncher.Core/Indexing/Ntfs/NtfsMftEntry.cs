namespace SmartFileLauncher.Core.Indexing.Ntfs;

public sealed record NtfsMftEntry(
    ulong FileReference,
    ulong ParentFileReference,
    string Name,
    bool IsDirectory,
    FileAttributes Attributes,
    long SizeBytes,
    long CreatedTimeUtc,
    long LastWriteTimeUtc);

internal readonly record struct NtfsDataRun(long StartVcn, long ClusterCount, long? Lcn);

internal sealed record NtfsDataExtent(
    long LowestVcn,
    long Length,
    IReadOnlyList<NtfsDataRun> Runs);

internal sealed record NtfsFileName(ulong ParentReference, string Name, byte Namespace);

internal sealed record NtfsBasicInformation(
    long CreatedTimeUtc,
    long LastWriteTimeUtc,
    FileAttributes Attributes);

internal sealed class NtfsMftRecord
{
    public ulong Reference { get; init; }
    public ulong BaseReference { get; init; }
    public bool IsDirectory { get; init; }
    public NtfsBasicInformation? BasicInformation { get; set; }
    public long? SizeBytes { get; set; }
    public List<NtfsFileName> Names { get; } = [];
    public List<NtfsDataExtent> DataExtents { get; } = [];
    public byte[]? ResidentAttributeList { get; set; }
    public NtfsDataExtent? NonresidentAttributeList { get; set; }
}
