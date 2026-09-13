using SmartFileLauncher.Core.Indexing.Ntfs;
using Xunit;
using static SmartFileLauncher.Core.Tests.Indexing.Ntfs.NtfsMftTestData;

namespace SmartFileLauncher.Core.Tests.Indexing.Ntfs;

public sealed class NtfsMftRecordParserTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsMetadataWithoutUsingFileNameTimestampCopies(bool nonresident)
    {
        var data = nonresident
            ? Nonresident(0x80, 9876543210, 0, 0, 0x11, 1, 20, 0)
            : Resident(0x80, [1, 2, 3]);
        var bytes = Protect(Record(Basic(FileAttributes.Hidden), Name("rapor.pdf"), data));

        var record = Assert.IsType<NtfsMftRecord>(NtfsMftRecordParser.Parse(bytes, 42, true));

        Assert.Equal(Reference(42), record.Reference);
        Assert.Equal(nonresident ? 9876543210 : 3, record.SizeBytes);
        Assert.Equal(Created.Ticks, record.BasicInformation!.CreatedTimeUtc);
        Assert.Equal(Modified.Ticks, record.BasicInformation.LastWriteTimeUtc);
        Assert.Equal(FileAttributes.Hidden, record.BasicInformation.Attributes);
        Assert.Equal(new NtfsFileName(Reference(5), "rapor.pdf", 1), Assert.Single(record.Names));
        Assert.Empty(record.DataExtents);
    }

    [Fact]
    public void PreservesUnpairedUtf16AndEveryFileNameAttribute()
    {
        var bytes = Protect(Record(Basic(), Name("bir\ud800.txt"), Name("ikinci.txt", 6),
            Name("BIR~1.TXT", nameSpace: 2)));

        var record = NtfsMftRecordParser.Parse(bytes, 42, true)!;

        Assert.Equal(3, record.Names.Count);
        Assert.Equal("bir\ud800.txt", record.Names[0].Name);
        Assert.Equal(Reference(6), record.Names[1].ParentReference);
        Assert.Equal(2, record.Names[2].Namespace);
    }

    [Fact]
    public void RestoresSectorTailBeforeReadingLongName()
    {
        var name = new string('x', 220) + ".txt";
        var bytes = Protect(Record(Basic(), Name(name)));

        Assert.Equal(name, Assert.Single(NtfsMftRecordParser.Parse(bytes, 8, true)!.Names).Name);
    }

    [Fact]
    public void RejectsTornSectorWithoutPartiallyRestoringTheRecord()
    {
        var bytes = Protect(Record(Basic(), Name("dosya.txt")));
        bytes[1022] ^= 1;
        var before = bytes.ToArray();

        Assert.Throws<InvalidDataException>(() => NtfsMftRecordParser.Parse(bytes, 8, true));

        Assert.Equal(before, bytes);
    }

    [Theory]
    [InlineData(60, 0)]
    [InlineData(60, 1024)]
    [InlineData(24, 4096)]
    public void RejectsInvalidRecordAndAttributeBounds(int offset, uint value)
    {
        var bytes = Record(Basic(), Name("dosya.txt"));
        Write32(bytes, offset, value);

        Assert.Throws<InvalidDataException>(() => NtfsMftRecordParser.Parse(bytes, 8, false));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("a\\b")]
    [InlineData("a\0b")]
    public void RejectsNamesThatAreNotSinglePathComponents(string name)
    {
        var bytes = Record(Basic(), Name(name));

        Assert.Throws<InvalidDataException>(() => NtfsMftRecordParser.Parse(bytes, 8, false));
    }

    [Fact]
    public void DecodesNegativeLcnDeltasAndSparseRuns()
    {
        var runs = NtfsMftRecordParser.ParseDataRuns([0x11, 2, 100, 0x11, 1, 251, 0x01, 3, 0], 10, 15);

        Assert.Equal(new[]
        {
            new NtfsDataRun(10, 2, 100), new NtfsDataRun(12, 1, 95), new NtfsDataRun(13, 3, null)
        }, runs);
    }

    [Theory]
    [InlineData(new byte[] { 0x11, 1 })]
    [InlineData(new byte[] { 0x10, 1, 0 })]
    [InlineData(new byte[] { 0x11, 0, 1, 0 })]
    [InlineData(new byte[] { 0x11, 1, 255, 0 })]
    [InlineData(new byte[] { 0x11, 2, 1, 0 })]
    [InlineData(new byte[] { 0x11, 1, 1 })]
    public void RejectsMalformedOrIncompleteDataRuns(byte[] bytes)
    {
        Assert.Throws<InvalidDataException>(() => NtfsMftRecordParser.ParseDataRuns(bytes, 0, 0));
    }

    [Fact]
    public void RejectsRunCountOverflow()
    {
        Assert.Throws<InvalidDataException>(() => NtfsMftRecordParser.ParseDataRuns(
            [0x18, 255, 255, 255, 255, 255, 255, 255, 255, 1, 0], 0, 0));
    }

    [Fact]
    public void SkipsUnusedAndZeroFilledRecords()
    {
        Assert.Null(NtfsMftRecordParser.Parse(new byte[1024], 4, true));
        var bytes = Record(Basic(), Name("silinmis.txt"));
        Write16(bytes, 22, 0);
        Assert.Null(NtfsMftRecordParser.Parse(bytes, 4, true));
    }
}
