using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.Indexing.Ntfs;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Indexing.Ntfs;

public sealed class NtfsRootInventoryTests
{
    [Fact]
    public void SharedVolumeIsReadTwiceForAllRootsAndHardlinksKeepBothNames()
    {
        var reads = 0;
        NtfsMftEntry[] entries = [Dir(5, 5, "."), Dir(10, 5, "one"), Dir(20, 5, "two"),
            File(30, 10, "a.txt"), File(30, 20, "b.txt"), File(40, 5, "outside.txt")];
        var reader = new NtfsRootInventory((_, _) => { reads++; return entries; });
        var result = reader.Read([Root(10, @"C:\one"), Root(20, @"C:\two")], default).ToArray();
        Assert.Equal(2, reads);
        Assert.Equal(4, result.Length);
        Assert.Contains(result, entry => entry.Path == @"C:\one\a.txt" && entry.SizeBytes == 42);
        Assert.Contains(result, entry => entry.Path == @"C:\two\b.txt");
        Assert.DoesNotContain(result, entry => entry.Path.Contains("outside"));
    }

    [Fact]
    public void HiddenSubtreesAreExcludedButAnExplicitHiddenRootIsIncluded()
    {
        NtfsMftEntry[] entries = [Dir(5, 5, "."), Dir(10, 5, "root") with { Attributes = FileAttributes.Hidden },
            Dir(20, 10, "hidden") with { Attributes = FileAttributes.Hidden }, File(30, 20, "secret"),
            File(40, 10, "visible")];
        var reader = new NtfsRootInventory((_, _) => entries);
        var result = reader.Read([Root(10, @"C:\root")], default).ToArray();
        Assert.Equal(2, result.Length);
        Assert.Contains(result, entry => entry.Path == @"C:\root\visible");
    }

    [Fact]
    public void ReparseEntryIsReturnedForTraversalButRawChildrenAreNot()
    {
        NtfsMftEntry[] entries = [Dir(5, 5, "."), Dir(10, 5, "root"),
            Dir(20, 10, "link") with { Attributes = FileAttributes.ReparsePoint }, File(30, 20, "child")];
        var reader = new NtfsRootInventory((_, _) => entries);
        var result = reader.Read([Root(10, @"C:\root")], default).ToArray();
        Assert.Equal(2, result.Length);
        Assert.Contains(result, entry => entry.Path == @"C:\root\link");
    }

    [Fact]
    public void MissingOrChangedDirectoryChainFailsTheWholeInventory()
    {
        var pass = 0;
        var reader = new NtfsRootInventory((_, _) => ++pass == 1
            ? [Dir(5, 5, "."), Dir(10, 5, "root")]
            : [Dir(5, 5, "."), Dir(10, 5, "root"), Dir(20, 10, "new")]);
        Assert.Throws<InvalidDataException>(() => reader.Read([Root(10, @"C:\root")], default).ToArray());
    }

    [Fact]
    public void DirectoryOrderAndVolumeRootDoNotRequirePerFileLookups()
    {
        NtfsMftEntry[] entries = [File(30, 20, "name\ud800"), Dir(20, 10, "child"), Dir(10, 5, "parent"), Dir(5, 5, ".")];
        var reader = new NtfsRootInventory((_, _) => entries);
        var result = reader.Read([Root(5, @"C:\")], default).ToArray();
        Assert.Contains(result, entry => entry.Path == "C:\\parent\\child\\name\ud800");
    }

    internal static ChangeFeedSubscribedRoot Root(ulong reference, string path) =>
        new(path, new ChangeFeedRootIdentity("ntfs-vsn:0x0000000000000001", $"0x{reference:X16}"),
            ChangeFeedRootGeneration.New());

    private static NtfsMftEntry Dir(ulong id, ulong parent, string name) =>
        new(id, parent, name, true, FileAttributes.Directory, 0, 1, 2);
    private static NtfsMftEntry File(ulong id, ulong parent, string name) =>
        new(id, parent, name, false, FileAttributes.Normal, 42, 1, 2);
}
