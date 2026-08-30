using SmartFileLauncher.Core.ChangeFeed.Usn;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class UsnRecordParserTests
{
    [Fact]
    public void Parse_ReadsVersion2Record()
    {
        var buffer = new UsnRecordBuffer()
            .AddVersion2(
                usn: 4096,
                fileReference: 0x1122334455667788,
                parentReference: 0x00000000000000AA,
                reason: UsnReason.FileCreate | UsnReason.Close,
                name: "rapor.txt")
            .Build();

        var records = UsnRecordParser.Parse(buffer);

        var record = Assert.Single(records);
        Assert.Equal(4096, record.Usn);
        Assert.Equal(UsnFileReference.FromNtfs(0x1122334455667788), record.FileReference);
        Assert.Equal(UsnFileReference.FromNtfs(0xAA), record.ParentFileReference);
        Assert.Equal(UsnReason.FileCreate | UsnReason.Close, record.Reason);
        Assert.Equal("rapor.txt", record.Name);
        Assert.False(record.IsDirectory);
    }

    [Fact]
    public void Parse_ReadsVersion3RecordWithFullWidthIdentity()
    {
        var fileReference = new UsnFileReference(0x1111111111111111, 0x2222222222222222);
        var parentReference = new UsnFileReference(0x3333333333333333, 0x4444444444444444);
        var buffer = new UsnRecordBuffer()
            .AddVersion3(
                usn: 8192,
                fileReference,
                parentReference,
                UsnReason.FileDelete,
                "alt",
                FileAttributes.Directory)
            .Build();

        var records = UsnRecordParser.Parse(buffer);

        var record = Assert.Single(records);
        Assert.Equal(8192, record.Usn);
        Assert.Equal(fileReference, record.FileReference);
        Assert.Equal(parentReference, record.ParentFileReference);
        Assert.Equal(UsnReason.FileDelete, record.Reason);
        Assert.Equal("alt", record.Name);
        Assert.True(record.IsDirectory);
    }

    [Fact]
    public void Parse_ReadsMixedVersionsInOneBuffer()
    {
        var buffer = new UsnRecordBuffer()
            .AddVersion2(1, 10, 1, UsnReason.FileCreate, "a.txt")
            .AddVersion3(2, UsnFileReference.FromNtfs(11), UsnFileReference.FromNtfs(1),
                UsnReason.DataExtend, "b.txt")
            .AddVersion2(3, 12, 1, UsnReason.FileDelete, "c.txt")
            .Build();

        var records = UsnRecordParser.Parse(buffer);

        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, records.Select(record => record.Name));
        Assert.Equal(new long[] { 1, 2, 3 }, records.Select(record => record.Usn));
    }

    [Fact]
    public void Parse_EmptyBufferYieldsNoRecords()
    {
        Assert.Empty(UsnRecordParser.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Parse_UnsupportedVersionThrows()
    {
        var buffer = new UsnRecordBuffer()
            .AddVersion2(1, 10, 1, UsnReason.FileCreate, "a.txt")
            .Build();
        BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)4);

        Assert.Throws<UsnRecordFormatException>(() => UsnRecordParser.Parse(buffer));
    }

    [Fact]
    public void Parse_TruncatedBufferThrows()
    {
        var buffer = new UsnRecordBuffer()
            .AddVersion2(1, 10, 1, UsnReason.FileCreate, "a.txt")
            .Build();

        Assert.Throws<UsnRecordFormatException>(
            () => UsnRecordParser.Parse(buffer.AsSpan(0, buffer.Length - 8)));
    }

    [Fact]
    public void Parse_NameOutsideRecordThrows()
    {
        var buffer = new UsnRecordBuffer()
            .AddVersion2(1, 10, 1, UsnReason.FileCreate, "a.txt")
            .Build();
        BitConverter.TryWriteBytes(buffer.AsSpan(56), (ushort)512);

        Assert.Throws<UsnRecordFormatException>(() => UsnRecordParser.Parse(buffer));
    }

    [Fact]
    public void Parse_RecordLengthShorterThanHeaderThrows()
    {
        var buffer = new UsnRecordBuffer()
            .AddVersion2(1, 10, 1, UsnReason.FileCreate, "a.txt")
            .Build();
        BitConverter.TryWriteBytes(buffer.AsSpan(0), 16u);

        Assert.Throws<UsnRecordFormatException>(() => UsnRecordParser.Parse(buffer));
    }
}
