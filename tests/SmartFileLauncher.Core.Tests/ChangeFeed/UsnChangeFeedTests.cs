using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class UsnChangeFeedTests
{
    private const string RootPath = @"C:\Kok";
    private const ulong VolumeSerialNumber = 0x1234;
    private const ulong RootReference = 1;
    private const ulong JournalId = 7;
    private const long StartUsn = 100;
    private const long EndUsn = 200;

    [Fact]
    public void Read_ProjectsCreateInsideRoot()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 50, RootReference, UsnReason.FileCreate, "rapor.txt")
                .Build());
        using var feed = CreateFeed(reader);

        var batch = feed.Read();

        Assert.Equal(ChangeFeedStatus.Ok, batch.Status);
        var change = Assert.Single(batch.Events);
        Assert.Equal(ChangeFeedEventKind.Created, change.Kind);
        Assert.Equal(@"C:\Kok\rapor.txt", change.FullPath);
        Assert.False(change.IsDirectory);
        Assert.Equal((StartUsn, JournalId), Assert.Single(reader.ReadCalls));
    }

    [Fact]
    public void Read_IgnoresRecordsOutsideRoot()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 50, 999, UsnReason.FileCreate, "gizli.txt")
                .Build());
        using var feed = CreateFeed(reader);

        var batch = feed.Read();

        Assert.Equal(ChangeFeedStatus.Ok, batch.Status);
        Assert.Empty(batch.Events);
    }

    [Fact]
    public void Read_StaysReplayableUntilAccept()
    {
        var records = new UsnRecordBuffer()
            .AddVersion2(110, 50, RootReference, UsnReason.FileCreate, "rapor.txt")
            .Build();
        var reader = CreateReader()
            .EnqueuePage(EndUsn, records)
            .EnqueuePage(EndUsn, records);
        using var feed = CreateFeed(reader);

        var first = feed.Read();
        var replay = feed.Read();

        Assert.Equal(
            first.Events.Select(change => change.ToString()),
            replay.Events.Select(change => change.ToString()));
        Assert.Equal(StartUsn, feed.AcceptedUsn);

        feed.Accept();
        Assert.Equal(EndUsn, feed.AcceptedUsn);

        var afterAccept = feed.Read();
        Assert.Empty(afterAccept.Events);
    }

    [Fact]
    public void Read_DoesNotDeliverRecordsAtOrAfterQueriedEnd()
    {
        var reader = CreateReader().EnqueuePage(
            260,
            new UsnRecordBuffer()
                .AddVersion2(110, 50, RootReference, UsnReason.FileCreate, "ilk.txt")
                .AddVersion2(EndUsn, 51, RootReference, UsnReason.FileCreate, "sinirda.txt")
                .AddVersion2(210, 52, RootReference, UsnReason.FileCreate, "sonraki.txt")
                .Build());
        using var feed = CreateFeed(reader);

        var batch = feed.Read();

        Assert.Equal(@"C:\Kok\ilk.txt", Assert.Single(batch.Events).FullPath);

        feed.Accept();
        Assert.Equal(EndUsn, feed.AcceptedUsn);

        reader.Descriptor = Descriptor(nextUsn: 300);
        reader.EnqueuePage(
            300,
            new UsnRecordBuffer()
                .AddVersion2(EndUsn, 51, RootReference, UsnReason.FileCreate, "sinirda.txt")
                .AddVersion2(210, 52, RootReference, UsnReason.FileCreate, "sonraki.txt")
                .Build());

        var next = feed.Read();

        Assert.Equal(
            new[] { @"C:\Kok\sinirda.txt", @"C:\Kok\sonraki.txt" },
            next.Events.Select(change => change.FullPath));
    }

    [Fact]
    public void Read_PairsRenameRecordsIntoOneEvent()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 50, RootReference, UsnReason.RenameOldName, "eski.txt")
                .AddVersion2(111, 50, RootReference, UsnReason.RenameNewName, "yeni.txt")
                .AddVersion2(112, 50, RootReference, UsnReason.RenameNewName | UsnReason.Close, "yeni.txt")
                .Build());
        using var feed = CreateFeed(reader);

        var change = Assert.Single(feed.Read().Events);

        Assert.Equal(ChangeFeedEventKind.Renamed, change.Kind);
        Assert.Equal(@"C:\Kok\eski.txt", change.OldPath);
        Assert.Equal(@"C:\Kok\yeni.txt", change.FullPath);
    }

    [Fact]
    public void Read_ReportsDirectoryMovedIntoRootAsCreated()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 60, 999, UsnReason.RenameOldName, "gelen", FileAttributes.Directory)
                .AddVersion2(111, 60, RootReference, UsnReason.RenameNewName, "gelen", FileAttributes.Directory)
                .Build());
        using var feed = CreateFeed(reader);

        var change = Assert.Single(feed.Read().Events);

        Assert.Equal(ChangeFeedEventKind.Created, change.Kind);
        Assert.Equal(@"C:\Kok\gelen", change.FullPath);
        Assert.True(change.IsDirectory);
    }

    [Fact]
    public void Read_ReportsDirectoryMovedOutOfRootAsDeleted()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 2, RootReference, UsnReason.RenameOldName, "alt", FileAttributes.Directory)
                .AddVersion2(111, 2, 999, UsnReason.RenameNewName, "alt", FileAttributes.Directory)
                .Build());
        using var feed = CreateFeed(reader, WithDirectory("alt", 2, RootReference));

        var change = Assert.Single(feed.Read().Events);

        Assert.Equal(ChangeFeedEventKind.Deleted, change.Kind);
        Assert.Equal(@"C:\Kok\alt", change.FullPath);
        Assert.True(change.IsDirectory);

        feed.Accept();
        Assert.Empty(feed.CaptureState().Directories);
    }

    [Fact]
    public void Read_ResolvesFileCreatedInDirectoryCreatedByTheSameBatch()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 2, RootReference, UsnReason.FileCreate, "alt", FileAttributes.Directory)
                .AddVersion2(111, 50, 2, UsnReason.FileCreate, "ic.txt")
                .Build());
        using var feed = CreateFeed(reader);

        var batch = feed.Read();

        Assert.Equal(
            new[] { @"C:\Kok\alt", @"C:\Kok\alt\ic.txt" },
            batch.Events.Select(change => change.FullPath));
    }

    [Fact]
    public void Read_KeepsDeletedDirectoryResolvableForTheRestOfTheBatch()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 2, RootReference, UsnReason.FileDelete, "alt", FileAttributes.Directory)
                .AddVersion2(111, 50, 2, UsnReason.FileDelete, "ic.txt")
                .Build());
        using var feed = CreateFeed(reader, WithDirectory("alt", 2, RootReference));

        var batch = feed.Read();

        Assert.Equal(
            new[] { @"C:\Kok\alt", @"C:\Kok\alt\ic.txt" },
            batch.Events.Select(change => change.FullPath));
        Assert.All(batch.Events, change => Assert.Equal(ChangeFeedEventKind.Deleted, change.Kind));
    }

    [Fact]
    public void Accept_CommitsDirectoryMapWithCursor()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 2, RootReference, UsnReason.FileCreate, "alt", FileAttributes.Directory)
                .Build());
        using var feed = CreateFeed(reader);

        feed.Read();
        Assert.Empty(feed.CaptureState().Directories);

        feed.Accept();

        var entry = Assert.Single(feed.CaptureState().Directories);
        Assert.Equal("alt", entry.Name);
        Assert.Equal(UsnFileReference.FromNtfs(RootReference), entry.ParentReference);
        Assert.Equal(EndUsn, feed.CaptureState().NextUsn);
    }

    [Fact]
    public void Read_ReportsModifyForDataChange()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 50, RootReference, UsnReason.DataExtend, "rapor.txt")
                .AddVersion2(111, 50, RootReference, UsnReason.DataExtend | UsnReason.Close, "rapor.txt")
                .Build());
        using var feed = CreateFeed(reader);

        var change = Assert.Single(feed.Read().Events);

        Assert.Equal(ChangeFeedEventKind.Modified, change.Kind);
        Assert.Equal(@"C:\Kok\rapor.txt", change.FullPath);
    }

    [Fact]
    public void Read_IgnoresReasonsThatDoNotChangeTheIndex()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 50, RootReference, UsnReason.SecurityChange | UsnReason.Close, "rapor.txt")
                .Build());
        using var feed = CreateFeed(reader);

        Assert.Empty(feed.Read().Events);
    }

    [Fact]
    public void Read_CollapsesCreateAndDeleteOfTemporaryFile()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 50, RootReference, UsnReason.FileCreate, "gecici.tmp")
                .AddVersion2(111, 50, RootReference, UsnReason.FileDelete | UsnReason.Close, "gecici.tmp")
                .Build());
        using var feed = CreateFeed(reader);

        var change = Assert.Single(feed.Read().Events);

        Assert.Equal(ChangeFeedEventKind.Deleted, change.Kind);
        Assert.Equal(@"C:\Kok\gecici.tmp", change.FullPath);
    }

    [Theory]
    [InlineData((ulong)9, StartUsn, ChangeFeedGapReason.JournalIdChanged)]
    [InlineData(JournalId, 40L, ChangeFeedGapReason.CursorOutsideJournal)]
    [InlineData(JournalId, 900L, ChangeFeedGapReason.CursorOutsideJournal)]
    public void Read_ReportsGapWhenJournalPositionIsUnusable(
        ulong journalId,
        long nextUsn,
        ChangeFeedGapReason expected)
    {
        var reader = CreateReader();
        reader.Descriptor = new UsnJournalDescriptor(journalId, 50, 800, 0, long.MaxValue, 0, 0);
        using var feed = CreateFeed(reader, startUsn: nextUsn);

        var batch = feed.Read();

        Assert.Equal(ChangeFeedStatus.Gap, batch.Status);
        Assert.Equal(expected, batch.GapReason);
        Assert.Empty(batch.Events);
    }

    [Fact]
    public void Read_ReportsGapWhenJournalCannotBeQueried()
    {
        var reader = CreateReader();
        reader.QueryFails = true;
        using var feed = CreateFeed(reader);

        Assert.Equal(ChangeFeedGapReason.JournalUnavailable, feed.Read().GapReason);
    }

    [Fact]
    public void Read_ReportsGapWhenRootIsMissing()
    {
        var probe = CreateProbe();
        probe.Remove(RootPath);
        using var feed = CreateFeed(CreateReader(), identityProbe: probe);

        Assert.Equal(ChangeFeedGapReason.RootUnavailable, feed.Read().GapReason);
    }

    [Fact]
    public void Read_ReportsGapWhenRootPathNowResolvesElsewhere()
    {
        var probe = CreateProbe().Set(RootPath, VolumeSerialNumber, 42);
        using var feed = CreateFeed(CreateReader(), identityProbe: probe);

        Assert.Equal(ChangeFeedGapReason.RootIdentityChanged, feed.Read().GapReason);
    }

    [Fact]
    public void Read_ReportsGapWhenRootItselfIsRenamed()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, RootReference, 999, UsnReason.RenameNewName, "YeniKok", FileAttributes.Directory)
                .Build());
        using var feed = CreateFeed(reader);

        var batch = feed.Read();

        Assert.Equal(ChangeFeedGapReason.RootIdentityChanged, batch.GapReason);
        Assert.Empty(batch.Events);
    }

    [Fact]
    public void Read_ReportsGapWhenJournalBufferIsMalformed()
    {
        var records = new UsnRecordBuffer()
            .AddVersion2(110, 50, RootReference, UsnReason.FileCreate, "rapor.txt")
            .Build();
        BitConverter.TryWriteBytes(records.AsSpan(4), (ushort)9);

        var reader = CreateReader().EnqueuePage(EndUsn, records);
        using var feed = CreateFeed(reader);

        Assert.Equal(ChangeFeedGapReason.FeedStateInvalid, feed.Read().GapReason);
    }

    [Fact]
    public void Accept_AfterGapKeepsPreviousPosition()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 50, RootReference, UsnReason.FileCreate, "rapor.txt")
                .Build());
        using var feed = CreateFeed(reader);

        Assert.Equal(ChangeFeedStatus.Ok, feed.Read().Status);

        reader.Descriptor = Descriptor(journalId: 99);
        Assert.Equal(ChangeFeedGapReason.JournalIdChanged, feed.Read().GapReason);

        feed.Accept();

        Assert.Equal(StartUsn, feed.AcceptedUsn);
    }

    [Fact]
    public void Read_ThrowsAfterDispose()
    {
        var feed = CreateFeed(CreateReader());
        feed.Dispose();

        Assert.Throws<ObjectDisposedException>(() => feed.Read());
    }

    [Fact]
    public void Dispose_LeavesBorrowedReaderOpenButClosesOwnedReader()
    {
        var borrowed = CreateReader();
        CreateFeed(borrowed).Dispose();
        Assert.False(borrowed.Disposed);

        var owned = CreateReader();
        CreateFeed(owned, ownsJournalReader: true).Dispose();
        Assert.True(owned.Disposed);
    }

    [Fact]
    public void CaptureState_RoundTripsThroughANewFeed()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 2, RootReference, UsnReason.FileCreate, "alt", FileAttributes.Directory)
                .Build());
        using var feed = CreateFeed(reader);
        feed.Read();
        feed.Accept();

        var resumedReader = CreateReader();
        resumedReader.Descriptor = Descriptor(nextUsn: 300);
        resumedReader.EnqueuePage(
            300,
            new UsnRecordBuffer()
                .AddVersion2(210, 50, 2, UsnReason.FileCreate, "ic.txt")
                .Build());

        using var resumed = new UsnChangeFeed(
            feed.CaptureState(),
            resumedReader,
            CreateProbe(),
            new FakeUsnSubtreeReader());

        var batch = resumed.Read();

        Assert.Equal(EndUsn, Assert.Single(resumedReader.ReadCalls).StartUsn);
        Assert.Equal(@"C:\Kok\alt\ic.txt", Assert.Single(batch.Events).FullPath);
    }

    [Fact]
    public void ProviderMetadataDescribesTheRoot()
    {
        using var feed = CreateFeed(CreateReader());

        Assert.Equal(UsnChangeFeed.ProviderIdentifier, feed.ProviderId);
        Assert.Equal(RootPath, feed.RootPath);
        Assert.False(feed.RootIdentity.IsUnknown);
    }

    [Fact]
    public void Accept_AfterAProjectionGapKeepsPreviousPosition()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 2, RootReference, UsnReason.FileCreate, "alt", FileAttributes.Directory)
                .AddVersion2(111, RootReference, 999, UsnReason.RenameNewName, "YeniKok", FileAttributes.Directory)
                .Build());
        using var feed = CreateFeed(reader);

        Assert.Equal(ChangeFeedGapReason.RootIdentityChanged, feed.Read().GapReason);

        feed.Accept();

        Assert.Equal(StartUsn, feed.AcceptedUsn);
        Assert.Empty(feed.CaptureState().Directories);
    }

    [Fact]
    public void Read_FaultsWhenTheNativeCallIsRejectedWithAnUnchangedJournal()
    {
        var reader = CreateReader();
        reader.ReadRejectsProtocol = true;
        using var feed = CreateFeed(reader);

        var batch = feed.Read();

        Assert.Equal(ChangeFeedStatus.Faulted, batch.Status);
        Assert.Equal(ChangeFeedFaultReason.NativeProtocolRejected, batch.FaultReason);
        Assert.Equal(ChangeFeedGapReason.None, batch.GapReason);
        Assert.False(batch.HasGap);
        Assert.False(string.IsNullOrWhiteSpace(batch.Diagnostics));
        Assert.True(feed.IsFaulted);
    }

    [Fact]
    public void Read_TurnsARejectionIntoAGapWhenTheJournalIdentityChanged()
    {
        var reader = CreateReader();
        reader.ReadRejectsProtocol = true;
        reader.OnReadPage = () => reader.Descriptor = Descriptor(journalId: 99);
        using var feed = CreateFeed(reader);

        var batch = feed.Read();

        Assert.Equal(ChangeFeedStatus.Gap, batch.Status);
        Assert.Equal(ChangeFeedGapReason.JournalIdChanged, batch.GapReason);
        Assert.False(feed.IsFaulted);
    }

    [Fact]
    public void Read_TurnsARejectionIntoAGapWhenTheRetainedRangeLeftTheCursorBehind()
    {
        var reader = CreateReader();
        reader.ReadRejectsProtocol = true;
        reader.OnReadPage = () =>
            reader.Descriptor = new UsnJournalDescriptor(JournalId, 500, 800, 0, long.MaxValue, 0, 0);
        using var feed = CreateFeed(reader);

        var batch = feed.Read();

        Assert.Equal(ChangeFeedGapReason.CursorOutsideJournal, batch.GapReason);
        Assert.False(feed.IsFaulted);
    }

    [Fact]
    public void Read_StopsTouchingTheVolumeAfterAFault()
    {
        var reader = CreateReader();
        reader.ReadRejectsProtocol = true;
        using var feed = CreateFeed(reader);

        Assert.True(feed.Read().IsFaulted);
        var queries = reader.QueryCalls;
        var reads = reader.ReadCalls.Count;

        Assert.True(feed.Read().IsFaulted);

        Assert.Equal(queries, reader.QueryCalls);
        Assert.Equal(reads, reader.ReadCalls.Count);
    }

    [Fact]
    public void Accept_AfterFaultKeepsPreviousPosition()
    {
        var reader = CreateReader();
        reader.QueryRejectsProtocol = true;
        using var feed = CreateFeed(reader);

        Assert.True(feed.Read().IsFaulted);
        feed.Accept();

        Assert.Equal(StartUsn, feed.AcceptedUsn);
        Assert.Empty(reader.ReadCalls);
    }

    private static UsnJournalDescriptor Descriptor(
        ulong journalId = JournalId,
        long nextUsn = EndUsn) =>
        new(journalId, 50, nextUsn, 0, long.MaxValue, 0, 0);

    private static FakeUsnJournalReader CreateReader() =>
        new() { Descriptor = Descriptor() };

    private static FakeUsnIdentityProbe CreateProbe() =>
        new FakeUsnIdentityProbe().Set(RootPath, VolumeSerialNumber, RootReference);

    private static UsnDirectoryEntry[] WithDirectory(
        string name,
        ulong reference,
        ulong parentReference) =>
        new[]
        {
            new UsnDirectoryEntry(
                UsnFileReference.FromNtfs(reference),
                name,
                UsnFileReference.FromNtfs(parentReference))
        };

    private static UsnChangeFeed CreateFeed(
        FakeUsnJournalReader reader,
        UsnDirectoryEntry[]? directories = null,
        FakeUsnIdentityProbe? identityProbe = null,
        long startUsn = StartUsn,
        IUsnSubtreeReader? subtreeReader = null,
        bool ownsJournalReader = false)
    {
        var state = new UsnChangeFeedState(
            RootPath,
            new UsnNodeIdentity(VolumeSerialNumber, UsnFileReference.FromNtfs(RootReference)),
            JournalId,
            startUsn,
            directories ?? Array.Empty<UsnDirectoryEntry>());

        return new UsnChangeFeed(
            state,
            reader,
            identityProbe ?? CreateProbe(),
            subtreeReader ?? new FakeUsnSubtreeReader(),
            ownsJournalReader);
    }
}
