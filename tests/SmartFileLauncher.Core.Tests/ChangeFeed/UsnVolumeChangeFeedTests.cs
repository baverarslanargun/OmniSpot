using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class UsnVolumeChangeFeedTests
{
    private const string FirstRootPath = @"C:\Kok";
    private const string SecondRootPath = @"C:\Diger";
    private const ulong VolumeSerialNumber = 0x1234;
    private const ulong FirstRootReference = 1;
    private const ulong SecondRootReference = 2;
    private const ulong JournalId = 7;
    private const long StartUsn = 100;
    private const long EndUsn = 200;

    [Fact]
    public void Read_ReadsTheJournalOnceAndProjectsEveryRoot()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 50, FirstRootReference, UsnReason.FileCreate, "ilk.txt")
                .AddVersion2(111, 51, SecondRootReference, UsnReason.FileCreate, "ikinci.txt")
                .Build());
        using var feed = CreateFeed(reader);

        var batch = feed.Read();

        Assert.Equal((StartUsn, JournalId), Assert.Single(reader.ReadCalls));
        Assert.Equal(2, batch.Roots.Count);
        Assert.Equal(
            @"C:\Kok\ilk.txt",
            Assert.Single(batch.Roots[0].Batch.Events).FullPath);
        Assert.Equal(
            @"C:\Diger\ikinci.txt",
            Assert.Single(batch.Roots[1].Batch.Events).FullPath);
    }

    [Fact]
    public void Read_GapsOnlyTheRootThatDisappeared()
    {
        var probe = CreateProbe();
        probe.Remove(SecondRootPath);
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 50, FirstRootReference, UsnReason.FileCreate, "ilk.txt")
                .Build());
        using var feed = CreateFeed(reader, probe);

        var batch = feed.Read();

        Assert.Equal(ChangeFeedStatus.Ok, batch.Roots[0].Batch.Status);
        Assert.Equal(ChangeFeedGapReason.RootUnavailable, batch.Roots[1].Batch.GapReason);
        Assert.Single(reader.ReadCalls);
    }

    [Fact]
    public void Read_SkipsTheJournalWhenNoRootIsUsable()
    {
        var reader = CreateReader();
        using var feed = CreateFeed(reader, new FakeUsnIdentityProbe());

        var batch = feed.Read();

        Assert.Empty(reader.ReadCalls);
        Assert.All(
            batch.Roots,
            root => Assert.Equal(ChangeFeedGapReason.RootUnavailable, root.Batch.GapReason));
    }

    [Fact]
    public void Read_StampsAJournalGapOnEveryRoot()
    {
        var reader = CreateReader();
        reader.Descriptor = new UsnJournalDescriptor(99, 50, EndUsn, 0, long.MaxValue, 0, 0);
        using var feed = CreateFeed(reader);

        var batch = feed.Read();

        Assert.All(
            batch.Roots,
            root => Assert.Equal(ChangeFeedGapReason.JournalIdChanged, root.Batch.GapReason));
    }

    [Fact]
    public void Accept_AdvancesOneCursorAndCommitsEveryRootMap()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 50, FirstRootReference, UsnReason.FileCreate, "alt", FileAttributes.Directory)
                .AddVersion2(111, 51, SecondRootReference, UsnReason.FileCreate, "yan", FileAttributes.Directory)
                .Build());
        using var feed = CreateFeed(reader);

        feed.Read();
        Assert.Equal(StartUsn, feed.AcceptedUsn);

        feed.Accept();

        Assert.Equal(EndUsn, feed.AcceptedUsn);
        Assert.Equal("alt", Assert.Single(CaptureDirectories(feed, 0)).Name);
        Assert.Equal("yan", Assert.Single(CaptureDirectories(feed, 1)).Name);
    }

    [Fact]
    public void Accept_AdvancesWhenAtLeastOneRootProjected()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, 50, FirstRootReference, UsnReason.FileCreate, "alt", FileAttributes.Directory)
                .AddVersion2(111, SecondRootReference, 999, UsnReason.RenameNewName, "Tasindi", FileAttributes.Directory)
                .Build());
        using var feed = CreateFeed(reader);

        var batch = feed.Read();

        Assert.Equal(ChangeFeedStatus.Ok, batch.Roots[0].Batch.Status);
        Assert.Equal(ChangeFeedGapReason.RootIdentityChanged, batch.Roots[1].Batch.GapReason);

        feed.Accept();

        Assert.Equal(EndUsn, feed.AcceptedUsn);
        Assert.Equal("alt", Assert.Single(CaptureDirectories(feed, 0)).Name);
        Assert.Empty(CaptureDirectories(feed, 1));
    }

    [Fact]
    public void Accept_KeepsThePositionWhenEveryRootProjectionGapped()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, FirstRootReference, 999, UsnReason.RenameNewName, "IlkTasindi", FileAttributes.Directory)
                .AddVersion2(111, SecondRootReference, 999, UsnReason.RenameNewName, "Tasindi", FileAttributes.Directory)
                .Build());
        using var feed = CreateFeed(reader);

        var batch = feed.Read();

        Assert.All(
            batch.Roots,
            root => Assert.Equal(ChangeFeedGapReason.RootIdentityChanged, root.Batch.GapReason));
        Assert.Equal(StartUsn, batch.NextUsn);

        feed.Accept();

        Assert.Equal(StartUsn, feed.AcceptedUsn);
    }

    [Fact]
    public void Accept_KeepsThePositionWhenNoRootCouldRead()
    {
        var reader = CreateReader();
        using var feed = CreateFeed(reader, new FakeUsnIdentityProbe());

        feed.Read();
        feed.Accept();

        Assert.Equal(StartUsn, feed.AcceptedUsn);
    }

    [Fact]
    public void Create_StartsFromTheOldestRootCursor()
    {
        var reader = CreateReader().EnqueuePage(EndUsn, Array.Empty<byte>());
        using var feed = UsnVolumeChangeFeed.Create(
            reader,
            new[]
            {
                State(FirstRootPath, FirstRootReference, nextUsn: 150),
                State(SecondRootPath, SecondRootReference, nextUsn: StartUsn)
            },
            CreateProbe(),
            new FakeUsnSubtreeReader());

        Assert.Equal(StartUsn, feed.AcceptedUsn);

        feed.Read();

        Assert.Equal(StartUsn, Assert.Single(reader.ReadCalls).StartUsn);
    }

    [Fact]
    public void Create_RejectsRootsFromAnotherVolume()
    {
        var states = new[]
        {
            State(FirstRootPath, FirstRootReference),
            State(SecondRootPath, SecondRootReference, volumeSerialNumber: 0x9999)
        };

        Assert.Throws<ArgumentException>(
            () => UsnVolumeChangeFeed.Create(
                CreateReader(),
                states,
                CreateProbe(),
                new FakeUsnSubtreeReader()));
    }

    [Fact]
    public void Create_RejectsRootsRecordedAgainstDifferentJournals()
    {
        var states = new[]
        {
            State(FirstRootPath, FirstRootReference),
            State(SecondRootPath, SecondRootReference, journalId: 9)
        };

        Assert.Throws<ArgumentException>(
            () => UsnVolumeChangeFeed.Create(
                CreateReader(),
                states,
                CreateProbe(),
                new FakeUsnSubtreeReader()));
    }

    private static IReadOnlyList<UsnDirectoryEntry> CaptureDirectories(
        UsnVolumeChangeFeed feed,
        int rootIndex) =>
        feed.Roots[rootIndex].CaptureState(feed.JournalId, feed.AcceptedUsn).Directories;

    private static UsnJournalDescriptor Descriptor() =>
        new(JournalId, 50, EndUsn, 0, long.MaxValue, 0, 0);

    private static FakeUsnJournalReader CreateReader() =>
        new() { Descriptor = Descriptor() };

    private static FakeUsnIdentityProbe CreateProbe() =>
        new FakeUsnIdentityProbe()
            .Set(FirstRootPath, VolumeSerialNumber, FirstRootReference)
            .Set(SecondRootPath, VolumeSerialNumber, SecondRootReference);

    private static UsnChangeFeedState State(
        string rootPath,
        ulong rootReference,
        long nextUsn = StartUsn,
        ulong journalId = JournalId,
        ulong volumeSerialNumber = VolumeSerialNumber) =>
        new(
            rootPath,
            new UsnNodeIdentity(volumeSerialNumber, UsnFileReference.FromNtfs(rootReference)),
            journalId,
            nextUsn,
            Array.Empty<UsnDirectoryEntry>());

    private static UsnVolumeChangeFeed CreateFeed(
        FakeUsnJournalReader reader,
        FakeUsnIdentityProbe? identityProbe = null)
    {
        var probe = identityProbe ?? CreateProbe();
        var subtrees = new FakeUsnSubtreeReader();

        return new UsnVolumeChangeFeed(
            reader,
            JournalId,
            StartUsn,
            new[]
            {
                new UsnRootProjection(State(FirstRootPath, FirstRootReference), probe, subtrees),
                new UsnRootProjection(State(SecondRootPath, SecondRootReference), probe, subtrees)
            });
    }
}
