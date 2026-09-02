using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class UsnChangeFeedStateStoreTests
{
    private const string FirstRoot = @"C:\Kok";
    private const string SecondRoot = @"C:\Diger";
    private const ulong VolumeSerialNumber = 0x1234;
    private const ulong JournalId = 7;
    private const long NextUsn = 4200;

    [Fact]
    public void Read_ReturnsNullBeforeAnythingIsWritten()
    {
        using var directory = new TemporaryDirectory();

        Assert.Null(CreateStore(directory).Read());
    }

    [Fact]
    public void Write_ThenRead_RoundTripsEveryRootAndDirectory()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);

        store.Write(
            JournalId,
            NextUsn,
            new[]
            {
                State(FirstRoot, 1, Entry(10, "alt", 1), Entry(11, "derin", 10)),
                State(SecondRoot, 2)
            });

        var restored = store.Read();

        Assert.NotNull(restored);
        Assert.Equal(JournalId, restored!.JournalId);
        Assert.Equal(NextUsn, restored.NextUsn);
        Assert.Equal(
            new[] { FirstRoot, SecondRoot },
            restored.Roots.Select(root => root.RootPath));
        Assert.Equal(
            new[] { "alt", "derin" },
            restored.Roots[0].Directories.Select(entry => entry.Name));
        Assert.Equal(
            UsnFileReference.FromNtfs(10),
            restored.Roots[0].Directories[1].ParentReference);
        Assert.Empty(restored.Roots[1].Directories);
    }

    [Fact]
    public void Write_PreservesTheHighHalfOfA128BitReference()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        var reference = new UsnFileReference(0xAAAA_BBBB_CCCC_DDDD, 0x1111_2222_3333_4444);

        store.Write(
            JournalId,
            NextUsn,
            new[]
            {
                new UsnChangeFeedState(
                    FirstRoot,
                    new UsnNodeIdentity(VolumeSerialNumber, reference),
                    JournalId,
                    NextUsn,
                    new[] { new UsnDirectoryEntry(reference, "alt", reference) })
            });

        var root = Assert.Single(store.Read()!.Roots);

        Assert.Equal(reference, root.RootIdentity.FileReference);
        Assert.Equal(reference, root.Directories[0].Reference);
        Assert.Equal(reference, root.Directories[0].ParentReference);
    }

    [Fact]
    public void Write_ReplacesThePreviousStateAndLeavesNoTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);

        store.Write(JournalId, 100, new[] { State(FirstRoot, 1) });
        store.Write(JournalId, 200, new[] { State(FirstRoot, 1) });

        Assert.Equal(200, store.Read()!.NextUsn);
        Assert.Equal(
            new[] { Path.GetFileName(store.FilePath) },
            Directory.GetFiles(directory.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Read_RejectsContentItCannotParse()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        File.WriteAllText(store.FilePath, "{ bozuk");

        Assert.Throws<InvalidDataException>(() => store.Read());
    }

    [Fact]
    public void Read_ProducesStateAVolumeFeedCanResumeFrom()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        store.Write(
            JournalId,
            NextUsn,
            new[] { State(FirstRoot, 1, Entry(10, "alt", 1)), State(SecondRoot, 2) });

        var restored = store.Read()!;
        using var feed = UsnVolumeChangeFeed.Create(
            new FakeUsnJournalReader { Descriptor = new UsnJournalDescriptor(JournalId, 50, NextUsn, 0, long.MaxValue, 0, 0) },
            restored.Roots,
            new FakeUsnIdentityProbe()
                .Set(FirstRoot, VolumeSerialNumber, 1)
                .Set(SecondRoot, VolumeSerialNumber, 2),
            new FakeUsnSubtreeReader());

        Assert.Equal(NextUsn, feed.AcceptedUsn);
        Assert.Equal(JournalId, feed.JournalId);
        Assert.All(
            feed.Read().Roots,
            root => Assert.Equal(ChangeFeedStatus.Ok, root.Batch.Status));
    }

    private static UsnChangeFeedStateStore CreateStore(TemporaryDirectory directory) =>
        new(Path.Combine(directory.Path, "state.json"));

    private static UsnChangeFeedState State(
        string rootPath,
        ulong rootReference,
        params UsnDirectoryEntry[] directories) =>
        new(
            rootPath,
            new UsnNodeIdentity(VolumeSerialNumber, UsnFileReference.FromNtfs(rootReference)),
            JournalId,
            NextUsn,
            directories);

    private static UsnDirectoryEntry Entry(ulong reference, string name, ulong parentReference) =>
        new(
            UsnFileReference.FromNtfs(reference),
            name,
            UsnFileReference.FromNtfs(parentReference));
}
