using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class UsnSubtreeReaderTests
{
    [Fact]
    public void ReadSubtree_LearnsANormalMovedDirectory()
    {
        using var root = new TemporaryDirectory();
        var moved = root.CreateDirectory("gelen");
        Directory.CreateDirectory(Path.Combine(moved, "alt"));

        var probe = new UsnFileSystemIdentityProbe();
        Assert.True(probe.TryReadIdentity(moved, out var movedIdentity));

        var result = new UsnFileSystemSubtreeReader(probe).ReadSubtree(
            moved,
            movedIdentity.FileReference,
            movedIdentity.VolumeSerialNumber);

        Assert.Equal(0, result.SkippedDirectoryCount);
        Assert.Equal("alt", Assert.Single(result.Directories).Name);
    }

    [Fact]
    public void ReadSubtree_DoesNotLearnTheTargetOfAMovedReparsePoint()
    {
        using var target = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(target.Path, "hedefAlt"));
        Directory.CreateDirectory(Path.Combine(target.Path, "hedefAlt", "hedefDerin"));

        using var root = new TemporaryDirectory();
        var junction = Path.Combine(root.Path, "baglanti");
        WindowsDirectoryLink.CreateJunction(junction, target.Path);

        try
        {
            var probe = new UsnFileSystemIdentityProbe();
            Assert.True(probe.TryReadIdentity(junction, out var junctionIdentity));

            var result = new UsnFileSystemSubtreeReader(probe).ReadSubtree(
                junction,
                junctionIdentity.FileReference,
                junctionIdentity.VolumeSerialNumber);

            Assert.Empty(result.Directories);
            Assert.Equal(1, result.SkippedDirectoryCount);
        }
        finally
        {
            WindowsDirectoryLink.Delete(junction);
        }
    }

    [Fact]
    public void ReadSubtree_DoesNotDescendIntoAReparsePointBelowTheMovedDirectory()
    {
        using var target = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(target.Path, "hedefAlt"));

        using var root = new TemporaryDirectory();
        var moved = root.CreateDirectory("gelen");
        Directory.CreateDirectory(Path.Combine(moved, "gercek"));
        var junction = Path.Combine(moved, "baglanti");
        WindowsDirectoryLink.CreateJunction(junction, target.Path);

        try
        {
            var probe = new UsnFileSystemIdentityProbe();
            Assert.True(probe.TryReadIdentity(moved, out var movedIdentity));

            var result = new UsnFileSystemSubtreeReader(probe).ReadSubtree(
                moved,
                movedIdentity.FileReference,
                movedIdentity.VolumeSerialNumber);

            Assert.Equal("gercek", Assert.Single(result.Directories).Name);
            Assert.Equal(1, result.SkippedDirectoryCount);
        }
        finally
        {
            WindowsDirectoryLink.Delete(junction);
        }
    }

    [Fact]
    public void ReadSubtree_SkipsADirectoryThatIsNoLongerTheOneTheRecordNamed()
    {
        using var root = new TemporaryDirectory();
        var moved = root.CreateDirectory("gelen");
        Directory.CreateDirectory(Path.Combine(moved, "alt"));

        var probe = new UsnFileSystemIdentityProbe();
        Assert.True(probe.TryReadIdentity(moved, out var movedIdentity));

        var result = new UsnFileSystemSubtreeReader(probe).ReadSubtree(
            moved,
            UsnFileReference.FromNtfs(movedIdentity.FileReference.Low + 1),
            movedIdentity.VolumeSerialNumber);

        Assert.Empty(result.Directories);
        Assert.Equal(1, result.SkippedDirectoryCount);
    }

    [Fact]
    public void ReadSubtree_SkipsADirectoryOnAnotherVolume()
    {
        using var root = new TemporaryDirectory();
        var moved = root.CreateDirectory("gelen");
        Directory.CreateDirectory(Path.Combine(moved, "alt"));

        var probe = new UsnFileSystemIdentityProbe();
        Assert.True(probe.TryReadIdentity(moved, out var movedIdentity));

        var result = new UsnFileSystemSubtreeReader(probe).ReadSubtree(
            moved,
            movedIdentity.FileReference,
            movedIdentity.VolumeSerialNumber + 1);

        Assert.Empty(result.Directories);
        Assert.Equal(1, result.SkippedDirectoryCount);
    }

    [Fact]
    public void ReadSubtree_SkipsAMissingDirectory()
    {
        using var root = new TemporaryDirectory();

        var result = new UsnFileSystemSubtreeReader(new UsnFileSystemIdentityProbe())
            .ReadSubtree(
                Path.Combine(root.Path, "yok"),
                UsnFileReference.FromNtfs(42),
                volumeSerialNumber: 1);

        Assert.Empty(result.Directories);
        Assert.Equal(1, result.SkippedDirectoryCount);
    }
}
