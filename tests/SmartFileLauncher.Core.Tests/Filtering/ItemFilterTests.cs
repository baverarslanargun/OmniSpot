using SmartFileLauncher.Core.Filtering;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Filtering;

public sealed class ItemFilterTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 14, 0, 0, DateTimeKind.Local);

    [Fact]
    public void EmptyFilterIsNotActiveAndKeepsEverything()
    {
        var filter = ItemFilter.None;

        Assert.False(filter.IsActive);
        Assert.True(filter.Matches("rapor.pdf", false, 10, Now, Now));
        Assert.True(filter.Matches("klasör", true, null, null, Now));
    }

    [Theory]
    [InlineData("foto.png", true)]
    [InlineData("foto.HEIC", true)]
    [InlineData("rapor.pdf", false)]
    [InlineData("uzantısız", false)]
    public void CategoryFilterMatchesExtensions(string name, bool expected)
    {
        var filter = new ItemFilter(Categories: FileFilterCategory.Image);

        Assert.Equal(expected, filter.Matches(name, false, 10, Now, Now));
    }

    [Fact]
    public void CategoryFilterAcceptsAnyOfTheSelectedCategories()
    {
        var filter = new ItemFilter(
            Categories: FileFilterCategory.Image | FileFilterCategory.Document);

        Assert.True(filter.Matches("foto.png", false, 10, Now, Now));
        Assert.True(filter.Matches("rapor.pdf", false, 10, Now, Now));
        Assert.False(filter.Matches("kur.exe", false, 10, Now, Now));
    }

    [Fact]
    public void TypeDateAndSizeFiltersLeaveFoldersVisible()
    {
        var filter = new ItemFilter(
            Categories: FileFilterCategory.Image,
            Date: FilterDateRange.Today,
            Size: FilterSizeRange.Large);

        Assert.True(filter.Matches("Belgeler", true, null, null, Now));
    }

    [Theory]
    [InlineData(FilterItemKind.FilesOnly, false)]
    [InlineData(FilterItemKind.FoldersOnly, true)]
    public void KindFilterIsTheOnlyOneThatHidesFolders(FilterItemKind kind, bool folderVisible)
    {
        var filter = new ItemFilter(Kind: kind);

        Assert.Equal(folderVisible, filter.Matches("Belgeler", true, null, null, Now));
        Assert.Equal(!folderVisible, filter.Matches("rapor.pdf", false, 10, Now, Now));
    }

    [Theory]
    [InlineData(FilterSizeRange.Small, 1024, true)]
    [InlineData(FilterSizeRange.Small, ItemFilter.SmallMaxBytes, false)]
    [InlineData(FilterSizeRange.Medium, ItemFilter.SmallMaxBytes, true)]
    [InlineData(FilterSizeRange.Medium, ItemFilter.MediumMaxBytes, true)]
    [InlineData(FilterSizeRange.Large, ItemFilter.MediumMaxBytes, false)]
    [InlineData(FilterSizeRange.Large, ItemFilter.MediumMaxBytes + 1, true)]
    public void SizeFilterUsesInclusiveMiddleBucket(FilterSizeRange size, long bytes, bool expected)
    {
        var filter = new ItemFilter(Size: size);

        Assert.Equal(expected, filter.Matches("veri.bin", false, bytes, Now, Now));
    }

    [Fact]
    public void SizeFilterDropsFilesWithoutSizeMetadata()
    {
        var filter = new ItemFilter(Size: FilterSizeRange.Small);

        Assert.False(filter.Matches("veri.bin", false, null, Now, Now));
    }

    [Theory]
    [InlineData(FilterDateRange.Today, 0, true)]
    [InlineData(FilterDateRange.Today, -1, false)]
    [InlineData(FilterDateRange.LastWeek, -6, true)]
    [InlineData(FilterDateRange.LastWeek, -8, false)]
    [InlineData(FilterDateRange.LastMonth, -29, true)]
    [InlineData(FilterDateRange.LastMonth, -31, false)]
    [InlineData(FilterDateRange.LastYear, -300, true)]
    [InlineData(FilterDateRange.LastYear, -400, false)]
    public void DateFilterUsesRollingWindows(FilterDateRange range, int dayOffset, bool expected)
    {
        var filter = new ItemFilter(Date: range);

        Assert.Equal(
            expected,
            filter.Matches("rapor.pdf", false, 10, Now.AddDays(dayOffset), Now));
    }

    [Fact]
    public void DateFilterDropsFilesWithoutDateMetadata()
    {
        var filter = new ItemFilter(Date: FilterDateRange.Today);

        Assert.False(filter.Matches("rapor.pdf", false, 10, null, Now));
    }

    [Fact]
    public void ConditionsCombineWithAnd()
    {
        var filter = new ItemFilter(
            Categories: FileFilterCategory.Image,
            Date: FilterDateRange.Today,
            Size: FilterSizeRange.Small,
            Kind: FilterItemKind.FilesOnly);

        Assert.True(filter.Matches("foto.png", false, 1024, Now, Now));
        Assert.False(filter.Matches("foto.png", false, 1024, Now.AddDays(-2), Now));
        Assert.False(filter.Matches("foto.png", false, ItemFilter.MediumMaxBytes, Now, Now));
        Assert.False(filter.Matches("rapor.pdf", false, 1024, Now, Now));
    }

    [Fact]
    public void CategoryToggleAddsAndRemovesTheSameCategory()
    {
        var withImage = ItemFilter.None.WithCategoryToggled(FileFilterCategory.Image);
        var withBoth = withImage.WithCategoryToggled(FileFilterCategory.Video);
        var backToVideo = withBoth.WithCategoryToggled(FileFilterCategory.Image);

        Assert.Equal(FileFilterCategory.Image, withImage.Categories);
        Assert.Equal(FileFilterCategory.Image | FileFilterCategory.Video, withBoth.Categories);
        Assert.Equal(FileFilterCategory.Video, backToVideo.Categories);
        Assert.Equal(ItemFilter.None, backToVideo.WithCategoryToggled(FileFilterCategory.Video));
    }
}
