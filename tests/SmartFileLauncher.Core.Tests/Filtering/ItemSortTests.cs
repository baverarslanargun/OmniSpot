using SmartFileLauncher.Core.Filtering;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Filtering;

public sealed class ItemSortTests
{
    private static readonly DateTime Base = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void RelevanceOrdersHigherScoreFirst()
    {
        var sort = ItemSort.Relevance;

        Assert.True(sort.Compare(Row("a", score: 10), Row("b", score: 5)) < 0);
        Assert.True(sort.Compare(Row("a", score: 5), Row("b", score: 10)) > 0);
    }

    [Fact]
    public void NameSortIgnoresScoreAndCase()
    {
        var sort = new ItemSort(SortField.Name, Descending: false);

        Assert.True(sort.Compare(Row("alfa.txt", score: 1), Row("Beta.txt", score: 100)) < 0);
    }

    [Fact]
    public void DescendingFlipsThePrimaryKeyOnly()
    {
        var ascending = new ItemSort(SortField.Name, Descending: false);
        var descending = new ItemSort(SortField.Name, Descending: true);
        var left = Row("alfa.txt");
        var right = Row("beta.txt");

        Assert.True(ascending.Compare(left, right) < 0);
        Assert.True(descending.Compare(left, right) > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingSizeGoesLastInBothDirections(bool descending)
    {
        var sort = new ItemSort(SortField.Size, descending);
        var withSize = Row("dosya.txt", size: 100);
        var withoutSize = Row("klasör", size: null, isDirectory: true);

        Assert.True(sort.Compare(withSize, withoutSize) < 0);
        Assert.True(sort.Compare(withoutSize, withSize) > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingDateGoesLastInBothDirections(bool descending)
    {
        var sort = new ItemSort(SortField.Modified, descending);
        var dated = Row("dosya.txt", modified: Base);
        var undated = Row("klasör", modified: null, isDirectory: true);

        Assert.True(sort.Compare(dated, undated) < 0);
        Assert.True(sort.Compare(undated, dated) > 0);
    }

    [Fact]
    public void EqualKeysFallBackToScoreThenName()
    {
        var sort = new ItemSort(SortField.Modified, Descending: true);
        var strong = Row("beta.txt", modified: Base, score: 100);
        var weak = Row("alfa.txt", modified: Base, score: 10);

        Assert.True(sort.Compare(strong, weak) < 0);

        var tiedScore = new ItemSort(SortField.Size, Descending: true);
        Assert.True(
            tiedScore.Compare(
                Row("alfa.txt", size: 10, score: 5),
                Row("beta.txt", size: 10, score: 5)) < 0);
    }

    [Fact]
    public void KindSortUsesTheExtensionAndTreatsFoldersAsEmpty()
    {
        var sort = new ItemSort(SortField.Kind, Descending: false);

        Assert.True(sort.Compare(Row("a.docx"), Row("b.txt")) < 0);
        Assert.True(sort.Compare(Row("klasör", isDirectory: true), Row("a.docx")) < 0);
    }

    [Theory]
    [InlineData(SortField.Name, false)]
    [InlineData(SortField.Kind, false)]
    [InlineData(SortField.Modified, true)]
    [InlineData(SortField.Size, true)]
    [InlineData(SortField.Relevance, true)]
    public void DefaultDirectionMatchesWhatTheFieldMeans(SortField field, bool expected)
    {
        Assert.Equal(expected, ItemSort.DefaultDescending(field));
    }

    [Fact]
    public void FolderAndSearchDefaultsDiffer()
    {
        Assert.Equal(SortField.Relevance, ResultView.SearchDefault.Sort.Field);
        Assert.Equal(SortField.Name, ResultView.FolderDefault.Sort.Field);
        Assert.False(ResultView.FolderDefault.Sort.Descending);
    }

    private static SortRow Row(
        string name,
        long? size = 1024,
        DateTime? modified = null,
        double score = 0,
        bool isDirectory = false) =>
        new(name, $@"C:\Is\{name}", isDirectory, size, modified ?? Base, score);
}
