using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class UsnPrivateStoreTests
{
    [Fact]
    public void PrivateWritesStayOutOfDeliveryWithoutHidingSiblingNamesOrCrossBoundaryRenames()
    {
        var scope = new UsnRootProjection(new UsnChangeFeedState(@"C:\", new(7, new(1, 0)), 1, 0,
            [new(new(2, 0), "store", new(1, 0)), new(new(3, 0), "store-other", new(1, 0))]),
            new Probe(), excludedPath: @"C:\store");
        UsnRecord Record(long usn, ulong id, ulong parent, UsnReason reason, string name) =>
            new(usn, new(id, 0), new(parent, 0), reason, FileAttributes.Normal, name);

        var batch = scope.Project([
            Record(1, 20, 2, UsnReason.FileCreate | UsnReason.SecurityChange, "frame.bin"),
            Record(2, 21, 3, UsnReason.FileCreate, "visible.txt"),
            Record(3, 22, 2, UsnReason.RenameOldName, "inside.txt"),
            Record(4, 22, 1, UsnReason.RenameNewName, "outside.txt"),
            Record(5, 23, 1, UsnReason.RenameOldName, "removed.txt"),
            Record(6, 23, 2, UsnReason.RenameNewName, "private.txt")]);

        Assert.False(scope.LastSecurityChanged);
        Assert.Equal(ChangeFeedStatus.Ok, batch.Status);
        Assert.Equal(new[] { ChangeFeedEventKind.Created, ChangeFeedEventKind.Created, ChangeFeedEventKind.Deleted },
            batch.Events.Select(change => change.Kind));
        Assert.Equal(new[] { @"C:\store-other\visible.txt", @"C:\outside.txt", @"C:\removed.txt" },
            batch.Events.Select(change => change.FullPath));
        scope.Accept();
        scope.Project([Record(7, 21, 3, UsnReason.SecurityChange, "visible.txt")]);
        Assert.True(scope.LastSecurityChanged);
    }

    private sealed class Probe : IUsnIdentityProbe
    {
        public bool TryReadIdentity(string path, out UsnNodeIdentity identity)
        {
            identity = new(7, new(1, 0));
            return true;
        }
    }
}
