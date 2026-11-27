using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Search;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Index;

public sealed class FileIndexLooseNameSearchTests
{
    private const ulong Root = 5;

    private static readonly string[] Names =
    [
        "cikmis_sorular", "cikmis_sorular.pdf", "Çıkmış Sorular.pdf", "çıkmış sorular.docx", "sorular.txt",
        "cikis_belgesi.pdf", "CIKMIS-SORULAR-2024.zip", "eski çıkmış.txt", "iki-kelime.txt", "iki kelime.txt",
        "kelime iki.txt", "Işık raporu.docx", "isik.txt", "ÇAĞRI.txt", "cagri.txt", "notlar.txt", "çiçek.png", "pdf-rehberi.txt"
    ];

    private static FileIndex Build()
    {
        var entries = new List<IndexEntry> { Entry(Root, Root, @"C:\", FileAttributes.Directory) };
        for (var i = 0; i < Names.Length; i++)
            entries.Add(Entry((ulong)(16 + i), Root, Names[i], i == 0 ? FileAttributes.Directory : FileAttributes.Normal));
        return FileIndex.Build(@"C:\", Root, entries);
    }

    private static ResultView Ranked(params (string FullPath, double Boost)[] opened) =>
        ResultView.SearchDefault with { LiteralRanking = new LiteralRanking(opened) };

    private static string[] Found(FileIndex index, string query, ResultView view) =>
        index.Search(query, 100, view).Select(hit => hit.Name).ToArray();

    [Theory]
    [InlineData("çıkmış")]
    [InlineData("çıkmış sorular")]
    [InlineData("ÇIKMIŞ SORULAR")]
    [InlineData("cikmis sorular")]
    [InlineData("cıkmıs")]
    [InlineData("çıkmış_sorular")]
    [InlineData("cikmis-sorular")]
    [InlineData("(çıkmış) [sorular],")]
    public void TurkishLettersAndSeparatedWordsFindTheUnderscoredName(string query)
    {
        using var index = Build();
        var ranked = Found(index, query, Ranked());
        var byName = Found(index, query, ResultView.SearchDefault with { Sort = ItemSort.NameAscending });

        Assert.Contains("cikmis_sorular", ranked);
        Assert.Contains("cikmis_sorular.pdf", ranked);
        Assert.Contains("Çıkmış Sorular.pdf", ranked);
        Assert.DoesNotContain("notlar.txt", ranked);
        Assert.DoesNotContain("cikis_belgesi.pdf", ranked);
        Assert.Equal(ranked.Order(), byName.Order());
        Assert.Equal(ranked.Length, index.CountMatches(query, Ranked(), null));
    }

    [Fact]
    public void EveryWordMustAppearInTheName()
    {
        using var index = Build();
        var found = Found(index, "çıkmış sorular", Ranked());

        Assert.DoesNotContain("sorular.txt", found);
        Assert.DoesNotContain("eski çıkmış.txt", found);
        Assert.Equal(new[] { "iki kelime.txt", "iki-kelime.txt", "kelime iki.txt" }.Order(StringComparer.Ordinal),
            Found(index, "iki kelime", Ranked()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void NameSeparatorsInTheQuerySplitWordsButTheDotStaysPartOfTheWord()
    {
        using var index = Build();

        Assert.Contains("çıkmış sorular.docx", Found(index, "cikmis_sorular", Ranked()));
        Assert.Contains("Çıkmış Sorular.pdf", Found(index, "çıkmış-sorular", Ranked()));
        Assert.Equal(new[] { "iki kelime.txt", "iki-kelime.txt", "kelime iki.txt" }.Order(StringComparer.Ordinal),
            Found(index, "iki_kelime", Ranked()).Order(StringComparer.Ordinal));

        var extension = Found(index, ".pdf", Ranked());
        Assert.Contains("cikmis_sorular.pdf", extension);
        Assert.DoesNotContain("pdf-rehberi.txt", extension);
    }

    [Theory]
    [InlineData("_", "cikmis_sorular")]
    [InlineData("-", "iki-kelime.txt")]
    [InlineData("()", null)]
    public void QueriesMadeOnlyOfSeparatorsStillSearchTheseCharacters(string query, string? expected)
    {
        using var index = Build();
        var found = Found(index, query, Ranked());

        if (expected is null) Assert.Empty(found);
        else Assert.Contains(expected, found);
        Assert.All(found, name => Assert.Contains(query, name));
        Assert.Equal(found.Length, index.CountMatches(query, Ranked(), null));
    }

    [Theory]
    [InlineData("\"çıkmış sorular\"", "Çıkmış Sorular.pdf|çıkmış sorular.docx")]
    [InlineData("\"çıkmış sorular", "Çıkmış Sorular.pdf|çıkmış sorular.docx")]
    [InlineData("\"cikmis\"", "cikmis_sorular|cikmis_sorular.pdf|CIKMIS-SORULAR-2024.zip")]
    [InlineData("sorular !çıkmış", "sorular.txt")]
    [InlineData("sorular !\"Çıkmış\"", "cikmis_sorular|cikmis_sorular.pdf|CIKMIS-SORULAR-2024.zip|sorular.txt")]
    [InlineData("kelime !iki", "")]
    [InlineData("kelime !\"iki kelime\"", "iki-kelime.txt|kelime iki.txt")]
    [InlineData("!notlar", "")]
    public void QuotesMatchExactlyAndExclamationExcludes(string query, string expected)
    {
        using var index = Build();
        var ranked = Found(index, query, Ranked());
        var byName = Found(index, query, ResultView.SearchDefault with { Sort = ItemSort.NameAscending });
        var want = expected.Split('|', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(want, ranked.Order(StringComparer.Ordinal));
        Assert.Equal(want, byName.Order(StringComparer.Ordinal));
        Assert.Equal(want.Length, index.CountMatches(query, Ranked(), null));
        Assert.Equal(ranked.Length, ranked.Distinct().Count());
    }

    [Fact]
    public void QuotedTextComesFirstAndOpenedFilesStillLead()
    {
        using var index = Build();
        var hits = index.Search("\"çıkmış sorular\"", 100, Ranked());
        Assert.All(hits, hit => Assert.True(hit.Score >= 1));

        var excluded = index.Search("sorular !\"Çıkmış\"", 100, Ranked((@"C:\sorular.txt", 0.5)));
        Assert.DoesNotContain(excluded, hit => hit.Name.Contains("Çıkmış", StringComparison.Ordinal));
        Assert.Equal("sorular.txt", excluded[0].Name);
    }

    [Fact]
    public void AsciiQueriesAlsoFindNamesWrittenWithTurkishLetters()
    {
        using var index = Build();

        Assert.Contains("Işık raporu.docx", Found(index, "isik", Ranked()));
        Assert.Contains("ÇAĞRI.txt", Found(index, "cagri", Ranked()));
        Assert.Contains("çiçek.png", Found(index, "cicek", Ranked()));
    }

    [Fact]
    public void NamesWrittenAsTypedComeBeforeLooseMatches()
    {
        using var index = Build();
        var hits = index.Search("çıkmış sorular", 100, Ranked());

        Assert.Equal(new[] { "Çıkmış Sorular.pdf", "çıkmış sorular.docx" }.Order(StringComparer.Ordinal),
            hits.Take(2).Select(hit => hit.Name).Order(StringComparer.Ordinal));
        Assert.All(hits.Take(2), hit => Assert.True(hit.Score > 0));
        Assert.All(hits.Skip(2), hit => Assert.Equal(0, hit.Score));
        Assert.True(hits.Count > 2);

        var single = index.Search("cikmis", 100, Ranked());
        var asTyped = single.Where(hit => hit.Name.StartsWith("cikmis", StringComparison.OrdinalIgnoreCase)).ToArray();
        var loose = single.Except(asTyped).ToArray();
        Assert.Equal(3, asTyped.Length);
        Assert.Contains(loose, hit => hit.Name == "Çıkmış Sorular.pdf");
        Assert.All(asTyped, hit => Assert.True(hit.Score > 0));
        Assert.All(loose, hit => Assert.Equal(0, hit.Score));
        Assert.Equal(asTyped, single.Take(asTyped.Length));
    }

    [Fact]
    public void OpenedLooseMatchesLeadTheLooseMatches()
    {
        using var index = Build();
        var hits = index.Search("çıkmış sorular", 100, Ranked((@"C:\cikmis_sorular.pdf", 0.5)));

        Assert.All(hits.Take(2), hit => Assert.True(hit.Score >= 1));
        Assert.Equal(("cikmis_sorular.pdf", 0.5), (hits[2].Name, hits[2].Score));
        Assert.All(hits.Skip(3), hit => Assert.Equal(0, hit.Score));
    }

    [Fact]
    public void PagesAndCountsCoverTheSameRowsAsOneWindow()
    {
        using var index = Build();
        foreach (var query in new[] { "çıkmış", "cikmis", "cikmis sorular", "i", "txt", " txt", "kelime ", "isik" })
        {
            var whole = index.Search(query, 0, 100, Ranked(), null).Select(hit => hit.FullPath).ToArray();
            var paged = Enumerable.Range(0, 20).SelectMany(page => index.Search(query, page * 2, 2, Ranked(), null))
                .Select(hit => hit.FullPath).ToArray();

            Assert.Equal(whole, paged);
            Assert.Equal(whole.Length, whole.Distinct().Count());
            Assert.Equal(whole.Length, index.CountMatches(query, Ranked(), null));
        }
    }

    [Theory]
    [InlineData("çıkmış")]
    [InlineData("çıkmış sorular")]
    [InlineData("sorular çıkmış")]
    [InlineData("isik")]
    [InlineData("IŞIK")]
    [InlineData("cagri txt")]
    [InlineData("ç")]
    [InlineData("kelime")]
    [InlineData("zzz")]
    [InlineData("cikmis_sorular")]
    [InlineData("iki-kelime")]
    [InlineData("_")]
    [InlineData(".pdf")]
    [InlineData("\"çıkmış\"")]
    [InlineData("sorular !çıkmış")]
    [InlineData("sorular !\"Çıkmış\"")]
    [InlineData("!notlar")]
    [InlineData("\"iki")]
    public void IndexScanAgreesWithNameCheckUsedForLiveRefresh(string query)
    {
        using var index = Build();
        var names = NameQuery.Create(query)!;
        var expected = Names.Where(names.Matches).Order().ToArray();

        Assert.Equal(expected, Found(index, query, ResultView.SearchDefault with { Sort = ItemSort.NameAscending }).Order());
    }

    private static IndexEntry Entry(ulong id, ulong parent, string name, FileAttributes attributes) =>
        new(id, parent, name, attributes, 20, DateTime.UnixEpoch.Ticks);
}
