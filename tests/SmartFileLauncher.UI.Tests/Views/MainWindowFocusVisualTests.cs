using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SmartFileLauncher.UI.Views;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Views;

public sealed partial class MainWindowSearchRefreshTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FocusedFileResultsShowFocusWithTheirOwnBorderInsteadOfADottedRectangle(bool gridView) => RunSta(async () =>
    {
        using var fixture = new Fixture(gridView: gridView) { StandardTotal = 3 };
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var host = Assert.IsAssignableFrom<FrameworkElement>(
            fixture.Window.FindName(gridView ? "ResultsGridScroll" : "ResultsList"));
        Layout(host);

        var focusTarget = typeof(MainWindow).GetMethod("FileSearchFocusTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var index = 0; index < fixture.Model.SearchResults.Count; index++)
            AssertOwnFocusIndicator(Assert.IsAssignableFrom<Control>(focusTarget.Invoke(fixture.Window, [index])));
    });

    [Theory]
    [InlineData("DesktopCardTemplate")]
    [InlineData("DesktopRowTemplate")]
    public Task FolderItemsShowFocusWithTheirOwnBorderInsteadOfADottedRectangle(string template) => RunSta(() =>
    {
        using var fixture = new Fixture();
        var item = Assert.IsType<Button>(((DataTemplate)fixture.Window.FindResource(template)).LoadContent());
        AssertOwnFocusIndicator(item);
        return Task.CompletedTask;
    });

    private static void AssertOwnFocusIndicator(Control item)
    {
        Assert.Null(item.FocusVisualStyle);
        Assert.Contains(item.Template.Triggers.OfType<Trigger>(),
            trigger => trigger.Property == UIElement.IsKeyboardFocusedProperty && true.Equals(trigger.Value));
    }
}
