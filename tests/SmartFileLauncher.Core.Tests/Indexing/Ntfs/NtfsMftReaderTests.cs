using SmartFileLauncher.Core.Indexing.Ntfs;
using Xunit;
using static SmartFileLauncher.Core.Tests.Indexing.Ntfs.NtfsMftTestData;

namespace SmartFileLauncher.Core.Tests.Indexing.Ntfs;

public sealed class NtfsMftReaderTests
{
    [Fact]
    public void CurrentReferenceRejectsReusedSequenceAndReadsReparseTag()
    {
        var source = LinearSource();
        var value = new byte[8];
        Write32(value, 0, 0xA0000003);
        source.Add(1, Record(true, 0, Basic(FileAttributes.Directory | FileAttributes.ReparsePoint),
            Name("junction"), Resident(0xC0, value)), 5120);
        using var reader = new NtfsMftReader(source);
        Assert.Empty(reader.ReadCurrent(Reference(1, 2), default));
        Assert.Equal(0xA0000003u, Assert.Single(reader.ReadCurrent(Reference(1), default)).ReparseTag);
        Assert.Equal(0xA0000003u, Assert.Single(reader.ReadEntries(), item => item.Name == "junction").ReparseTag);
    }

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
        Assert.DoesNotContain(Reference(1), source.RecordReads);
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
        Assert.Single(source.RecordReads, reference => reference == Reference(1));
        Assert.Equal(2, source.RecordReads.Count(reference => reference == Reference(2, 5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasedOrReusedExtensionRefreshesTheBaseOnce(bool reused)
    {
        var source = LinearSource();
        source.Add(1, Record(Basic(), Name("eski.txt"), Resident(0x80, [1]),
            AttributeList((0x30, Reference(3)), (0x30, Reference(2, 5)))), 5120);
        source.Add(3, Record(false, Reference(1), Name("eski-ek.txt")), 7168);
        if (reused) source.Add(2, Record(false, Reference(3), Name("baska.txt")), 6144);
        using var reader = new NtfsMftReader(source);
        using var entries = reader.ReadEntries().GetEnumerator();
        Assert.True(entries.MoveNext());
        Assert.Equal("$MFT", entries.Current.Name);
        source.Add(1, Record(Basic(), Name("guncel.txt"), Resident(0x80, [1, 2, 3])), 5120);

        Assert.True(entries.MoveNext());
        Assert.Equal("guncel.txt", entries.Current.Name);
        Assert.Equal(3, entries.Current.SizeBytes);
        Assert.False(entries.MoveNext());
        Assert.Single(source.RecordReads, reference => reference == Reference(1));
        Assert.Single(source.RecordReads, reference => reference == Reference(2, 5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BaseDeletedOrReusedDuringExtensionReadDoesNotStopTheNextFile(bool reused)
    {
        var source = LinearSource();
        source.Add(1, Record(Basic(), Name("silinen.txt"), AttributeList((0x30, Reference(2)))), 5120);
        source.Add(3, Record(Basic(), Name("kalan.txt"), Resident(0x80, [1])), 7168);
        using var reader = new NtfsMftReader(source);
        using var entries = reader.ReadEntries().GetEnumerator();
        Assert.True(entries.MoveNext());
        if (reused)
        {
            var replacement = Record(Basic(), Name("yeni-kimlik.txt"));
            Write16(replacement, 16, 2);
            source.Add(1, replacement, 5120);
        }
        else source.RemoveKernelRecord(1);

        Assert.True(entries.MoveNext());
        Assert.Equal("kalan.txt", entries.Current.Name);
        Assert.False(entries.MoveNext());
        Assert.Single(source.RecordReads, reference => reference == Reference(1));
    }

    [Fact]
    public void AStableBaseWithAMissingExtensionCannotBeSilentlyDropped()
    {
        var source = LinearSource();
        source.Add(1, Record(Basic(), Name("tutarsiz.txt"), AttributeList((0x30, Reference(2)))), 5120);
        using var reader = new NtfsMftReader(source);
        Assert.Throws<InvalidDataException>(() => reader.ReadEntries().ToArray());
        Assert.Single(source.RecordReads, reference => reference == Reference(1));
        Assert.Equal(2, source.RecordReads.Count(reference => reference == Reference(2)));
    }

    [Fact]
    public void RefreshedBaseCannotBecomeAnExtensionOfAnotherFile()
    {
        var source = LinearSource();
        source.Add(1, Record(Basic(), Name("eski.txt"), AttributeList((0x30, Reference(2)))), 5120);
        using var reader = new NtfsMftReader(source);
        using var entries = reader.ReadEntries().GetEnumerator();
        Assert.True(entries.MoveNext());
        source.Add(1, Record(false, Reference(3), Name("baska.txt")), 5120);
        Assert.Throws<InvalidDataException>(() => entries.MoveNext());
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

    [Fact]
    public void RecordWhoseExtensionSegmentWasFreedMidScanIsSkippedNotFatal()
    {
        var source = LinearSource();
        source.Add(1, Record(Basic(), Name("kalan.txt"), Resident(0x80, [1, 2, 3])), 5120);
        source.Add(2, Record(Basic(), Name("silinen.txt"), Resident(0x80, [4]),
            AttributeList((0x30, Reference(2)), (0x30, Reference(3)))), 6144);
        source.RemoveKernelRecord(2);
        using var reader = new NtfsMftReader(source);

        var entries = reader.ReadEntries().ToArray();

        Assert.Equal(new[] { "$MFT", "kalan.txt" }, entries.Select(entry => entry.Name));
    }

    private static FakeNtfsVolumeSource LinearSource()
    {
        var source = new FakeNtfsVolumeSource();
        source.Add(0, Record(Basic(), Name("$MFT"),
            Nonresident(0x80, 4096, 0, 7, 0x11, 8, 8, 0)), 4096);
        return source;
    }
}
