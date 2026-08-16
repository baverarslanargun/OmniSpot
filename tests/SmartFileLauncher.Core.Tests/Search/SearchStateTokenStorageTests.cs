using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

/// <summary>
/// `SearchState`'in öğe başına token deposunu temsilden bağımsız olarak sabitler.
///
/// Sonuç **sırası** bilerek doğrulanmaz: posting kümeleri `ImmutableHashSet` ve
/// .NET string hash'i process başına randomize olduğundan eşit skorlu sonuçların
/// sırası bugün de her koşumda değişiyor. Sabitlenebilen ve sabitlenmesi gereken
/// şey üyelik, skor ve skora göre azalan sıralamadır.
/// </summary>
public sealed class SearchStateTokenStorageTests
{
    private const string Root = @"C:\Data";
    private const string Archive = @"C:\Data\Arsiv";

    // Aynı adda yinelenen token, harf durumu ve alt dizin bir arada.
    private static readonly string[] RaporPaths =
    [
        @"C:\Data\Arsiv\istanbul-rapor.txt",
        @"C:\Data\Arsiv\rapor-eski.txt",
        @"C:\Data\Arsiv\rapor-yeni.txt",
        @"C:\Data\ISTANBUL-RAPOR.txt",
        @"C:\Data\RAPOR-BUYUK.TXT",
        @"C:\Data\dosya-00-rapor.txt",
        @"C:\Data\dosya-01-rapor.txt",
        @"C:\Data\ozet-rapor.txt",
        @"C:\Data\rapor-ozet.txt",
        @"C:\Data\rapor-rapor.txt",
        @"C:\Data\rapor.txt"
    ];

    [Fact]
    public void GetReturnsEveryMatchingPathExactlyOnce()
    {
        var state = CreateState();

        var paths = state.Get("rapor").Select(item => item.FullPath).ToArray();

        Assert.Equal(RaporPaths, Sorted(paths));
        // `rapor-rapor.txt` adında token iki kez geçer; depo tekilleştirmezse
        // yol sonuçta iki kez görünür.
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void AsciiCaseVariantsResolveToTheSameToken()
    {
        var state = CreateState();

        Assert.Equal(
            Sorted(state.Get("rapor").Select(item => item.FullPath)),
            Sorted(state.Get("RAPOR").Select(item => item.FullPath)));
    }

    /// <summary>
    /// Eskiden bilinen boşluktu: indeksleme `tr-TR` ile küçültüyor (`I` → `ı`),
    /// arama `OrdinalIgnoreCase` ile karşılaştırıyor ve o `ı` ile `i`'yi
    /// eşitlemiyordu; `ISTANBUL-RAPOR.txt` "istanbul" aranınca bulunmuyordu.
    /// Katlama bunu kapattı: aksansız biçim **aslının yanına** ikinci token
    /// olarak yazılıyor, bu yüzden her iki yazım da dosyayı buluyor ve
    /// noktasız arama hâlâ yalnız kendi aslını hedefleyebiliyor.
    /// </summary>
    [Fact]
    public void TurkishDottedAndDotlessIBothResolveToTheSameFile()
    {
        var state = CreateState();

        // Noktalı arama artık iki dosyayı da buluyor — asıl düzeltme bu.
        Assert.Equal(
            [@"C:\Data\Arsiv\istanbul-rapor.txt", @"C:\Data\ISTANBUL-RAPOR.txt"],
            Sorted(state.Get("istanbul").Select(item => item.FullPath)));
        // Aslı silinmedi: noktasız biçim yalnız kendi dosyasını hedefliyor.
        Assert.Equal(
            [@"C:\Data\ISTANBUL-RAPOR.txt"],
            Sorted(state.Get("\u0131stanbul").Select(item => item.FullPath)));
    }

    /// <summary>
    /// Aynı corpus düz ve ters düğüm sırasıyla kurulur; `path → skor` eşlemesi
    /// aynı çıkmalıdır.
    ///
    /// **Sınır — bu test enumeration sırasını değiştiremez.** Ölçüldü: posting
    /// kümeleri `ImmutableHashSet` olduğundan enumeration sırası yalnız hash
    /// düzenine bağlıdır, ekleme sırasından bağımsızdır; girdiyi ters çevirmek
    /// `Get` sırasını değiştirmez. Enumeration sırası ancak process'ten
    /// process'e değişir (string hash tohumu), bu da bir unit test'te
    /// üretilemez. Burada sabitlenen şey kurulum sırası bağımsızlığıdır;
    /// process'ler arası sıra kararsızlığı çalışma günlüğüne ölçüm olarak
    /// kaydedildi ve ayrı bir ürün kalemidir.
    /// </summary>
    [Fact]
    public void ScoresDoNotDependOnNodeInputOrder()
    {
        var forward = ScoresFor(BuildNodes());
        var reversed = ScoresFor(Enumerable.Reverse(BuildNodes()).ToList());

        Assert.Equal(RaporPaths, Sorted(forward.Keys));
        Assert.Equal(
            forward.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            reversed.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
        // 50 (eşleşen token) + 100 (tüm token'lar) + 25 (ad token'ı) = 175;
        // `rapor.txt` ayrıca OpenCount 4 → +8.
        Assert.Equal(183d, forward[@"C:\Data\rapor.txt"]);
        Assert.All(
            forward.Where(pair => !pair.Key.EndsWith(@"\rapor.txt", StringComparison.OrdinalIgnoreCase)),
            pair => Assert.Equal(175d, pair.Value));
    }

    /// <summary>
    /// Yayımlanan `SearchState` bir anlık görüntüdür: türetilmiş state'i
    /// üretmek eskisini değiştiremez. Dizi temsili paylaşılan bir kap taşıdığı
    /// için bu kapı temsilden bağımsız olarak sabitlenir.
    /// </summary>
    [Fact]
    public void EarlierSnapshotsKeepTheirPostings()
    {
        var original = CreateState();

        var renamed = original.WithUpserts(
            [new FileSystemNode("butce-yeni.txt", @"C:\Data\rapor-ozet.txt", false)],
            new BasicTokenizer());
        var trimmed = original.WithoutPathAndDescendants(Archive);

        Assert.Equal(RaporPaths, Sorted(original.Get("rapor").Select(item => item.FullPath)));
        Assert.Empty(original.Get("butce"));
        Assert.Equal(RaporPaths.Length + 2, original.ItemCount);
        // Türetilmiş state'ler gerçekten değişmiş olmalı; yoksa yukarısı
        // hiçbir şey doğrulamayan boş bir kapı olurdu.
        Assert.Equal(RaporPaths.Length - 1, renamed.Get("rapor").Count);
        Assert.Single(renamed.Get("butce"));
        Assert.Equal(RaporPaths.Length - 3, trimmed.Get("rapor").Count);
    }

    [Fact]
    public void ResultsAreOrderedByScoreDescending()
    {
        var state = CreateState();
        var engine = new SearchEngine(_ => state, new BasicTokenizer(), new BasicScoringStrategy());

        var scores = engine.Search("rapor", maxResults: 50).Select(result => result.Score).ToArray();

        Assert.Equal(scores.OrderByDescending(score => score).ToArray(), scores);
    }

    // `WithoutPath` hangi posting'leri temizleyeceğini öğe başına token
    // deposundan öğrenir. Depo token kaybederse eski posting asılı kalır.
    [Fact]
    public void UpsertLeavesNoStalePostingForAReplacedName()
    {
        var state = CreateState();

        var renamed = state.WithUpserts(
            [new FileSystemNode("butce-yeni.txt", @"C:\Data\rapor-ozet.txt", false)],
            new BasicTokenizer());

        Assert.DoesNotContain(
            @"C:\Data\rapor-ozet.txt",
            renamed.Get("rapor").Select(item => item.FullPath),
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            @"C:\Data\rapor-ozet.txt",
            renamed.Get("ozet").Select(item => item.FullPath),
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            @"C:\Data\rapor-ozet.txt",
            renamed.Get("butce").Select(item => item.FullPath),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(RaporPaths.Length - 1, renamed.Get("rapor").Count);
    }

    [Fact]
    public void RemovalDropsExactlyTheRemovedSubtree()
    {
        var state = CreateState();

        var trimmed = state.WithoutPathAndDescendants(Archive);

        Assert.Equal(
            RaporPaths.Where(path => !path.StartsWith(Archive + @"\", StringComparison.OrdinalIgnoreCase)).ToArray(),
            Sorted(trimmed.Get("rapor").Select(item => item.FullPath)));
        Assert.Empty(trimmed.Get("eski"));
    }

    // Bu tek yer sırası pinlenebilir olan yer: `GetDescendants` açıkça
    // sıralar, hash düzenine bağlı değildir.
    [Fact]
    public void DescendantOrderingStaysDeterministic()
    {
        var state = CreateState();
        var root = state.GetAllItems().Single(item => item.FullPath == Root);

        var first = state.GetDescendants(root).Select(item => item.FullPath).ToArray();
        var second = CreateState()
            .GetDescendants(CreateState().GetAllItems().Single(item => item.FullPath == Root))
            .Select(item => item.FullPath)
            .ToArray();

        Assert.Equal(first, second);
        Assert.Equal(Archive, first[0]);
    }

    private static string[] Sorted(IEnumerable<string> values) =>
        values.OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static Dictionary<string, double> ScoresFor(List<FileSystemNode> nodes)
    {
        var state = SearchState.Create(nodes, new BasicTokenizer());
        var engine = new SearchEngine(_ => state, new BasicTokenizer(), new BasicScoringStrategy());
        return engine.Search("rapor", maxResults: 50)
            .ToDictionary(
                result => result.FullPath,
                result => result.Score,
                StringComparer.OrdinalIgnoreCase);
    }

    private static SearchState CreateState() =>
        SearchState.Create(BuildNodes(), new BasicTokenizer());

    private static List<FileSystemNode> BuildNodes()
    {
        var nodes = new List<FileSystemNode>();
        var root = new FileSystemNode("Data", Root, true);
        var archive = new FileSystemNode("Arsiv", Archive, true);
        root.AddChild(archive);
        nodes.Add(root);
        nodes.Add(archive);

        foreach (var path in RaporPaths)
        {
            var parent = path.StartsWith(Archive + @"\", StringComparison.OrdinalIgnoreCase)
                ? archive
                : root;
            var node = new FileSystemNode(Path.GetFileName(path), path, false)
            {
                Metadata = new FileMetadata
                {
                    OpenCount = path.EndsWith(@"\rapor.txt", StringComparison.OrdinalIgnoreCase) ? 4 : 0
                }
            };
            parent.AddChild(node);
            nodes.Add(node);
        }

        return nodes;
    }
}
