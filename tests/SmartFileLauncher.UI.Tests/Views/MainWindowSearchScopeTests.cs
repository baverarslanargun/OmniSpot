using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Views;

public sealed partial class MainWindowSearchRefreshTests
{
    private const string ScopeFolder = @"C:\Kullanici\Projeler";

    [Fact]
    public Task SearchInAFolderStaysScopedUntilTheChipIsRemovedAndTheScopeReturnsForTheNextSearch() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 3 };
        using var keyboardSource = ApplicationKeySource();
        fixture.Model.CurrentFolderPath = ScopeFolder;
        var search = ApplicationElement<TextBox>(fixture, "SearchBox");
        var chip = ApplicationElement<Border>(fixture, "SearchScopeChip");
        var bar = ApplicationElement<Grid>(fixture, "SearchScopeBar");
        var toggle = ApplicationElement<TextBlock>(fixture, "SearchScopeToggleText");
        void Press(Key key) => SendApplicationKey(search, keyboardSource, key);

        await TypeAndWait(fixture, "fatura");
        Assert.Equal(ScopeFolder, LastScope(fixture));
        Assert.Equal(Visibility.Visible, chip.Visibility);
        Assert.Equal("Projeler", ApplicationElement<TextBlock>(fixture, "SearchScopeChipText").Text);
        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Equal("Projeler", ((Run)fixture.Window.FindName("SearchScopeBarName")).Text);
        Assert.Equal("Tüm bilgisayarda ara", toggle.Text);
        Assert.Equal("Projeler içinde ara...", ApplicationElement<TextBlock>(fixture, "SearchWatermark").Text);

        var resting = ((SolidColorBrush)chip.BorderBrush).Color;

        var calls = fixture.StandardCalls;
        search.CaretIndex = 0;
        Press(Key.Back);
        Assert.Equal("fatura", search.Text);
        Assert.NotEqual(resting, ((SolidColorBrush)chip.BorderBrush).Color);
        Assert.Equal(calls, fixture.StandardCalls);

        Press(Key.Back);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("fatura", search.Text);
        Assert.Equal(Visibility.Collapsed, chip.Visibility);
        Assert.Null(LastScope(fixture));
        Assert.Equal(Visibility.Visible, bar.Visibility);
        Assert.Equal("Yalnız Projeler içinde ara", toggle.Text);
        Assert.Equal("Tüm bilgisayarda", ((Run)fixture.Window.FindName("SearchScopeBarName")).Text);

        Click(ApplicationElement<Button>(fixture, "SearchScopeToggleButton"));
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ScopeFolder, LastScope(fixture));
        Assert.Equal(Visibility.Visible, chip.Visibility);

        Click(ApplicationElement<Button>(fixture, "SearchScopeChipClear"));
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(LastScope(fixture));

        fixture.Type("", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(Visibility.Collapsed, chip.Visibility);
        Assert.Equal(Visibility.Collapsed, bar.Visibility);
        Assert.Equal("Projeler içinde ara...", ApplicationElement<TextBlock>(fixture, "SearchWatermark").Text);

        await TypeAndWait(fixture, "rapor");
        Assert.Equal(ScopeFolder, LastScope(fixture));
        Assert.Equal(Visibility.Visible, chip.Visibility);
    });

    [Fact]
    public Task AnEmptyScopedSearchOffersToSearchEverywhere() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 0 };
        fixture.Model.CurrentFolderPath = ScopeFolder;
        var widen = ApplicationElement<Button>(fixture, "NoResultsWidenButton");
        var hint = ApplicationElement<TextBlock>(fixture, "NoResultsHint");

        await TypeAndWait(fixture, "yok");
        Assert.Equal(Visibility.Visible, ApplicationElement<StackPanel>(fixture, "NoResultsPanel").Visibility);
        Assert.Equal(Visibility.Visible, widen.Visibility);
        Assert.Equal("Projeler ve alt klasörlerinde eşleşme yok", hint.Text);

        Click(widen);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Null(LastScope(fixture));
        Assert.Equal(Visibility.Collapsed, widen.Visibility);
        Assert.Equal("Farklı anahtar kelimeler deneyin", hint.Text);
    });

    [Fact]
    public Task SearchFromHomeIsNotScopedAndShowsNoScopeChrome() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 2 };
        await TypeAndWait(fixture, "fatura");
        Assert.Null(LastScope(fixture));
        Assert.Equal(Visibility.Collapsed, ApplicationElement<Border>(fixture, "SearchScopeChip").Visibility);
        Assert.Equal(Visibility.Collapsed, ApplicationElement<Grid>(fixture, "SearchScopeBar").Visibility);
        Assert.Equal("Uygulama, dosya veya klasör ara...", ApplicationElement<TextBlock>(fixture, "SearchWatermark").Text);
    });

    [Theory]
    [InlineData(355, 78, 90)]
    [InlineData(600, 132, 280)]
    [InlineData(900, 160, 520)]
    public Task ALongFolderNameLeavesRoomForTheTypedText(double width, double chipTextLimit, double textRoom) => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 1 };
        fixture.Model.CurrentFolderPath = @"C:\Kullanici\baver.tr_inspect_element_IGNORE";
        await TypeAndWait(fixture, "baver");
        var layout = ApplicationElement<Grid>(fixture, "SearchBarLayout");

        for (var pass = 0; pass < 2; pass++)
        {
            layout.Measure(new Size(width, 40));
            layout.Arrange(new Rect(0, 0, width, 40));
            layout.UpdateLayout();
        }

        var chipText = ApplicationElement<TextBlock>(fixture, "SearchScopeChipText");
        Assert.Equal(Visibility.Visible, ApplicationElement<Border>(fixture, "SearchScopeChip").Visibility);
        Assert.Equal(chipTextLimit, chipText.MaxWidth);
        Assert.True(chipText.ActualWidth <= chipTextLimit);
        var room = ApplicationElement<TextBox>(fixture, "SearchBox").ActualWidth;
        Assert.True(room >= textRoom, $"Arama metnine {room} DIP kaldı. layout={layout.ActualWidth} cols={string.Join("/", layout.ColumnDefinitions.Select(c => c.ActualWidth))} chip={ApplicationElement<Border>(fixture, "SearchScopeChip").ActualWidth} text={chipText.ActualWidth}");
    });

    private static async Task TypeAndWait(Fixture fixture, string text)
    {
        fixture.Type(text, naturalLanguage: false);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static string? LastScope(Fixture fixture)
    {
        lock (fixture.StandardScopes) return fixture.StandardScopes[^1];
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
}
