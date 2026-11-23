using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SmartFileLauncher.UI.Views;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Views;

public sealed partial class MainWindowSearchRefreshTests
{
    [Fact]
    public void TheMarqueeKeepsTheEarlierSelectionAndAddsWhatItTouches()
    {
        object a = "a", b = "b", c = "c";
        var bounds = new Dictionary<object, Rect>
        {
            [a] = new Rect(10, 10, 100, 100),
            [b] = new Rect(130, 10, 100, 100),
            [c] = new Rect(10, 130, 100, 100)
        };

        Assert.Equal([a, b], MainWindow.MarqueeChoice(new Rect(0, 0, 140, 50), bounds, []).OrderBy(item => (string)item));
        Assert.Equal([a, c], MainWindow.MarqueeChoice(new Rect(0, 0, 20, 20), bounds, [c]).OrderBy(item => (string)item));
        Assert.Empty(MainWindow.MarqueeChoice(new Rect(115, 115, 10, 10), bounds, []));
    }

    [Fact]
    public Task DraggingFromEmptySpaceSelectsTheCardsInsideTheRectangleLive() => RunSta(async () =>
    {
        using var fixture = new Fixture(gridView: true) { StandardTotal = 8 };
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Layout(Assert.IsAssignableFrom<FrameworkElement>(fixture.Window.FindName("ResultsGridScroll")));
        var viewer = Assert.IsAssignableFrom<ScrollViewer>(Call(fixture.Window, "SearchResultsScroll"));
        var results = fixture.Model.SearchResults;
        Point Center(int index)
        {
            var card = Assert.IsAssignableFrom<FrameworkElement>(Call(fixture.Window, "FileSearchFocusTarget", index));
            return card.TranslatePoint(new Point(card.ActualWidth / 2, card.ActualHeight / 2), viewer);
        }
        bool[] Selected() => results.Select(result => result.IsTransferSelected).ToArray();

        Call(fixture.Window, "PrepareMarquee", true, viewer, new Point(1, 1), false);
        Call(fixture.Window, "DragMarqueeTo", Center(1));
        Assert.Equal([true, true, false, false, false, false, false, false], Selected());

        Call(fixture.Window, "DragMarqueeTo", Center(5));
        Assert.Equal([true, true, false, false, true, true, false, false], Selected());

        Call(fixture.Window, "DragMarqueeTo", Center(0));
        Assert.Equal([true, false, false, false, false, false, false, false], Selected());
        Call(fixture.Window, "EndMarquee");

        Call(fixture.Window, "PrepareMarquee", true, viewer, Center(7), true);
        Call(fixture.Window, "DragMarqueeTo", Center(6));
        Assert.Equal([true, false, false, false, false, false, true, true], Selected());
        Assert.Equal(Visibility.Visible, ApplicationElement<Border>(fixture, "SelectionBar").Visibility);
        Call(fixture.Window, "EndMarquee");
    });

    [Fact]
    public Task PressingEmptySpaceInsideTheItemsDoesNotMoveTheWindow() => RunSta(async () =>
    {
        using var fixture = new Fixture(gridView: true) { StandardTotal = 8 };
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Layout(Assert.IsAssignableFrom<FrameworkElement>(fixture.Window.FindName("ResultsGridScroll")));
        var viewer = Assert.IsAssignableFrom<ScrollViewer>(Call(fixture.Window, "SearchResultsScroll"));
        bool DragAllowed() => (bool)typeof(MainWindow)
            .GetProperty("WindowDragAllowed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Window)!;

        Assert.True(DragAllowed());
        Assert.False((bool)Call(fixture.Window, "ArmMarqueeAt", true, viewer, new Point(viewer.ViewportWidth + 4, 4), new Point(0, 0), false)!);
        Assert.True(DragAllowed());

        Assert.True((bool)Call(fixture.Window, "ArmMarqueeAt", true, viewer, new Point(4, 4), new Point(0, 0), false)!);
        Assert.False(DragAllowed());
        Call(fixture.Window, "EndMarquee");
        Assert.True(DragAllowed());

        Call(fixture.Window, "ArmMarqueeAt", true, viewer, new Point(4, 4), new Point(0, 0), false);
        Call(fixture.Window, "DragMarqueeTo", new Point(40, 40));
        Assert.False(DragAllowed());
        Call(fixture.Window, "EndMarquee");
        Assert.True(DragAllowed());
    });

    [Fact]
    public Task FolderRowsCanBeSelectedWithTheMarqueeToo() =>MainWindowNavigationTests.RunSta(async () =>
    {
        using var fixture = new MainWindowNavigationTests.Fixture();
        await fixture.Navigate(Path.Combine(@"C:\Test", "Belgeler"));
        var host = fixture.Element("DesktopIconsScroll");
        host.Measure(new Size(680, 500));
        host.Arrange(new Rect(0, 0, 680, 500));
        host.UpdateLayout();
        var viewer = Assert.IsAssignableFrom<ScrollViewer>(typeof(MainWindow)
            .GetProperty("DesktopScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Window));

        Call(fixture.Window, "PrepareMarquee", false, viewer, new Point(1, 1), false);
        Call(fixture.Window, "DragMarqueeTo", new Point(viewer.ViewportWidth - 1, viewer.ViewportHeight - 1));
        Assert.All(fixture.Model.DesktopIcons, icon => Assert.True(icon.IsTransferSelected));
        Assert.Equal(Visibility.Visible, fixture.Element("SelectionBar").Visibility);
        Call(fixture.Window, "EndMarquee");
    });

    private static object? Call(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
}
