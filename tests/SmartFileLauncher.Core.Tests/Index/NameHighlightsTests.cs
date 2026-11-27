using SmartFileLauncher.Core.Index;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Index;

public sealed class NameHighlightsTests
{
    [Theory]
    [InlineData("cikmis_sorular.pdf", "çıkmış", "0:6")]
    [InlineData("Çıkmış Sorular.pdf", "cikmis sorular", "0:6 7:7")]
    [InlineData("Çıkmış Sorular.pdf", "\"çıkmış\"", "0:6")]
    [InlineData("cikmis.pdf", "\"çıkmış\"", "")]
    [InlineData("rapor taslak.docx", "rapor !taslak", "0:5")]
    [InlineData("faturafatura.pdf", "fatura", "0:12")]
    [InlineData("Açik.txt", "acik", "0:5")]
    [InlineData("Işık raporu.docx", "isik", "0:4")]
    [InlineData("İSTANBUL.jpg", "istanbul", "0:8")]
    [InlineData("notlar.txt", "", "")]
    [InlineData("notlar.txt", "zzz", "")]
    public void RangesFollowTheSameRulesAsTheSearch(string name, string query, string expected)
    {
        var ranges = NameHighlights.Find(name, query).Select(range => $"{range.Start}:{range.Length}");
        Assert.Equal(expected, string.Join(' ', ranges));
    }

    [Theory]
    [InlineData("çıkmış sorular")]
    [InlineData("cikmis_sorular")]
    [InlineData("\"Çıkmış\" !taslak")]
    [InlineData("isik")]
    public void EveryNameTheIndexFindsHasSomethingHighlighted(string query)
    {
        string[] names = ["cikmis_sorular", "Çıkmış Sorular.pdf", "çıkmış sorular taslak.docx", "Işık raporu.docx", "notlar.txt"];
        var check = NameQuery.Create(query)!;
        foreach (var name in names.Where(check.Matches))
            Assert.NotEmpty(NameHighlights.Find(name, query));
    }
}
