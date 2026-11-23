using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Indexing.Folders;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Indexing;

public sealed class FolderVolumeIndexTests
{
    private const ulong MftRoot = 5UL << 48 | 5;

    [Fact]
    public void FolderSyncMatchesAFreshScanAfterCreatesDeletesRenamesAndEdits()
    {
        using var directory = new TemporaryDirectory();
        var data = directory.CreateDirectory("veri");
        directory.CreateFile(@"veri\rapor.pdf", "1");
        directory.CreateFile(@"veri\Notlar\liste.txt", "12");
        directory.CreateFile(@"veri\Notlar\eski.txt", "123");
        directory.CreateFile(@"veri\Silinecek\iç\dosya.bin", "1234");
        directory.CreateFile(@"veri\Taşınacak\alt\belge.docx", "12345");
        directory.CreateFile(@"veri\küçük.TXT", "x");
        var volume = SettingsIndexedLocationProvider.VolumeRoot(data);
        var ids = new FolderReferences(FolderVolumeScanner.FirstReference);
        using var index = Build(volume, data, ids);

        File.WriteAllText(Path.Combine(data, "yeni.txt"), "yeni");
        File.WriteAllText(Path.Combine(data, "rapor.pdf"), "daha uzun içerik");
        File.Delete(Path.Combine(data, "Notlar", "eski.txt"));
        Directory.Delete(Path.Combine(data, "Silinecek"), recursive: true);
        Directory.Move(Path.Combine(data, "Taşınacak"), Path.Combine(data, "Notlar", "Taşındı"));
        File.Move(Path.Combine(data, "küçük.TXT"), Path.Combine(data, "Küçük.txt"));
        Directory.CreateDirectory(Path.Combine(data, "Yeni Klasör", "derin"));
        File.WriteAllText(Path.Combine(data, "Yeni Klasör", "derin", "iç.txt"), "iç");
        var dirty = new[] { data, Path.Combine(data, "Notlar"), Path.Combine(data, "Silinecek", "iç"), Path.Combine(data, "Taşınacak") };

        var states = FolderVolumeSync.Collect(index, FolderVolumeScanner.Roots(volume, [data]), dirty, ids, default);
        var applied = index.Reconcile(states);

        Assert.True(applied.Applied > 0);
        using var fresh = Build(volume, data, new FolderReferences(FolderVolumeScanner.FirstReference));
        Assert.Equal(Inventory(fresh, data), Inventory(index, data));
        Assert.Contains(Inventory(index, data), item => item.EndsWith(@"\Küçük.txt|f|1", StringComparison.Ordinal));
        Assert.Empty(FolderVolumeSync.Collect(index, FolderVolumeScanner.Roots(volume, [data]), dirty, ids, default));
    }

    [Fact]
    public void ADirtyPathInsideANewFolderClimbsToTheNearestIndexedFolder()
    {
        using var directory = new TemporaryDirectory();
        var data = directory.CreateDirectory("veri");
        directory.CreateFile(@"veri\ilk.txt");
        var volume = SettingsIndexedLocationProvider.VolumeRoot(data);
        var ids = new FolderReferences(FolderVolumeScanner.FirstReference);
        using var index = Build(volume, data, ids);
        var deep = directory.CreateDirectory(@"veri\a\b\c");
        File.WriteAllText(Path.Combine(deep, "son.txt"), "son");

        var outside = Path.GetDirectoryName(data)!;
        index.Reconcile(FolderVolumeSync.Collect(index, FolderVolumeScanner.Roots(volume, [data]), [deep, outside], ids, default));

        using var fresh = Build(volume, data, new FolderReferences(FolderVolumeScanner.FirstReference));
        Assert.Equal(Inventory(fresh, data), Inventory(index, data));
        Assert.Contains(Inventory(index, data), item => item.EndsWith(@"\a\b\c\son.txt|f|3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AutomaticFallsBackToFolderScanWhenTheJournalIsInactive()
    {
        using var directory = new TemporaryDirectory();
        var data = directory.CreateDirectory("veri");
        directory.CreateFile(@"veri\Belgeler\rapor.pdf");
        var volume = SettingsIndexedLocationProvider.VolumeRoot(data);
        var channel = new InactiveJournalChannel();
        var notices = new List<string>();
        using var service = new VolumeIndexLifecycleService(channel, new Locations([data]), directory.CreateDirectory("indeks"),
            policy: new Policy(IndexVolumeMethod.Automatic));
        service.Notice += notices.Add;

        var started = await service.InitializeAsync();

        Assert.Equal(1, channel.Opens);
        Assert.Equal(1, started.Stats.FileCount);
        Assert.Equal([Path.Combine(data, "Belgeler", "rapor.pdf")], Search(service, "rapor"));
        Assert.Empty(Search(service, "OmniSpot.Tests"));
        Assert.Contains($"{volume} için Windows değişiklik günlüğü kapalı; klasör taraması kullanılıyor.", notices);
        Assert.StartsWith("F", Path.GetFileName(Assert.Single(File.ReadAllLines(service.DatabasePath))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExplicitMftChoiceOnAnInactiveJournalIsNotReplacedByFolderScan()
    {
        using var directory = new TemporaryDirectory();
        var data = directory.CreateDirectory("veri");
        using var service = new VolumeIndexLifecycleService(new InactiveJournalChannel(), new Locations([data]),
            directory.CreateDirectory("indeks"), policy: new Policy(IndexVolumeMethod.Mft));

        var failure = await Assert.ThrowsAsync<IndexIssueException>(() => service.InitializeAsync());

        Assert.Equal(IndexIssueReason.JournalInactive, failure.Issue.Reason);
    }

    [Theory]
    [InlineData(IndexVolumeMethod.Automatic)]
    [InlineData(IndexVolumeMethod.Mft)]
    public async Task VolumesThatAreNotNtfsNeverAskTheServiceForMft(IndexVolumeMethod method)
    {
        using var directory = new TemporaryDirectory();
        var data = directory.CreateDirectory("veri");
        directory.CreateFile(@"veri\fotoğraf.jpg");
        var channel = new InactiveJournalChannel();
        var notices = new List<string>();
        using var service = new VolumeIndexLifecycleService(channel, new Locations([data]), directory.CreateDirectory("indeks"),
            policy: new Policy(method, IndexVolumeKind.Other));
        service.Notice += notices.Add;

        await service.InitializeAsync();

        Assert.Equal(0, channel.Opens);
        Assert.Equal([Path.Combine(data, "fotoğraf.jpg")], Search(service, "fotoğraf"));
        Assert.Contains(notices, notice => notice.EndsWith("NTFS değil; klasör taraması kullanılıyor.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MftAndFolderVolumesAreSearchedTogether()
    {
        using var directory = new TemporaryDirectory();
        var data = directory.CreateDirectory("veri");
        directory.CreateFile(@"veri\rapor-taslak.txt");
        var folderVolume = SettingsIndexedLocationProvider.VolumeRoot(data);
        var channel = new MftChannel(@"D:\");
        using var service = new VolumeIndexLifecycleService(channel, new Locations([@"D:\", data]), directory.CreateDirectory("indeks"),
            policy: new Policy(IndexVolumeMethod.Automatic, perVolume: new() { [folderVolume] = IndexVolumeMethod.Folders }));

        await service.InitializeAsync();

        Assert.Equal(new[] { Path.Combine(data, "rapor-taslak.txt"), @"D:\Belgeler\rapor.pdf" }.Order(StringComparer.Ordinal).ToArray(),
            Search(service, "rapor").Order(StringComparer.Ordinal).ToArray());
        Assert.True(channel.Opens > 0);
        Assert.Equal(2, File.ReadAllLines(service.DatabasePath).Length);
    }

    [Fact]
    public async Task AFolderSnapshotOpensAtOnceAndABackgroundRescanAddsChangesMadeWhileClosed()
    {
        using var directory = new TemporaryDirectory();
        var data = directory.CreateDirectory("veri");
        directory.CreateFile(@"veri\eski.txt");
        var store = directory.CreateDirectory("indeks");
        using (var first = new VolumeIndexLifecycleService(new InactiveJournalChannel(), new Locations([data]), store,
            policy: new Policy(IndexVolumeMethod.Folders)))
            await first.InitializeAsync();
        File.WriteAllText(Path.Combine(data, "kapaliyken.txt"), "yeni");
        File.Delete(Path.Combine(data, "eski.txt"));

        using var second = new VolumeIndexLifecycleService(new InactiveJournalChannel(), new Locations([data]), store,
            policy: new Policy(IndexVolumeMethod.Folders));
        second.Preload();
        await second.InitializeAsync();

        Assert.Equal([Path.Combine(data, "eski.txt")], Search(second, "eski"));
        Assert.Empty(Search(second, "kapaliyken"));
        await WaitUntil(second, data, () => Search(second, "kapaliyken").Length == 1 && Search(second, "eski").Length == 0);
    }

    [Fact]
    public async Task WatcherChangesReachSearchWithoutARescan()
    {
        using var directory = new TemporaryDirectory();
        var data = directory.CreateDirectory("veri");
        directory.CreateFile(@"veri\Proje\plan.txt");
        using var service = new VolumeIndexLifecycleService(new InactiveJournalChannel(), new Locations([data]),
            directory.CreateDirectory("indeks"), policy: new Policy(IndexVolumeMethod.Folders));
        var changes = 0;
        service.FileChanged += _ => Interlocked.Increment(ref changes);
        await service.InitializeAsync();

        File.WriteAllText(Path.Combine(data, "Proje", "bütçe.xlsx"), "1");
        Directory.Move(Path.Combine(data, "Proje"), Path.Combine(data, "Arşiv"));

        await WaitUntil(service, data, () => Search(service, "bütçe").SequenceEqual([Path.Combine(data, "Arşiv", "bütçe.xlsx")]) &&
            Search(service, "plan").SequenceEqual([Path.Combine(data, "Arşiv", "plan.txt")]));
        Assert.True(Volatile.Read(ref changes) > 0);
    }

    private static async Task WaitUntil(VolumeIndexLifecycleService service, string path, Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await service.EnsureSyncedAsync(path, deadline.Token);
            await Task.Delay(100, deadline.Token);
        }
    }

    private static FileIndex Build(string volume, string data, FolderReferences ids) =>
        FileIndex.Build(volume, FolderVolumeScanner.RootReference, FolderVolumeScanner.Scan(volume, [data], ids, null, default));

    private static string[] Inventory(FileIndex index, string data) =>
        index.AllItems(default)
            .Where(item => item.FullPath.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(item => $"{item.FullPath}|{(item.IsDirectory ? "d" : "f")}|{item.SizeBytes}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] Search(IIndexLifecycleService service, string query)
    {
        var state = service.CreateSearchState();
        return ((ILiteralNameQuerySnapshot)state).SearchLiteralNames(query, 50, ResultView.SearchDefault, default)
            .Select(result => result.FullPath).ToArray();
    }

    private sealed class Locations(IReadOnlyList<string> roots) : IIndexedLocationProvider
    {
        public IndexLocations Resolve() => new(@"C:\Users\test\Desktop", roots);
    }

    private sealed class Policy(IndexVolumeMethod method, IndexVolumeKind kind = IndexVolumeKind.Ntfs,
        Dictionary<string, IndexVolumeMethod>? perVolume = null) : IIndexVolumePolicy
    {
        public IndexVolumeMethod MethodFor(string volume) =>
            perVolume is not null && perVolume.TryGetValue(volume, out var chosen) ? chosen : method;
        public IndexVolumeKind KindOf(string volume) => kind;
    }

    private sealed class InactiveJournalChannel : IChangeFeedRequestChannel
    {
        internal int Opens;

        public Task<ChangeFeedResponse> SendAsync(ChangeFeedRequest request, CancellationToken cancellationToken)
        {
            if (request.Kind == ChangeFeedRequestKind.IndexVolumeOpen) Interlocked.Increment(ref Opens);
            var failure = SmartFileLauncher.Core.ChangeFeed.Usn.UsnVolumeJournalReader.TranslateFailure(1179, "FSCTL_QUERY_USN_JOURNAL");
            return Task.FromResult(ChangeFeedResponse.Failed(ChangeFeedResponseStatus.Unavailable, failure.Message) with
            {
                IndexIssue = IndexIssue.FromException(failure, request.RootPath, request.Kind.ToString())
            });
        }
    }

    private sealed class MftChannel(string root) : IChangeFeedRequestChannel
    {
        internal int Opens;

        public Task<ChangeFeedResponse> SendAsync(ChangeFeedRequest request, CancellationToken cancellationToken)
        {
            Assert.Equal(root, Path.GetPathRoot(request.RootPath!));
            var page = request.Kind switch
            {
                ChangeFeedRequestKind.IndexVolumeOpen => Opened(),
                ChangeFeedRequestKind.IndexVolumeScan => new IndexVolumePageDto(root, 0, 0, 0, 0, 0, null, IndexEntryCodec.PackEntries(
                [
                    new(MftRoot, MftRoot, ".", FileAttributes.Directory, 0, 0),
                    new(1UL << 48 | 20, MftRoot, "Belgeler", FileAttributes.Directory, 0, DateTime.UtcNow.Ticks),
                    new(1UL << 48 | 21, 1UL << 48 | 20, "rapor.pdf", FileAttributes.Archive, 10, DateTime.UtcNow.Ticks)
                ]), Count: 3, Done: true),
                ChangeFeedRequestKind.IndexVolumeChanges => new IndexVolumePageDto(root, 0, 0, 9, 0, 100, Done: true),
                _ => throw new InvalidOperationException(request.Kind.ToString())
            };
            return Task.FromResult(new ChangeFeedResponse(ChangeFeedProtocol.Version, ChangeFeedResponseStatus.Ok, Volume: page));
        }

        private IndexVolumePageDto Opened()
        {
            Interlocked.Increment(ref Opens);
            return new(root, MftRoot, 0xDCBA, 9, 0, 100);
        }
    }
}
