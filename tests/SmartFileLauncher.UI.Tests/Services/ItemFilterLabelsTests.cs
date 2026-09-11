using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class ItemFilterLabelsTests
{
    [Fact]
    public void InactiveFilterProducesNoBadge()
    {
        Assert.Empty(ItemFilterLabels.Parts(ItemFilter.None));
        Assert.Equal(string.Empty, ItemFilterLabels.Badge(ItemFilter.None));
        Assert.Equal(string.Empty, ItemFilterLabels.Tooltip(ItemFilter.None));
    }

    [Fact]
    public void SingleConditionBadgeShowsTheConditionItself()
    {
        var filter = new ItemFilter(Categories: FileFilterCategory.Image);

        Assert.Equal("Görsel", ItemFilterLabels.Badge(filter));
    }

    [Fact]
    public void MultipleConditionsCollapseIntoACounter()
    {
        var filter = new ItemFilter(
            Categories: FileFilterCategory.Image | FileFilterCategory.Video,
            Date: FilterDateRange.LastWeek,
            Kind: FilterItemKind.FilesOnly);

        Assert.Equal(
            new[] { "Yalnız dosyalar", "Görsel", "Video", "Son 7 gün" },
            ItemFilterLabels.Parts(filter));
        Assert.Equal("Yalnız dosyalar +3", ItemFilterLabels.Badge(filter));
        Assert.Contains("Görsel, Video", ItemFilterLabels.Tooltip(filter));
    }

    [Fact]
    public void SizeUsesShortLabelInTheBadgeAndRangeInTheMenu()
    {
        var filter = new ItemFilter(Size: FilterSizeRange.Medium);

        Assert.Equal("Orta", ItemFilterLabels.Badge(filter));
        Assert.Equal("Orta (1-100 MB)", ItemFilterLabels.Size(FilterSizeRange.Medium));
    }

    [Fact]
    public void ChipsCarryTheValueNeededToRemoveASingleCondition()
    {
        var filter = new ItemFilter(
            Categories: FileFilterCategory.Image | FileFilterCategory.Video,
            Date: FilterDateRange.Today);

        var chips = ItemFilterLabels.Chips(filter);
        var video = chips.Single(chip => chip.Label == "Video");
        var without = ItemFilterLabels.Without(filter, video.Value);

        Assert.Equal(3, chips.Count);
        Assert.Equal(FileFilterCategory.Image, without.Categories);
        Assert.Equal(FilterDateRange.Today, without.Date);
    }

    [Fact]
    public void RemovingTheLastConditionLeavesAnInactiveFilter()
    {
        var filter = new ItemFilter(Date: FilterDateRange.LastWeek);
        var chip = Assert.Single(ItemFilterLabels.Chips(filter));

        var without = ItemFilterLabels.Without(filter, chip.Value);

        Assert.False(without.IsActive);
    }

    [Theory]
    [InlineData(SortField.Relevance, "İlgi düzeyi")]
    [InlineData(SortField.Name, "Ad")]
    [InlineData(SortField.Modified, "Değiştirilme tarihi")]
    [InlineData(SortField.Size, "Boyut")]
    [InlineData(SortField.Kind, "Tür")]
    public void SortFieldsHaveTurkishLabels(SortField field, string expected)
    {
        Assert.Equal(expected, ItemFilterLabels.SortField(field));
    }
}
