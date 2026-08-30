using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

/// <summary>
/// A directory moved into the root renames only itself; its descendants stay
/// silent in the journal. These tests pin that the feed learns them anyway, so
/// changes under a moved-in subtree keep producing events after the accept.
/// </summary>
public sealed class UsnChangeFeedSubtreeTests
{
    private const string RootPath = @"C:\Kok";
    private const ulong VolumeSerialNumber = 0x1234;
    private const ulong RootReference = 1;
    private const ulong JournalId = 7;
    private const long StartUsn = 100;
    private const long EndUsn = 200;

    private const ulong MovedReference = 60;
    private const ulong NestedReference = 61;
    private const ulong DeeperReference = 62;

    [Fact]
    public void MoveInThenAccept_KeepsResolvingChangesInsideTheMovedSubtree()
    {
        var subtrees = new FakeUsnSubtreeReader().Add(
            @"C:\Kok\gelen",
            skippedDirectoryCount: 0,
            Entry(NestedReference, "alt", MovedReference),
            Entry(DeeperReference, "derin", NestedReference));

        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, MovedReference, 999, UsnReason.RenameOldName, "gelen", FileAttributes.Directory)
                .AddVersion2(111, MovedReference, RootReference, UsnReason.RenameNewName, "gelen", FileAttributes.Directory)
                .Build());
        using var feed = CreateFeed(reader, subtreeReader: subtrees);

        var moveIn = feed.Read();

        Assert.Equal(@"C:\Kok\gelen", Assert.Single(moveIn.Events).FullPath);
        Assert.Equal(@"C:\Kok\gelen", Assert.Single(subtrees.Requests));

        feed.Accept();

        reader.Descriptor = Descriptor(nextUsn: 300);
        reader.EnqueuePage(
            300,
            new UsnRecordBuffer()
                .AddVersion2(210, 70, DeeperReference, UsnReason.DataExtend, "rapor.txt")
                .Build());

        var change = Assert.Single(feed.Read().Events);

        Assert.Equal(ChangeFeedEventKind.Modified, change.Kind);
        Assert.Equal(@"C:\Kok\gelen\alt\derin\rapor.txt", change.FullPath);
    }

    [Fact]
    public void MoveIn_LearnsTheSubtreeOnlyAfterAccept()
    {
        var subtrees = new FakeUsnSubtreeReader().Add(
            @"C:\Kok\gelen",
            skippedDirectoryCount: 0,
            Entry(NestedReference, "alt", MovedReference));

        using var feed = CreateFeed(CreateMoveInReader(), subtreeReader: subtrees);

        feed.Read();
        Assert.Empty(feed.CaptureState().Directories);

        feed.Accept();

        Assert.Equal(
            new[] { "alt", "gelen" },
            feed.CaptureState().Directories.Select(entry => entry.Name).Order());
    }

    [Fact]
    public void MoveIn_ResolvesSubtreeForLaterRecordsInTheSameBatch()
    {
        var subtrees = new FakeUsnSubtreeReader().Add(
            @"C:\Kok\gelen",
            skippedDirectoryCount: 0,
            Entry(NestedReference, "alt", MovedReference));

        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, MovedReference, 999, UsnReason.RenameOldName, "gelen", FileAttributes.Directory)
                .AddVersion2(111, MovedReference, RootReference, UsnReason.RenameNewName, "gelen", FileAttributes.Directory)
                .AddVersion2(112, 70, NestedReference, UsnReason.FileCreate, "yeni.txt")
                .Build());
        using var feed = CreateFeed(reader, subtreeReader: subtrees);

        var batch = feed.Read();

        Assert.Equal(
            new[] { @"C:\Kok\gelen", @"C:\Kok\gelen\alt\yeni.txt" },
            batch.Events.Select(change => change.FullPath));
    }

    [Fact]
    public void RenameInsideRoot_DoesNotRewalkAnAlreadyKnownDirectory()
    {
        var subtrees = new FakeUsnSubtreeReader();
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, MovedReference, RootReference, UsnReason.RenameOldName, "eski", FileAttributes.Directory)
                .AddVersion2(111, MovedReference, RootReference, UsnReason.RenameNewName, "yeni", FileAttributes.Directory)
                .Build());
        using var feed = CreateFeed(
            reader,
            directories: new[] { Entry(MovedReference, "eski", RootReference) },
            subtreeReader: subtrees);

        var change = Assert.Single(feed.Read().Events);

        Assert.Equal(ChangeFeedEventKind.Renamed, change.Kind);
        Assert.Empty(subtrees.Requests);
    }

    [Fact]
    public void FreshDirectoryCreate_DoesNotTriggerASubtreeWalk()
    {
        var subtrees = new FakeUsnSubtreeReader();
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, MovedReference, RootReference, UsnReason.FileCreate, "yeni", FileAttributes.Directory)
                .Build());
        using var feed = CreateFeed(reader, subtreeReader: subtrees);

        feed.Read();

        Assert.Empty(subtrees.Requests);
    }

    [Fact]
    public void MoveIn_ReportsDirectoriesTheWalkCouldNotRead()
    {
        var subtrees = new FakeUsnSubtreeReader().Add(
            @"C:\Kok\gelen",
            skippedDirectoryCount: 2,
            Entry(NestedReference, "alt", MovedReference));

        using var feed = CreateFeed(CreateMoveInReader(), subtreeReader: subtrees);

        var batch = feed.Read();

        Assert.Equal(ChangeFeedStatus.Ok, batch.Status);
        Assert.Equal(2, feed.LastSkippedSubtreeDirectoryCount);
    }

    [Fact]
    public void MoveOut_DropsTheWholeSubtreeFromThePersistedMap()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, MovedReference, RootReference, UsnReason.RenameOldName, "gelen", FileAttributes.Directory)
                .AddVersion2(111, MovedReference, 999, UsnReason.RenameNewName, "gelen", FileAttributes.Directory)
                .Build());
        using var feed = CreateFeed(
            reader,
            directories: new[]
            {
                Entry(MovedReference, "gelen", RootReference),
                Entry(NestedReference, "alt", MovedReference),
                Entry(DeeperReference, "derin", NestedReference)
            });

        var change = Assert.Single(feed.Read().Events);
        Assert.Equal(ChangeFeedEventKind.Deleted, change.Kind);

        feed.Accept();

        Assert.Empty(feed.CaptureState().Directories);
    }

    [Fact]
    public void DirectoryDelete_DropsDescendantsThatHadNoDeleteRecord()
    {
        var reader = CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, MovedReference, RootReference, UsnReason.FileDelete, "gelen", FileAttributes.Directory)
                .Build());
        using var feed = CreateFeed(
            reader,
            directories: new[]
            {
                Entry(MovedReference, "gelen", RootReference),
                Entry(NestedReference, "alt", MovedReference)
            });

        feed.Read();
        feed.Accept();

        Assert.Empty(feed.CaptureState().Directories);
    }

    private static UsnDirectoryEntry Entry(ulong reference, string name, ulong parentReference) =>
        new(
            UsnFileReference.FromNtfs(reference),
            name,
            UsnFileReference.FromNtfs(parentReference));

    private static UsnJournalDescriptor Descriptor(
        ulong journalId = JournalId,
        long nextUsn = EndUsn) =>
        new(journalId, 50, nextUsn, 0, long.MaxValue, 0, 0);

    private static FakeUsnJournalReader CreateReader() =>
        new() { Descriptor = Descriptor() };

    private static FakeUsnJournalReader CreateMoveInReader() =>
        CreateReader().EnqueuePage(
            EndUsn,
            new UsnRecordBuffer()
                .AddVersion2(110, MovedReference, 999, UsnReason.RenameOldName, "gelen", FileAttributes.Directory)
                .AddVersion2(111, MovedReference, RootReference, UsnReason.RenameNewName, "gelen", FileAttributes.Directory)
                .Build());

    private static UsnChangeFeed CreateFeed(
        FakeUsnJournalReader reader,
        UsnDirectoryEntry[]? directories = null,
        IUsnSubtreeReader? subtreeReader = null)
    {
        var state = new UsnChangeFeedState(
            RootPath,
            new UsnNodeIdentity(VolumeSerialNumber, UsnFileReference.FromNtfs(RootReference)),
            JournalId,
            StartUsn,
            directories ?? Array.Empty<UsnDirectoryEntry>());

        return new UsnChangeFeed(
            state,
            reader,
            new FakeUsnIdentityProbe().Set(RootPath, VolumeSerialNumber, RootReference),
            subtreeReader ?? new FakeUsnSubtreeReader());
    }
}
