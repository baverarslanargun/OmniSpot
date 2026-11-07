using System.IO.Compression;
using SmartFileLauncher.Core.Application.Files;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Files;

public sealed class ZipPackagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omnispot-zip-" + Guid.NewGuid().ToString("N"));

    public ZipPackagingTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void CreatePacksFilesAndFoldersAndKeepsTopLevelNamesUnique()
    {
        var first = Write(Path.Combine("A", "rapor.txt"), "ilk");
        var second = Write(Path.Combine("B", "rapor.txt"), "ikinci");
        Write(Path.Combine("Klasör", "alt", "not.md"), "not");
        Directory.CreateDirectory(Path.Combine(_root, "Boş"));

        var archive = ZipPackaging.Create(
            [first, second, Path.Combine(_root, "Klasör"), Path.Combine(_root, "Boş")],
            _root,
            "Arşiv");

        Assert.Equal(Path.Combine(_root, "Arşiv.zip"), archive);
        using var zip = ZipFile.OpenRead(archive);
        Assert.Equal(
            ["Boş/", "Klasör/alt/not.md", "rapor (2).txt", "rapor.txt"],
            zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CreateNeverOverwritesAnExistingArchive()
    {
        var file = Write("veri.csv", "1,2");
        Write("veri.zip", "eski");

        var archive = ZipPackaging.Create([file], _root, "veri");

        Assert.Equal(Path.Combine(_root, "veri (2).zip"), archive);
        Assert.Equal("eski", File.ReadAllText(Path.Combine(_root, "veri.zip")));
    }

    [Fact]
    public void CreateRemovesThePartialArchiveWhenASourceIsMissing()
    {
        var file = Write("var.txt", "x");

        Assert.Throws<FileNotFoundException>(() =>
            ZipPackaging.Create([file, Path.Combine(_root, "yok.txt")], _root, "yarim"));
        Assert.False(File.Exists(Path.Combine(_root, "yarim.zip")));
    }

    [Fact]
    public void ExtractWritesIntoANewSiblingFolder()
    {
        var archive = ZipPackaging.Create([Write(Path.Combine("Kaynak", "a.txt"), "içerik")], _root, "paket");
        Directory.CreateDirectory(Path.Combine(_root, "paket"));

        var folder = ZipPackaging.Extract(archive);

        Assert.Equal(Path.Combine(_root, "paket (2)"), folder);
        Assert.Equal("içerik", File.ReadAllText(Path.Combine(folder, "a.txt")));
    }

    [Fact]
    public void ExtractRejectsEntriesThatEscapeTheDestination()
    {
        var archive = Path.Combine(_root, "kotu.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("../disari.txt").Open());
            writer.Write("x");
        }

        Assert.ThrowsAny<IOException>(() => ZipPackaging.Extract(archive));
        Assert.False(File.Exists(Path.Combine(_root, "disari.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "kotu")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
