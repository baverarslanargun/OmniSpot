using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SmartFileLauncher.UI.Views;
using Xunit;
using static SmartFileLauncher.UI.Tests.Views.MainWindowNavigationTests;

namespace SmartFileLauncher.UI.Tests.Views;

[Collection("MainWindow search")]
public sealed class MainWindowFolderKeyboardTests
{
    private static readonly string Folder = Path.Combine(@"C:\Test", "Belgeler");

    [Fact]
    public Task ArrowsAndEnterKeepWalkingIntoFoldersAfterAFolderResultIsOpened() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var search = fixture.SearchBox;

        Press(search, Key.Down);
        Press(search, Key.Enter);
        var opened = Path.Combine(@"C:\Test", "ilk-fatura");
        await WaitForFolder(fixture, opened);
        Assert.Equal("", search.Text);
        var icons = fixture.Model.DesktopIcons;

        Press(search, Key.Down);
        Assert.True(icons[0].IsTransferSelected);
        Press(search, Key.Down);
        Assert.True(icons[1].IsTransferSelected);
        Assert.False(icons[0].IsTransferSelected);
        Press(search, Key.Up);
        Assert.True(icons[0].IsTransferSelected);

        Press(search, Key.Enter);
        await WaitForFolder(fixture, Path.Combine(opened, "Muhasebe"));
        Press(search, Key.Down);
        Assert.True(fixture.Model.DesktopIcons[0].IsTransferSelected);
        Assert.Equal("", search.Text);
    });

    [Fact]
    public Task UpFromTheTopFolderItemReturnsToTheSearchBox() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Navigate(Folder);
        var icons = fixture.Model.DesktopIcons;

        Press(fixture.SearchBox, Key.Down);
        Assert.True(icons[0].IsTransferSelected);
        Press(fixture.SearchBox, Key.Up);
        Assert.DoesNotContain(icons, icon => icon.IsTransferSelected);
        Press(fixture.SearchBox, Key.Up);
        Assert.DoesNotContain(icons, icon => icon.IsTransferSelected);
        Press(fixture.SearchBox, Key.Enter);
        Assert.Equal(Folder, fixture.Model.CurrentFolderPath);
    });

    [Fact]
    public Task ArrowsMoveBetweenFocusedFolderItemsAndUpAtTheTopReturnsToTheSearchBox() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Navigate(Folder);
        Realize(fixture.Element("DesktopIconsScroll"));
        var icons = fixture.Model.DesktopIcons;
        UIElement Item(int index) => (UIElement)Call(fixture, "MainItemContainer", icons[index].FullPath)!;

        Press(fixture.SearchBox, Key.Tab);
        Assert.True(icons[0].IsTransferSelected);
        Press(Item(0), Key.Down);
        Assert.True(icons[1].IsTransferSelected);
        Assert.False(icons[0].IsTransferSelected);
        Press(Item(1), Key.Down);
        Assert.True(icons[1].IsTransferSelected);
        Press(Item(1), Key.Up);
        Assert.True(icons[0].IsTransferSelected);
        Press(Item(0), Key.Up);
        Assert.DoesNotContain(icons, icon => icon.IsTransferSelected);
    });

    [Fact]
    public Task UpFromTheFirstFileResultReturnsToTheSearchBox() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardResults = [Fixture.Result("ilk-fatura"), Fixture.Result("ikinci-fatura")] };
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var list = (ListView)fixture.Element("ResultsList");
        Realize(list);

        Press(fixture.SearchBox, Key.Down);
        Assert.Equal(0, list.SelectedIndex);
        var first = (UIElement)list.ItemContainerGenerator.ContainerFromIndex(0);
        Press(first, Key.Up);
        Assert.Null(list.SelectedItem);
        Assert.Equal(-1, list.SelectedIndex);
    });

    private static async Task WaitForFolder(Fixture fixture, string folder)
    {
        for (var i = 0; i < 200 && !string.Equals(fixture.Model.CurrentFolderPath, folder, StringComparison.OrdinalIgnoreCase); i++)
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(folder, fixture.Model.CurrentFolderPath);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void Realize(FrameworkElement element)
    {
        element.Measure(new Size(680, 500));
        element.Arrange(new Rect(0, 0, 680, 500));
        element.UpdateLayout();
    }

    private static void Press(UIElement target, Key key)
    {
        using var source = new HwndSource(new HwndSourceParameters("OmniSpot klasör tuşları") { Width = 1, Height = 1, WindowStyle = 0 });
        var input = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(input);
        if (input.Handled) return;
        input.RoutedEvent = Keyboard.KeyDownEvent;
        target.RaiseEvent(input);
    }

    private static object? Call(Fixture fixture, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(fixture.Window, args);
}
