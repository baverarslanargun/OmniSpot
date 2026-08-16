using System.Text;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

/// <summary>
/// Ad normalizasyonunun iki işini sabitler: ayrıştırılmış Unicode biçimini
/// toparlamak ve aksansız ikinci bir token üretmek. İkisi de kullanıcıya dönük
/// arama doğruluğu kuralıdır.
/// </summary>
public sealed class SearchTextNormalizationTests
{
    private const string Root = @"C:\Data";

    [Fact]
    public void FoldStripsTurkishDiacriticsAndMapsDotlessI()
    {
        Assert.Equal("gorusme", SearchTextNormalizer.Fold("görüşme"));
        Assert.Equal("cigdem", SearchTextNormalizer.Fold("çiğdem"));
        Assert.Equal("istanbul", SearchTextNormalizer.Fold("\u0131stanbul"));
        Assert.Equal("izmir", SearchTextNormalizer.Fold("\u0131zm\u0131r"));
    }

    // ASCII token'da katlama yeni nesne üretmemeli; her ad için çalışan bir yol.
    [Fact]
    public void FoldLeavesAsciiTokensUntouched()
    {
        const string token = "rapor";

        Assert.Same(token, SearchTextNormalizer.Fold(token));
    }

    // Geçersiz Unicode taşıyan bir ad taramayı düşürmemeli.
    [Fact]
    public void NormalizationSurvivesLoneSurrogates()
    {
        var broken = "rapor\ud800dosya";

        Assert.NotNull(SearchTextNormalizer.ToComposedForm(broken));
        Assert.NotNull(SearchTextNormalizer.Fold(broken));
    }

    /// <summary>
    /// Asıl regresyon: `İstanbul` adı macOS/ağ paylaşımlarında `I` + birleşen
    /// nokta olarak gelir. Normalize edilmezse `tr-TR` küçültmesi bunu
    /// `ı` + birleşen nokta yapar ve dosya hiçbir yazımla bulunamaz.
    /// </summary>
    [Fact]
    public void DecomposedFileNameIsStillFound()
    {
        var decomposed = "İstanbul".Normalize(NormalizationForm.FormD);
        Assert.NotEqual("İstanbul", decomposed);

        var state = StateWith(decomposed + ".txt");

        Assert.Single(state.Get("istanbul"));
        Assert.Single(state.GetPartial("istanb"));
    }

    [Fact]
    public void AccentedNameIsFoundByAsciiSpelling()
    {
        var state = StateWith("görüşme-notlari.docx");

        Assert.Single(state.Get("gorusme"));
        Assert.Single(state.Get("görüşme"));
    }

    /// <summary>
    /// Katlama aslını silmiyor; tam yazım hem aslını hem katlanmışını
    /// eşleştirdiği için daha yüksek puan alır ve üste çıkar.
    /// </summary>
    [Fact]
    public void ExactSpellingOutranksFoldedSpelling()
    {
        var tokenizer = new BasicTokenizer();
        var state = SearchState.Create(
            [
                Node("görüşme.txt"),
                Node("gorusme.txt")
            ],
            tokenizer);
        var engine = new SearchEngine(_ => state, tokenizer, new BasicScoringStrategy());

        var results = engine.Search("görüşme", maxResults: 10);

        Assert.Equal(2, results.Count);
        Assert.Equal(@"C:\Data\görüşme.txt", results[0].FullPath);
        Assert.True(results[0].Score > results[1].Score);
    }

    /// <summary>
    /// Katlanmış biçim, kelimenin **alternatifidir**; zorunlu ikinci parçası
    /// değil. `AdvancedSearchEngine` bir terimden çıkan token'ları kesiştirdiği
    /// için bu ayrım orada kritik: gruplanmazsa `görüşme` sorgusu yalnız
    /// `gorusme.txt` varken hiçbir şey bulamaz. İki motor da aynı davranmalı.
    /// </summary>
    [Theory]
    [InlineData("görüşme")]
    [InlineData("gorusme")]
    public void BothEnginesFindEitherSpelling(string query)
    {
        var tokenizer = new BasicTokenizer();
        var onlyFolded = Node("gorusme.txt");
        var onlyAccented = Node("görüşme.txt");

        var state = SearchState.Create([onlyFolded, onlyAccented], tokenizer);

        var standard = new SearchEngine(_ => state, tokenizer, new BasicScoringStrategy())
            .Search(query, maxResults: 10)
            .Select(result => result.FullPath)
            .ToArray();
        var advanced = new AdvancedSearchEngine(_ => state, tokenizer, new BasicScoringStrategy())
            .Search(new StructuredQuery { Keywords = [query] })
            .Select(result => result.FullPath)
            .ToArray();

        Assert.Contains(onlyFolded.FullPath, standard);
        Assert.Contains(onlyAccented.FullPath, standard);
        Assert.Contains(onlyFolded.FullPath, advanced);
        Assert.Contains(onlyAccented.FullPath, advanced);
    }

    /// <summary>
    /// Regresyonun en keskin hali: indekste yalnız aksansız yazım varsa,
    /// aksanlı sorgunun ürettiği ilk token hiçbir şey eşleştirmez. Gelişmiş
    /// motor token'ları kesiştirdiği için bu, terimin tamamını düşürür ve
    /// dosya **hiç** bulunamaz.
    /// </summary>
    [Fact]
    public void AccentedQueryFindsFoldedOnlyFileInAdvancedSearch()
    {
        var tokenizer = new BasicTokenizer();
        var onlyFolded = Node("gorusme.txt");
        var state = SearchState.Create([onlyFolded], tokenizer);

        var results = new AdvancedSearchEngine(_ => state, tokenizer, new BasicScoringStrategy())
            .Search(new StructuredQuery { Keywords = ["görüşme"] })
            .Select(result => result.FullPath)
            .ToArray();

        Assert.Equal([onlyFolded.FullPath], results);
    }

    /// <summary>
    /// Terimdeki **ayrı kelimeler** birlikte aranmaya devam etmeli; alternatif
    /// gruplaması bunu gevşetirse gelişmiş arama her şeyi eşleştirmeye başlar.
    /// </summary>
    [Fact]
    public void DistinctWordsInATermStillRequireAllOfThem()
    {
        var tokenizer = new BasicTokenizer();
        var both = Node("yıllık-görüşme.txt");
        var onlyOne = Node("aylık-görüşme.txt");
        var state = SearchState.Create([both, onlyOne], tokenizer);

        var results = new AdvancedSearchEngine(_ => state, tokenizer, new BasicScoringStrategy())
            .Search(new StructuredQuery { Keywords = ["yıllık görüşme"] })
            .Select(result => result.FullPath)
            .ToArray();

        Assert.Contains(both.FullPath, results);
        Assert.DoesNotContain(onlyOne.FullPath, results);
    }

    private static SearchState StateWith(string fileName) =>
        SearchState.Create([Node(fileName)], new BasicTokenizer());

    private static FileSystemNode Node(string fileName) =>
        new(fileName, Root + "\\" + fileName, false);
}
