using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.UI.Views;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Views;

public sealed partial class MainWindowSearchRefreshTests
{
    [Fact]
    public Task GridCardsShowTheFolderOnlyWhenTheNameIsSharedAndAlwaysOfferTheFullPath() => RunSta(async () =>
    {
        using var fixture = new Fixture(gridView: true) { StandardTotal = 3 };
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var host = Assert.IsAssignableFrom<FrameworkElement>(fixture.Window.FindName("ResultsGridScroll"));
        Layout(host);
        var focusTarget = typeof(MainWindow).GetMethod("FileSearchFocusTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Button Card(int index) => Assert.IsType<Button>(focusTarget.Invoke(fixture.Window, [index]));
        TextBlock Location(int index) => Descendants(Card(index)).OfType<TextBlock>().Single(text => text.Name == "ResultLocation");

        var results = fixture.Model.SearchResults;
        Assert.All(results, result => Assert.Null(result.Location));
        Assert.Equal(Visibility.Collapsed, Location(0).Visibility);
        Assert.Equal(results[0].FullPath, Card(0).ToolTip);

        results[1].Location = "Arsiv";
        Assert.Equal(Visibility.Visible, Location(1).Visibility);
        Assert.Equal("Arsiv", Location(1).Text);
        Assert.Equal(Visibility.Collapsed, Location(2).Visibility);
    });

    [Fact]
    public Task SearchResultsSharingANameGetTheirFolderAfterTheSearch() => MainWindowNavigationTests.RunSta(async () =>
    {
        using var fixture = new MainWindowNavigationTests.Fixture
        {
            StandardResults =
            [
                new SearchResult { Name = "baver", FullPath = @"C:\Test\Memduh\baver", Score = 1 },
                new SearchResult { Name = "Baver", FullPath = @"C:\Test\Ortak\Baver", Score = 1, IsDirectory = true },
                new SearchResult { Name = "baver.tr", FullPath = @"C:\Test\Site\baver.tr", Score = 1, IsDirectory = true }
            ]
        };
        await fixture.Search("baver", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Equal(["Memduh", "Ortak", null], fixture.Model.SearchResults.Select(result => result.Location));
    });

    [Fact]
    public Task AGlidingSelectionLightsUpTheCardOnlyWhenItArrives() => RunSta(async () =>
    {
        using var fixture = new Fixture(gridView: true) { StandardTotal = 2 };
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Layout(Assert.IsAssignableFrom<FrameworkElement>(fixture.Window.FindName("ResultsGridScroll")));
        var focusTarget = typeof(MainWindow).GetMethod("FileSearchFocusTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var card = Assert.IsType<Button>(focusTarget.Invoke(fixture.Window, [1]));
        var border = Assert.IsType<Border>(card.Template.FindName("CardBorder", card));
        var result = fixture.Model.SearchResults[1];
        Color Ink() => ((SolidColorBrush)border.BorderBrush).Color;

        result.SelectionArriving = true;
        result.IsTransferSelected = true;
        Assert.NotEqual(Color.FromRgb(0x00, 0x7A, 0xFF), Ink());

        result.SelectionArriving = false;
        Assert.Equal(Color.FromRgb(0x00, 0x7A, 0xFF), Ink());
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
