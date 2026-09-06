using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class WatcherIntakeCostTests
{
    [Fact]
    public void EachWatchGetsTheFullNotificationBuffer()
    {
        using var workspace = new TemporaryDirectory();
        var first = workspace.CreateDirectory("bir");
        var second = workspace.CreateDirectory("iki");

        using var watcher = new FileWatcherService(debounceMs: 1);
        watcher.Watch(first);
        watcher.Watch(second);

        Assert.Equal(
            new[] { 64 * 1024, 64 * 1024 },
            watcher.WatcherBufferSizes);
    }

    [Theory]
    [InlineData(@"C:\kok\node_modules", "a.js")]
    [InlineData(@"C:\kok\proje\obj", "b.dll")]
    [InlineData(@"C:\kok\proje", "yarim.tmp")]
    public void AnExcludedCreation_CostsNoFilesystemProbe(string directory, string name)
    {
        using var watcher = new FileWatcherService(debounceMs: 1, skipReparsePoints: true);

        watcher.OnFileCreated(
            watcher,
            new FileSystemEventArgs(WatcherChangeTypes.Created, directory, name));

        Assert.Equal(0, watcher.FilesystemProbeCount);
    }

    [Fact]
    public void AnOrdinaryCreation_StillProbesTheFilesystem()
    {
        using var watcher = new FileWatcherService(debounceMs: 1, skipReparsePoints: true);

        watcher.OnFileCreated(
            watcher,
            new FileSystemEventArgs(WatcherChangeTypes.Created, @"C:\kok\proje", "a.cs"));

        Assert.True(
            watcher.FilesystemProbeCount > 0,
            "Elenmeyen yol için dosya sistemi sorgusu hâlâ yapılmalı.");
    }

    [Fact]
    public void AnExcludedChange_CostsNoFilesystemProbe()
    {
        using var watcher = new FileWatcherService(debounceMs: 1, skipReparsePoints: true);

        watcher.OnFileChanged(
            watcher,
            new FileSystemEventArgs(WatcherChangeTypes.Changed, @"C:\kok\.git", "HEAD"));

        Assert.Equal(0, watcher.FilesystemProbeCount);
    }

    [Fact]
    public void AnExcludedDeletion_CostsNoFilesystemProbe()
    {
        using var watcher = new FileWatcherService(debounceMs: 1, skipReparsePoints: true);

        watcher.OnFileDeleted(
            watcher,
            new FileSystemEventArgs(WatcherChangeTypes.Deleted, @"C:\kok\bin", "app.exe"));

        Assert.Equal(0, watcher.FilesystemProbeCount);
    }

    [Fact]
    public void ARenameInsideExcludedGround_CostsNoFilesystemProbe()
    {
        using var watcher = new FileWatcherService(debounceMs: 1, skipReparsePoints: true);

        watcher.OnFileRenamed(
            watcher,
            new RenamedEventArgs(
                WatcherChangeTypes.Renamed,
                @"C:\kok\node_modules",
                "yeni.js",
                "eski.js"));

        Assert.Equal(0, watcher.FilesystemProbeCount);
    }

    [Fact]
    public async Task ARenameOutOfExcludedGround_StillReachesTheIndex()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("kok");

        using var watcher = new FileWatcherService(debounceMs: 1, skipReparsePoints: false);
        var seen = new List<FileChangeEvent>();
        watcher.OnChange += evt =>
        {
            lock (seen)
            {
                seen.Add(evt);
            }
        };

        watcher.Watch(root);
        watcher.Start();

        watcher.OnFileRenamed(
            watcher,
            new RenamedEventArgs(
                WatcherChangeTypes.Renamed,
                root,
                "kurtulan.js",
                Path.Combine("node_modules", "eski.js")));

        for (var attempt = 0; attempt < 100 && seen.Count == 0; attempt++)
        {
            await Task.Delay(20);
        }

        lock (seen)
        {
            var only = Assert.Single(seen);
            Assert.Equal(FileChangeType.Created, only.ChangeType);
            Assert.Equal(Path.Combine(root, "kurtulan.js"), only.FullPath);
        }
    }
}
