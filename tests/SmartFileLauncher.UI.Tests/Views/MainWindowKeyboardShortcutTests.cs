using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SmartFileLauncher.UI.Views;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Views;

public sealed partial class MainWindowSearchRefreshTests
{
    [Theory]
    [InlineData(Key.Right, 5, 6)]
    [InlineData(Key.Right, 7, 7)]
    [InlineData(Key.Left, 4, 4)]
    [InlineData(Key.Up, 2, 2)]
    [InlineData(Key.Down, 5, 9)]
    [InlineData(Key.Down, 9, 9)]
    [InlineData(Key.Down, 7, 9)]
    [InlineData(Key.Home, 6, 0)]
    [InlineData(Key.End, 1, 9)]
    [InlineData(Key.PageDown, 1, 9)]
    [InlineData(Key.PageDown, 0, 8)]
    [InlineData(Key.PageUp, 9, 1)]
    [InlineData(Key.PageUp, 6, 2)]
    public void NavigationKeysShareOneGridGeometry(Key key, int index, int expected) =>
        Assert.Equal(expected, MainWindow.NavigationTarget(key, index, columns: 4, count: 10, pageRows: 2));

    [Fact]
    public void ListNavigationIsOneColumnWide()
    {
        Assert.Equal(3, MainWindow.NavigationTarget(Key.Down, 2, columns: 1, count: 10, pageRows: 4));
        Assert.Equal(2, MainWindow.NavigationTarget(Key.Right, 2, columns: 1, count: 10, pageRows: 4));
        Assert.Equal(6, MainWindow.NavigationTarget(Key.PageDown, 2, columns: 1, count: 10, pageRows: 4));
        Assert.Null(MainWindow.NavigationTarget(Key.A, 2, columns: 1, count: 10, pageRows: 4));
    }

    [Fact]
    public Task ShiftArrowsGrowAndShrinkARangeAndCtrlMovesTheCursorWithoutSelecting() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 6 };
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var list = ApplicationElement<ListView>(fixture, "ResultsList");
        Layout(list);
        UIElement Row(int index) => (UIElement)list.ItemContainerGenerator.ContainerFromIndex(index);
        int[] Selected() => list.SelectedItems.Cast<object>().Select(item => list.Items.IndexOf(item)).Order().ToArray();

        Press(ApplicationElement<TextBox>(fixture, "SearchBox"), Key.Down);
        Shortcut(fixture.Window, true, Key.Down, ModifierKeys.Shift, true);
        Assert.Equal([0, 1], Selected());
        Shortcut(fixture.Window, true, Key.Down, ModifierKeys.Shift, true);
        Assert.Equal([0, 1, 2], Selected());
        Shortcut(fixture.Window, true, Key.Up, ModifierKeys.Shift, true);
        Assert.Equal([0, 1], Selected());

        Shortcut(fixture.Window, true, Key.Down, ModifierKeys.Control, true);
        Shortcut(fixture.Window, true, Key.Down, ModifierKeys.Control, true);
        Assert.Equal([0, 1], Selected());
        Shortcut(fixture.Window, true, Key.Space, ModifierKeys.Control, true);
        Assert.Equal([0, 1, 3], Selected());
        Shortcut(fixture.Window, true, Key.Space, ModifierKeys.Control, true);
        Assert.Equal([0, 1], Selected());

        Press(Row(3), Key.End);
        Assert.Equal([5], Selected());
        Shortcut(fixture.Window, true, Key.Home, ModifierKeys.Shift, true);
        Assert.Equal([0, 1, 2, 3, 4, 5], Selected());
        Press(Row(0), Key.Home);
        Assert.Equal([0], Selected());
        Assert.All(fixture.Model.SearchResults, result => Assert.False(result.SelectionArriving));
    });

    [Fact]
    public Task TheSearchBoxExtendsWithShiftUpDownAndPagesWithoutTakingOverTextKeys() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 6 };
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var list = ApplicationElement<ListView>(fixture, "ResultsList");
        var search = ApplicationElement<TextBox>(fixture, "SearchBox");
        int[] Selected() => list.SelectedItems.Cast<object>().Select(item => list.Items.IndexOf(item)).Order().ToArray();

        Shortcut(fixture.Window, true, Key.Down, ModifierKeys.Shift, false);
        Assert.Equal([0], Selected());
        Shortcut(fixture.Window, true, Key.Down, ModifierKeys.Shift, false);
        Shortcut(fixture.Window, true, Key.Down, ModifierKeys.Shift, false);
        Assert.Equal([0, 1, 2], Selected());
        Press(search, Key.PageDown);
        Assert.Single(Selected());
        Assert.True(Selected()[0] > 2);

        Assert.False(Shortcut(fixture.Window, true, Key.Left, ModifierKeys.Control, false));
        Press(search, Key.Home);
        Assert.Equal([0], Selected());
    });

    [Fact]
    public Task FolderItemsTakeTheSameShortcutsFromTheSearchBox() => MainWindowNavigationTests.RunSta(async () =>
    {
        using var fixture = new MainWindowNavigationTests.Fixture();
        await fixture.Navigate(Path.Combine(@"C:\Test", "Belgeler"));
        var icons = fixture.Model.DesktopIcons;

        Shortcut(fixture.Window, false, Key.Down, ModifierKeys.Shift, false);
        Assert.True(icons[0].IsTransferSelected);
        Shortcut(fixture.Window, false, Key.Down, ModifierKeys.Shift, false);
        Assert.All(icons, icon => Assert.True(icon.IsTransferSelected));
        Assert.Equal(Visibility.Visible, fixture.Element("SelectionBar").Visibility);
        Press(fixture.SearchBox, Key.PageUp);
        Assert.True(icons[0].IsTransferSelected);
        Assert.False(icons[1].IsTransferSelected);
    });

    [Fact]
    public Task OnceAResultIsChosenTheSearchBoxHandsNavigationKeysToTheResults() => RunSta(async () =>
    {
        using var fixture = new Fixture(gridView: true) { StandardTotal = 9 };
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var list = ApplicationElement<ListView>(fixture, "ResultsList");
        var search = ApplicationElement<TextBox>(fixture, "SearchBox");
        bool Routes(Key key, ModifierKeys modifiers) => (bool)typeof(MainWindow)
            .GetMethod("SearchBoxRoutesToItems", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(fixture.Window, [true, key, modifiers])!;
        int[] Selected() => list.SelectedItems.Cast<object>().Select(item => list.Items.IndexOf(item)).Order().ToArray();

        Assert.False(Routes(Key.Right, ModifierKeys.Shift));
        Assert.False(Routes(Key.Home, ModifierKeys.None));
        Assert.False(Routes(Key.Space, ModifierKeys.Control));

        Press(search, Key.Down);
        Assert.True(Routes(Key.Right, ModifierKeys.Shift));
        Assert.True(Routes(Key.Left, ModifierKeys.Control));
        Assert.True(Routes(Key.Down, ModifierKeys.Control));
        Assert.True(Routes(Key.Home, ModifierKeys.Shift));
        Assert.True(Routes(Key.Space, ModifierKeys.Control));

        Press(search, Key.End);
        Assert.Equal([8], Selected());
        Press(search, Key.Home);
        Assert.Equal([0], Selected());
        SearchBoxShortcut(fixture.Window, Key.Right, ModifierKeys.Shift);
        SearchBoxShortcut(fixture.Window, Key.Right, ModifierKeys.Shift);
        Assert.Equal([0, 1, 2], Selected());
        SearchBoxShortcut(fixture.Window, Key.Down, ModifierKeys.Control);
        Assert.Equal([0, 1, 2], Selected());
        SearchBoxShortcut(fixture.Window, Key.Space, ModifierKeys.Control);
        Assert.Equal([0, 1, 2, 6], Selected());
    });

    [Fact]
    public Task InAListTheSearchBoxKeepsShiftAndCtrlSidewaysForText() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 3 };
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Press(ApplicationElement<TextBox>(fixture, "SearchBox"), Key.Down);
        bool Routes(Key key, ModifierKeys modifiers) => (bool)typeof(MainWindow)
            .GetMethod("SearchBoxRoutesToItems", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(fixture.Window, [true, key, modifiers])!;

        Assert.False(Routes(Key.Right, ModifierKeys.Shift));
        Assert.False(Routes(Key.Left, ModifierKeys.Control));
        Assert.True(Routes(Key.Down, ModifierKeys.Shift));
        Assert.True(Routes(Key.End, ModifierKeys.None));
    });

    private static bool SearchBoxShortcut(MainWindow window, Key key, ModifierKeys modifiers) =>
        (bool)typeof(MainWindow).GetMethod("HandleSearchBoxSelectionKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, [key, modifiers])!;

    private static void Press(UIElement target, Key key)
    {
        using var source = new HwndSource(new HwndSourceParameters("OmniSpot kısayol") { Width = 1, Height = 1, WindowStyle = 0 });
        var input = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(input);
        if (input.Handled) return;
        input.RoutedEvent = Keyboard.KeyDownEvent;
        target.RaiseEvent(input);
    }

    private static bool Shortcut(MainWindow window, bool search, Key key, ModifierKeys modifiers, bool focusOnItems) =>
        (bool)typeof(MainWindow).GetMethod("HandleSelectionKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, [search, key, modifiers, focusOnItems])!;
}
