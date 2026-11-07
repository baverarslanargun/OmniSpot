using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Index;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Index;

public sealed class FileIndexChangeNotificationTests
{
    private const ulong Root = 1UL << 48 | 5;
    private const ulong Folder = 1UL << 48 | 20;
    private static readonly long Time = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly IndexEntry File = new(1UL << 48 | 30, Root, "rapor.pdf",
        FileAttributes.Archive, 1024, Time, Time);

    [Fact]
    public void IdenticalRecordStatesAndHardLinksDoNotCountAsChanges()
    {
        var second = File with { ParentReference = Folder, Name = "kopya.pdf" };
        using var index = Build([File, second]);

        Assert.Equal(new IndexApplyResult(0, 2), index.Reconcile([new(File.FileReference, [File, second])]));
        Assert.Equal(new IndexApplyResult(0, 1), index.Apply([IndexChange.Upsert(File)]));
        Assert.Equal(2, index.Search(".pdf", 10, ResultView.SearchDefault).Count);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("parent")]
    [InlineData("size")]
    [InlineData("modified")]
    [InlineData("created")]
    [InlineData("attributes")]
    public void ARealFieldChangeIsAppliedOnceAndItsReplayIsSkipped(string field)
    {
        using var index = Build([File]);
        var changed = field switch
        {
            "name" => File with { Name = "RAPOR.pdf" },
            "parent" => File with { ParentReference = Folder },
            "size" => File with { SizeBytes = 4096 },
            "modified" => File with { LastWriteTimeUtc = Time + TimeSpan.TicksPerSecond },
            "created" => File with { CreatedTimeUtc = Time + TimeSpan.TicksPerSecond },
            "attributes" => File with { Attributes = FileAttributes.Archive | FileAttributes.ReadOnly },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        Assert.Equal(new IndexApplyResult(1, 0), index.Reconcile([new(File.FileReference, [changed])]));
        Assert.Equal(new IndexApplyResult(0, 1), index.Reconcile([new(File.FileReference, [changed])]));
        var found = Assert.Single(index.Search(".pdf", 10, ResultView.SearchDefault));
        Assert.Equal(changed.Name, found.Name);
        Assert.Equal(changed.SizeBytes, found.SizeBytes);
        Assert.Equal(new DateTime(changed.LastWriteTimeUtc, DateTimeKind.Utc).ToLocalTime(), found.LastWriteTime);
        Assert.Equal(Path.Combine(@"C:\", field == "parent" ? "Belgeler" : "", changed.Name), found.FullPath);
    }

    [Fact]
    public void ImpactKeepsOldAndNewNamesIncludingDeletedHardLinks()
    {
        var second = File with { ParentReference = Folder, Name = "ikinci.pdf" };
        using var index = Build([File, second]);
        var renamed = File with { Name = "yeni.pdf" };

        index.Reconcile([new(File.FileReference, [renamed])], out var impact);

        Assert.NotNull(impact);
        Assert.False(impact.IncludesDirectories);
        Assert.Equal(new[] { "ikinci.pdf", "rapor.pdf", "yeni.pdf" }, impact.Names!.Order().ToArray());
        index.Reconcile([new(File.FileReference, [])], out var deleted);
        Assert.Equal(new[] { "yeni.pdf" }, deleted!.Names);
    }

    [Fact]
    public void OnlyDirectoryStructureChangesInvalidateDescendantSearches()
    {
        using var index = Build([File with { ParentReference = Folder }]);
        var folder = new IndexEntry(Folder, Root, "Belgeler", FileAttributes.Directory, 0, Time);
        index.Reconcile([new(Folder, [folder with { LastWriteTimeUtc = Time + TimeSpan.TicksPerSecond }])], out var metadata);
        Assert.NotNull(metadata);
        Assert.False(metadata.IncludesDirectories);
        Assert.Equal(new[] { "Belgeler" }, metadata.Names);

        index.Reconcile([new(Folder, [folder with { Name = "Arsiv" }])], out var renamed);
        Assert.True(renamed!.IncludesDirectories);
        Assert.Contains("Belgeler", renamed.Names!);
        Assert.Contains("Arsiv", renamed.Names!);
        Assert.Equal(@"C:\Arsiv\rapor.pdf", Assert.Single(index.Search("rapor", 10, ResultView.SearchDefault)).FullPath);
    }

    [Fact]
    public void AnOversizedImpactFallsBackToUnknownScope()
    {
        using var index = Build([]);
        var entries = Enumerable.Range(100, 4097)
            .Select(i => new IndexEntry(1UL << 48 | (uint)i, Root, $"file-{i}.txt", FileAttributes.Archive, 1, Time));
        index.Reconcile(entries.Select(entry => new IndexRecordState(entry.FileReference, [entry])).ToArray(), out var impact);
        Assert.NotNull(impact);
        Assert.Null(impact.Names);
    }

    [Fact]
    public void DeletionAndRecordReuseStillCountAsChanges()
    {
        using var index = Build([File]);
        var replacement = File with { FileReference = 2UL << 48 | 30, Name = "yeni.pdf" };
        Assert.Equal(new IndexApplyResult(1, 0), index.Apply([IndexChange.Upsert(replacement)]));
        Assert.Equal("yeni.pdf", Assert.Single(index.Search(".pdf", 10, ResultView.SearchDefault)).Name);
        Assert.Equal(new IndexApplyResult(1, 0), index.Reconcile([new(replacement.FileReference, [])]));
        Assert.Empty(index.Search(".pdf", 10, ResultView.SearchDefault));
        Assert.Equal(new IndexApplyResult(0, 0), index.Reconcile([new(replacement.FileReference, [])]));
    }

    private static FileIndex Build(IEnumerable<IndexEntry> files) => FileIndex.Build(@"C:\", Root,
        new[]
        {
            new IndexEntry(Root, Root, ".", FileAttributes.Directory, 0, Time),
            new IndexEntry(Folder, Root, "Belgeler", FileAttributes.Directory, 0, Time)
        }.Concat(files));
}
