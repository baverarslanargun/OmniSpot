using SmartFileLauncher.Core.Indexing.Ntfs;
using Xunit;
using static SmartFileLauncher.Core.Tests.Indexing.Ntfs.NtfsMftTestData;

namespace SmartFileLauncher.Core.Tests.Indexing.Ntfs;

public sealed class NtfsMftReaderTests
{
    [Fact]
    public void ReadsFragmentedMftUsingItsExtensionRecord()
    {
        var source = new FakeNtfsVolumeSource();
        source.Add(0, Record(Basic(), Name("$MFT"),
            Nonresident(0x80, 4096, 0, 3, 0x11, 4, 8, 0),
            AttributeList((0x80, Reference(0)), (0x80, Reference(3)))), 4096);
        source.Add(1, Record(Basic(), Name("bir.txt"), Resident(0x80, [1, 2, 3])), 5120);
        source.Add(2, Record(Basic(), Name("iki.txt"), Resident(0x80, [4, 5])), 10240);
        source.Add(3, Record(false, Reference(0),
            Nonresident(0x80, 0, 4, 7, 0x11, 4, 20, 0)), 11264);
        using var reader = new NtfsMftReader(source);

        var entries = reader.ReadEntries().ToArray();

        Assert.Equal(new[] { "$MFT", "bir.txt", "iki.txt" }, entries.Select(entry => entry.Name));
        Assert.Equal(new[] { (4096L, 2048), (10240L, 2048) }, source.Reads);
        Assert.Contains(Reference(3), source.RecordReads);
        Assert.Equal(3, entries[1].SizeBytes);
        Assert.Equal(2, entries[2].SizeBytes);
    }

    [Fact]
    public void IncludesHardlinkNamesFromExtensionsButExcludesDosAliases()
    {
        var source = LinearSource();
        source.Add(1, Record(Basic(), Name("asil.txt"), Resident(0x80, [1]),
            AttributeList((0x30, Reference(1)), (0x30, Reference(2)))), 5120);
        source.Add(2, Record(false, Reference(1), Name("bag.txt", 6),
            Name("ASIL~1.TXT", nameSpace: 2)), 6144);
        using var reader = new NtfsMftReader(source);

        var entries = reader.ReadEntries().Where(entry => entry.FileReference == Reference(1)).ToArray();

        Assert.Equal(new[] { "asil.txt", "bag.txt" }, entries.Select(entry => entry.Name));
        Assert.Equal(new[] { Reference(5), Reference(6) }, entries.Select(entry => entry.ParentFileReference));
        Assert.All(entries, entry => Assert.Equal(1, entry.SizeBytes));
    }

    [Fact]
    public void TornLiveRecordIsRetriedOnceThroughTheKernel()
    {
        var source = LinearSource();
        source.Add(1, Record(Basic(), Name("guncellenen.txt"), Resident(0x80, [1, 2])), 5120);
        source.CorruptByte(5120 + 1022);
        using var reader = new NtfsMftReader(source);

        var entry = Assert.Single(reader.ReadEntries(), entry => entry.Name == "guncellenen.txt");

        Assert.Equal(2, entry.SizeBytes);
        Assert.Single(source.RecordReads, reference => reference == 1);
    }

    [Fact]
    public void TornRecordDeletedBeforeKernelRetryIsSkipped()
    {
        var source = LinearSource();
        source.Add(1, Record(Basic(), Name("silinen.txt")), 5120);
        source.CorruptByte(5120 + 1022);
        source.RemoveKernelRecord(1);
        using var reader = new NtfsMftReader(source);

        Assert.DoesNotContain(reader.ReadEntries(), entry => entry.Name == "silinen.txt");
        Assert.Single(source.RecordReads, reference => reference == 1);
    }

    [Fact]
    public void RejectsExtensionReusedByAnotherFile()
    {
        var source = LinearSource();
        source.Add(1, Record(Basic(), Name("asil.txt"),
            AttributeList((0x30, Reference(2)))), 5120);
        source.Add(2, Record(false, Reference(3), Name("baska.txt")), 6144);
        using var reader = new NtfsMftReader(source);

        Assert.Throws<InvalidDataException>(() => reader.ReadEntries().ToArray());
    }

    [Fact]
    public void RejectsStaleExtensionSequence()
    {
        var source = LinearSource();
        source.Add(1, Record(Basic(), Name("asil.txt"),
            AttributeList((0x30, Reference(2, 5)))), 5120);
        source.Add(2, Record(false, Reference(1), Name("baska.txt")), 6144);
        using var reader = new NtfsMftReader(source);

        Assert.Throws<InvalidDataException>(() => reader.ReadEntries().ToArray());
    }

    [Fact]
    public void CanceledEnumerationDoesNotReadVolumeData()
    {
        var source = LinearSource();
        using var reader = new NtfsMftReader(source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => reader.ReadEntries(cancellation.Token).ToArray());
        Assert.Empty(source.Reads);
    }

    [Fact]
    public void EnumerationOwnershipIsReleasedWhenCallerStopsEarly()
    {
        var source = LinearSource();
        using var reader = new NtfsMftReader(source);
        using (var first = reader.ReadEntries().GetEnumerator())
        {
            Assert.True(first.MoveNext());
            Assert.Throws<InvalidOperationException>(() => reader.ReadEntries().ToArray());
        }

        Assert.Single(reader.ReadEntries());
    }

    [Fact]
    public void ConstructorFailureDisposesVolumeSource()
    {
        var source = new FakeNtfsVolumeSource();
        source.Add(0, Record(Basic(), Name("$MFT"),
            Nonresident(0x80, 4096, 1, 8, 0x11, 8, 8, 0)), 4096);

        Assert.Throws<InvalidDataException>(() => new NtfsMftReader(source));
        Assert.True(source.Disposed);
    }

    [Fact]
    public void DisposeClosesTheSourceAndPreventsFurtherEnumeration()
    {
        var source = LinearSource();
        var reader = new NtfsMftReader(source);

        reader.Dispose();

        Assert.True(source.Disposed);
        Assert.Throws<ObjectDisposedException>(() => reader.ReadEntries().ToArray());
    }

    private static FakeNtfsVolumeSource LinearSource()
    {
        var source = new FakeNtfsVolumeSource();
        source.Add(0, Record(Basic(), Name("$MFT"),
            Nonresident(0x80, 4096, 0, 7, 0x11, 8, 8, 0)), 4096);
        return source;
    }
}
