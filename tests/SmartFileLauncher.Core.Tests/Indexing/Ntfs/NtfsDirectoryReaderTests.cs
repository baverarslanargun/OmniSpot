using SmartFileLauncher.Core.Indexing.Ntfs;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Indexing.Ntfs;

public sealed class NtfsDirectoryReaderTests
{
    [Fact]
    public void PagingFileMetadataCanBeReadWithoutOpeningThePagingFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;
        using var reader = new NtfsDirectoryReader(root);
        NtfsMftEntry? expected = null;
        while (true)
        {
            var entries = reader.ReadNext();
            if (entries.Count == 0) break;
            expected = entries.FirstOrDefault(entry => string.Equals(entry.Name, "pagefile.sys", StringComparison.OrdinalIgnoreCase));
            if (expected is not null) break;
        }
        if (expected is null) return;

        var actual = NtfsDirectoryReader.ReadFile(Path.Combine(root, expected.Name), reader.Root.FileReference);

        Assert.NotNull(actual);
        Assert.Equal(expected.FileReference, actual.FileReference);
        Assert.Equal(expected.ParentFileReference, actual.ParentFileReference);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Attributes, actual.Attributes);
        Assert.Equal(expected.CreatedTimeUtc, actual.CreatedTimeUtc);
        Assert.Equal(expected.IsDirectory, actual.IsDirectory);
    }

    [Fact]
    public void BatchedNamesMetadataAndReferencesMatchIndividualReads()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "OmniSpot.DirectoryReader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var i = 0; i < 700; i++) File.WriteAllText(Path.Combine(root, $"ışık-{i}.txt"), "hello");
            Directory.CreateDirectory(Path.Combine(root, "child"));
            using var reader = new NtfsDirectoryReader(root);
            var entries = new List<NtfsMftEntry>();
            while (true) { var page = reader.ReadNext(); if (page.Count == 0) break; entries.AddRange(page); }
            Assert.Equal(701, entries.Count);
            Assert.Equal(701, entries.Select(entry => entry.FileReference).Distinct().Count());
            foreach (var entry in entries)
            {
                var actual = NtfsDirectoryReader.ReadFile(Path.Combine(root, entry.Name), reader.Root.FileReference);
                Assert.NotNull(actual);
                Assert.Equal(entry.FileReference, actual.FileReference);
                Assert.Equal(entry.CreatedTimeUtc, actual.CreatedTimeUtc);
                Assert.Equal(entry.LastWriteTimeUtc, actual.LastWriteTimeUtc);
                if (!entry.IsDirectory) Assert.Equal(entry.SizeBytes, actual.SizeBytes);
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
