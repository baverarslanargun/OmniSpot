using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.Views;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Views;

public sealed partial class MainWindowSearchRefreshTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task FileSearchKeysSelectAndOpenOnlyCurrentResults(bool gridView, bool hasStartMenu) => RunSta(async () =>
    {
        var files = new StartMenuTestFileOperations();
        var shell = hasStartMenu ? new StartMenuTestShell(new StartMenuApplication("writer", "Writer", "")) : null;
        using var fixture = new Fixture(gridView: gridView, startMenuShell: shell, fileOperations: files)
            { StandardTotal = 6 };
        using var keyboardSource = ApplicationKeySource();
        if (hasStartMenu) await ReadStartMenuApplications(fixture);
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        var search = ApplicationElement<TextBox>(fixture, "SearchBox");
        var list = ApplicationElement<ListView>(fixture, "ResultsList");
        var results = fixture.Model.SearchResults;
        Assert.Null(list.SelectedItem);

        void Press(Key key) => SendApplicationKey(search, keyboardSource, key);

        Press(Key.Left);
        Press(Key.Right);
        Assert.Null(list.SelectedItem);
        Press(Key.Down);
        Assert.Same(results[0], list.SelectedItem);
        Press(Key.Up);
        Assert.Null(list.SelectedItem);
        Assert.False(results[0].IsTransferSelected);
        Press(Key.Down);
        Assert.Same(results[0], list.SelectedItem);

        if (gridView)
        {
            Press(Key.Right);
            Assert.Same(results[1], list.SelectedItem);
            Press(Key.Right);
            Press(Key.Right);
            Press(Key.Right);
            Assert.Same(results[3], list.SelectedItem);
            Press(Key.Down);
            Press(Key.Down);
            Assert.Same(results[5], list.SelectedItem);
            Press(Key.Up);
            Assert.Same(results[1], list.SelectedItem);
            Press(Key.Down);
            Press(Key.Left);
            Press(Key.Left);
        }
        else
        {
            Press(Key.Right);
            Assert.Same(results[0], list.SelectedItem);
            for (var i = 0; i < 6; i++) Press(Key.Down);
            Assert.Same(results[5], list.SelectedItem);
            Press(Key.Up);
        }
        var selected = results[4];
        Assert.Same(selected, list.SelectedItem);
        Assert.True(selected.IsTransferSelected);
        Press(Key.Enter);
        Assert.Equal([selected.FullPath], files.Opened);

        fixture.Type("faturalar", naturalLanguage: false);
        Assert.Null(list.SelectedItem);
        Assert.False(selected.IsTransferSelected);
        Press(Key.Down);
        Press(Key.Tab);
        Press(Key.Enter);
        Assert.Null(list.SelectedItem);
        Assert.Equal([selected.FullPath], files.Opened);

        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(selected, results[4]);
        Assert.Null(list.SelectedItem);
        Press(Key.Enter);
        Assert.Equal([selected.FullPath, results[0].FullPath], files.Opened);

        fixture.StandardTotal = 0;
        fixture.Type("eşleşme yok", naturalLanguage: false);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Press(Key.Down);
        Press(Key.Enter);
        Assert.Null(list.SelectedItem);
        Assert.Equal(2, files.Opened.Count);
    });

    [Fact]
    public Task SearchGridCanMoveUpFromAnyFirstRowColumnToTheApplications() => RunSta(async () =>
    {
        var shell = new StartMenuTestShell(new StartMenuApplication("writer", "Writer", ""));
        using var fixture = new Fixture(gridView: true, startMenuShell: shell) { StandardTotal = 6 };
        using var keyboardSource = ApplicationKeySource();
        await ReadStartMenuApplications(fixture);
        await fixture.Search("Writer", naturalLanguage: false);

        var search = ApplicationElement<TextBox>(fixture, "SearchBox");
        void Press(Key key) => SendApplicationKey(search, keyboardSource, key);

        Press(Key.Down);
        Press(Key.Right);
        var list = ApplicationElement<ListView>(fixture, "ResultsList");
        Assert.Equal(1, list.SelectedIndex);
        Press(Key.Up);
        Assert.Null(list.SelectedItem);
        Assert.Equal("writer", ApplicationList(fixture).SelectedValue);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TabWalksForwardThroughFileResultsAndShiftTabWalksBack(bool gridView) => RunSta(async () =>
    {
        using var fixture = new Fixture(gridView: gridView) { StandardTotal = 6 };
        using var keyboardSource = ApplicationKeySource();
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        var search = ApplicationElement<TextBox>(fixture, "SearchBox");
        var list = ApplicationElement<ListView>(fixture, "ResultsList");
        var host = gridView ? (UIElement)ApplicationElement<ListBox>(fixture, "ResultsGrid") : list;
        var step = typeof(MainWindow).GetMethod("StepFileSearchFocus", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Back() => step.Invoke(fixture.Window, [-1]);

        SendApplicationKey(search, keyboardSource, Key.Tab);
        Assert.Equal(0, list.SelectedIndex);
        SendApplicationKey(host, keyboardSource, Key.Tab);
        SendApplicationKey(host, keyboardSource, Key.Tab);
        Assert.Equal(2, list.SelectedIndex);
        Assert.True(fixture.Model.SearchResults[2].IsTransferSelected);
        Assert.False(fixture.Model.SearchResults[0].IsTransferSelected);

        Back();
        Assert.Equal(1, list.SelectedIndex);
        for (var i = 0; i < 8; i++) SendApplicationKey(host, keyboardSource, Key.Tab);
        Assert.Equal(5, list.SelectedIndex);
        for (var i = 0; i < 8; i++) Back();
        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal([fixture.Model.SearchResults[0]], list.SelectedItems.Cast<object>());
    });

    [Theory]
    [InlineData(false, typeof(ListViewItem))]
    [InlineData(true, typeof(Button))]
    public Task EveryRealizedFileHasItsOwnSearchFocusTarget(bool gridView, Type targetType) => RunSta(async () =>
    {
        using var fixture = new Fixture(gridView: gridView) { StandardTotal = 6 };
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        var host = Assert.IsAssignableFrom<FrameworkElement>(
            fixture.Window.FindName(gridView ? "ResultsGridScroll" : "ResultsList"));
        Layout(host);

        var focusTarget = typeof(MainWindow).GetMethod("FileSearchFocusTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var index = 0; index < fixture.Model.SearchResults.Count; index++)
        {
            var target = Assert.IsAssignableFrom<FrameworkElement>(focusTarget.Invoke(fixture.Window, [index]));
            Assert.IsType(targetType, target);
            Assert.True(target.Focusable);
            Assert.Same(fixture.Model.SearchResults[index], target.DataContext);
        }
    });
}
