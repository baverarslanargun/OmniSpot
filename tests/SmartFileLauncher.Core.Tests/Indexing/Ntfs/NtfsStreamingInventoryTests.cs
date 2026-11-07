using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Indexing.Ntfs;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Indexing.Ntfs;

public sealed class NtfsStreamingInventoryTests
{
    [Fact]
    public void OnePassResolvesChildrenBeforeParentsAndSharesTheDirectoryMap()
    {
        NtfsMftEntry[] source = [File(30, 20, "name\ud800"), Dir(20, 10, "child"),
            File(30, 10, "alias.txt"), Dir(40, 10, "link") with { Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint },
            File(50, 40, "skip.txt"), Dir(10, 5, "root"), Dir(5, 5, "."), File(60, 5, "outside")];
        var reads = 0;
        var inventory = new NtfsRootInventory((_, _) => { reads++; return source; });
        var roots = new[] { NtfsRootInventoryTests.Root(10, @"C:\root") };
        var expected = inventory.Read(roots, default).OrderBy(entry => entry.Path).ToArray();
        reads = 0;
        using var security = new NtfsInventorySecurityGuard(_ => new Journal());
        var maps = new Dictionary<string, IReadOnlyList<UsnDirectoryEntry>>();
        var actual = inventory.ReadStreaming(roots, default, security, (root, entries) => maps.Add(root, entries))
            .OrderBy(entry => entry.Path).ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(1, reads);
        Assert.True(security.Validate(default));
        Assert.Equal(new ulong[] { 20, 40 }, maps[@"C:\root"].Select(entry => entry.Reference.Low).Order());
    }

    [Fact]
    public void MissingParentNeedsJournalEvidenceAndCyclesAreRejected()
    {
        using var guard = new NtfsInventorySecurityGuard(_ => new Journal());
        var reader = new NtfsRootInventory((_, _) => [Dir(5, 5, "."), Dir(10, 5, "root"), File(30, 20, "lost")]);
        reader.ReadStreaming([NtfsRootInventoryTests.Root(10, @"C:\root")], default, guard).ToArray();
        Assert.False(guard.Validate(default));
        using var cycleGuard = new NtfsInventorySecurityGuard(_ => new Journal());
        var cyclic = new NtfsRootInventory((_, _) => [Dir(5, 5, "."), Dir(10, 5, "root"), Dir(20, 21, "one"), Dir(21, 20, "two")]);
        Assert.Throws<InvalidDataException>(() => cyclic.ReadStreaming([NtfsRootInventoryTests.Root(10, @"C:\root")], default, cycleGuard).ToArray());
    }

    private static NtfsMftEntry Dir(ulong id, ulong parent, string name) => new(id, parent, name, true, FileAttributes.Directory, 0, 1, 2);
    private static NtfsMftEntry File(ulong id, ulong parent, string name) => new(id, parent, name, false, FileAttributes.Normal, 42, 1, 2);
    private sealed class Journal : IUsnJournalReader
    {
        public UsnJournalDescriptor QueryJournal() => new(1, 0, 100, 0, long.MaxValue, 0, 0);
        public UsnReadPage ReadPage(long startUsn, ulong expectedJournalId) => new(100, ReadOnlyMemory<byte>.Empty);
        public void Dispose() { }
    }
}
