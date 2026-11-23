using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class RecycleBinRestoreTests
{
    private static readonly DateTime Deleted = new(2026, 11, 19, 21, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheNewestDeletionFromTheSameFolderAndNameIsRestored()
    {
        var older = new RecycledEntry(@"C:\Belgeler", "not.txt", Deleted.AddMinutes(-30));
        var ours = new RecycledEntry(@"C:\Belgeler\", "NOT.txt", Deleted.AddSeconds(1));
        var otherFolder = new RecycledEntry(@"D:\Belgeler", "not.txt", Deleted.AddSeconds(1));

        var matches = RecycleBinRestore.Match([older, otherFolder, ours], [@"C:\Belgeler\not.txt"], Deleted);

        Assert.Same(ours, Assert.Single(matches).Value);
    }

    [Fact]
    public void DeletionsBeforeTheOperationAreNeverRestored()
    {
        var earlier = new RecycledEntry(@"C:\Belgeler", "not.txt", Deleted.AddSeconds(-3));
        var withinClockSlack = new RecycledEntry(@"C:\Belgeler", "rapor.pdf", Deleted.AddSeconds(-1));

        var matches = RecycleBinRestore.Match(
            [earlier, withinClockSlack],
            [@"C:\Belgeler\not.txt", @"C:\Belgeler\rapor.pdf"],
            Deleted);

        Assert.Equal([@"C:\Belgeler\rapor.pdf"], matches.Keys);
    }

    [Fact]
    public void ShellTimesAreReadAsTheLaterOfUtcAndLocalTime()
    {
        var shellTime = new DateTime(2026, 11, 19, 21, 4, 38, DateTimeKind.Unspecified);
        var fromLocal = DateTime.SpecifyKind(shellTime, DateTimeKind.Local).ToUniversalTime();
        var asUtc = DateTime.SpecifyKind(shellTime, DateTimeKind.Utc);

        var reading = RecycleBinRestore.LatestUtcReading(shellTime);

        Assert.Equal(DateTimeKind.Utc, reading.Kind);
        Assert.Equal(asUtc > fromLocal ? asUtc : fromLocal, reading);
        Assert.True(reading >= asUtc && reading >= fromLocal);
    }

    [Fact]
    public void EachRecycledItemIsUsedOnlyOnce()
    {
        var file = new RecycledEntry(@"C:\Belgeler", "not.txt", Deleted);
        var folder = new RecycledEntry(@"C:\Arsiv", "not.txt", Deleted);

        var matches = RecycleBinRestore.Match(
            [file, folder],
            [@"C:\Belgeler\not.txt", @"C:\Arsiv\not.txt", @"C:\Belgeler\not.txt\"],
            Deleted);

        Assert.Same(file, matches[@"C:\Belgeler\not.txt"]);
        Assert.Same(folder, matches[@"C:\Arsiv\not.txt"]);
        Assert.Equal(2, matches.Count);
    }
}
