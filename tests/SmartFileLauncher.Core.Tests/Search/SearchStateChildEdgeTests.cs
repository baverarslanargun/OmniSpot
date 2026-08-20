using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

/// <summary>
/// `SearchState`'in çocuk-kenarı invariantını sabitler: bir öğenin
/// `ParentPath`'i varsa kenar **her zaman** yazılır, ebeveyn o an state'te
/// olmasa bile.
///
/// Eskiden kenar yalnız ebeveyn mevcutken yazılıyor ve sonradan onarılmıyordu;
/// çocuk ebeveynden önce ayrı bir çağrıda gelirse kenar kalıcı olarak
/// kayboluyordu. Sarkan kenar okuma tarafında zararsızdır: `GetDescendants`
/// `_itemsByPath`'te bulunmayan yolu zaten atlar.
/// </summary>
public sealed class SearchStateChildEdgeTests
{
    private const string Root = @"C:\Data";
    private const string Folder = @"C:\Data\Klasor";
    private const string ChildFile = @"C:\Data\Klasor\dosya.txt";

    /// <summary>
    /// Asıl kusur buydu: çocuk önce, ebeveyn sonra, ayrı çağrılarda.
    /// </summary>
    [Fact]
    public void ChildAddedBeforeItsParentStillBecomesADescendant()
    {
        var (folder, child) = BuildFolderWithChild();

        var state = SearchState.Empty
            .WithUpserts([child], new BasicTokenizer())
            .WithUpserts([folder], new BasicTokenizer());

        Assert.Equal([ChildFile], DescendantPaths(state, Folder));
    }

    /// <summary>
    /// Ters sıra eskiden de çalışıyordu; buradaki amaç düzeltmenin onu
    /// bozmadığını sabitlemek.
    /// </summary>
    [Fact]
    public void ParentAddedBeforeItsChildStillBecomesADescendant()
    {
        var (folder, child) = BuildFolderWithChild();

        var state = SearchState.Empty
            .WithUpserts([folder], new BasicTokenizer())
            .WithUpserts([child], new BasicTokenizer());

        Assert.Equal([ChildFile], DescendantPaths(state, Folder));
    }

    /// <summary>
    /// `Create` yolu da aynı invariantı taşımalı: ebeveyni verilen düğüm
    /// kümesinin dışında kalan çocuk yine kenarını almalı.
    /// </summary>
    [Fact]
    public void CreateKeepsTheChildEdgeWhenTheParentIsOutsideTheNodeSet()
    {
        var (_, child) = BuildFolderWithChild();

        var state = SearchState.Create([child], new BasicTokenizer());

        Assert.Equal([ChildFile], DescendantPaths(state, Folder));
    }

    /// <summary>
    /// Kenar yazıldıktan sonra silme onu temizlemeli; yoksa düzeltme sarkan
    /// kenarı kalıcılaştırırdı. İlk iddia düzeltmeyi, ikincisi temizliği tutar.
    /// </summary>
    [Fact]
    public void RemovingAChildDropsItsEdge()
    {
        var (folder, child) = BuildFolderWithChild();
        var state = SearchState.Empty
            .WithUpserts([child], new BasicTokenizer())
            .WithUpserts([folder], new BasicTokenizer());

        Assert.Equal([ChildFile], DescendantPaths(state, Folder));
        Assert.Empty(DescendantPaths(state.WithoutPathAndDescendants(ChildFile), Folder));
    }

    /// <summary>
    /// Dizin aynı yolda dosyaya dönüşürse altağaç düşer. `WithUpserts` bu yolu
    /// `WithoutPathAndDescendants` üzerinden işlediği için kenarların da
    /// gitmesi gerekir.
    /// </summary>
    [Fact]
    public void DirectoryReplacedByAFileLosesItsDescendants()
    {
        var (folder, child) = BuildFolderWithChild();
        var state = SearchState.Empty.WithUpserts([folder, child], new BasicTokenizer());
        Assert.Equal([ChildFile], DescendantPaths(state, Folder));

        var replaced = state.WithUpserts(
            [new FileSystemNode("Klasor", Folder, false)],
            new BasicTokenizer());

        Assert.Empty(DescendantPaths(replaced, Folder));
        Assert.Empty(replaced.Get("dosya"));
    }

    /// <summary>
    /// Kenar sözlüğü `OrdinalIgnoreCase` anahtarlı; çocuğun `ParentPath` yazımı
    /// ebeveynin yazımından farklı olsa da kenar çözülmeli.
    /// </summary>
    [Fact]
    public void ChildEdgeResolvesParentPathCaseInsensitively()
    {
        var upperFolder = new FileSystemNode("KLASOR", @"C:\Data\KLASOR", true);
        var child = new FileSystemNode("dosya.txt", @"C:\Data\KLASOR\dosya.txt", false);
        upperFolder.AddChild(child);

        var state = SearchState.Empty.WithUpserts([child], new BasicTokenizer());

        Assert.Equal(
            [@"C:\Data\KLASOR\dosya.txt"],
            DescendantPaths(state, @"C:\data\klasor"));
    }

    private static string[] DescendantPaths(SearchState state, string parentPath) =>
        state.GetDescendants(ParentItem(parentPath))
            .Select(item => item.FullPath)
            .ToArray();

    // Ebeveyn state'te bulunmayabilir; `GetDescendants` yalnız `FullPath`
    // kullandığı için taşıyıcı bir öğe yeterli.
    private static SearchItem ParentItem(string parentPath) =>
        new("Klasor", parentPath, true, null, null, null, 0, Root);

    private static (FileSystemNode Folder, FileSystemNode Child) BuildFolderWithChild()
    {
        var folder = new FileSystemNode("Klasor", Folder, true);
        var child = new FileSystemNode("dosya.txt", ChildFile, false);
        folder.AddChild(child);
        return (folder, child);
    }
}
