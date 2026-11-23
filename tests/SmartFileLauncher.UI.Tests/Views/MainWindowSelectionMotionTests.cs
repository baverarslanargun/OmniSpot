using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.ViewModels;
using SmartFileLauncher.UI.Views;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Views;

public sealed partial class MainWindowSearchRefreshTests
{
    private static readonly Color SelectionInk = Color.FromRgb(0x00, 0x7A, 0xFF);
    private static readonly Color SelectionWash = Color.FromRgb(0xEF, 0xF6, 0xFF);

    [Fact]
    public Task ListResultsUseTheSameLightSelectionAsCardsAndWaitForTheGlide() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 3 };
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var list = ApplicationElement<ListView>(fixture, "ResultsList");
        Layout(list);
        var row = Assert.IsType<ListViewItem>(list.ItemContainerGenerator.ContainerFromIndex(1));
        var border = Assert.IsType<Border>(row.Template.FindName("border", row));
        var result = fixture.Model.SearchResults[1];

        result.SelectionArriving = true;
        list.SelectedIndex = 1;
        Assert.True(result.IsTransferSelected);
        Assert.NotEqual(SelectionInk, ((SolidColorBrush)border.BorderBrush).Color);

        result.SelectionArriving = false;
        Assert.Equal(SelectionInk, ((SolidColorBrush)border.BorderBrush).Color);
        Assert.Equal(SelectionWash, ((SolidColorBrush)border.Background).Color);
        Assert.NotEqual(Colors.White, ((SolidColorBrush)row.Foreground).Color);
    });

    [Fact]
    public Task ArrowsOnAFocusedListRowMoveThroughTheSharedSelectionPath() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 3 };
        using var keyboardSource = ApplicationKeySource();
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var list = ApplicationElement<ListView>(fixture, "ResultsList");
        Layout(list);
        var results = fixture.Model.SearchResults;
        UIElement Row(int index) => (UIElement)list.ItemContainerGenerator.ContainerFromIndex(index);

        SendApplicationKey(ApplicationElement<TextBox>(fixture, "SearchBox"), keyboardSource, Key.Down);
        Assert.Equal(0, list.SelectedIndex);
        SendApplicationKey(Row(0), keyboardSource, Key.Down);
        Assert.Equal(1, list.SelectedIndex);
        SendApplicationKey(Row(1), keyboardSource, Key.Down);
        SendApplicationKey(Row(2), keyboardSource, Key.Down);
        Assert.Equal(2, list.SelectedIndex);
        SendApplicationKey(Row(2), keyboardSource, Key.Up);
        Assert.Equal(1, list.SelectedIndex);
        Assert.Equal([results[1]], list.SelectedItems.Cast<object>());
        Assert.All(results, result => Assert.False(result.SelectionArriving));
    });

    [Fact]
    public Task FolderListRowsWaitForTheGlideBeforeLightingUp() => RunSta(() =>
    {
        using var fixture = new Fixture();
        var row = Assert.IsType<Button>(((DataTemplate)fixture.Window.FindResource("DesktopRowTemplate")).LoadContent());
        var item = new DesktopIconViewModel { Name = "not.txt", FullPath = @"C:\Test\not.txt", IsTransferSelected = true, SelectionArriving = true };
        row.DataContext = item;
        row.ApplyTemplate();
        var border = Assert.IsType<Border>(row.Template.FindName("Row", row));
        Assert.NotEqual(SelectionInk, ((SolidColorBrush)border.BorderBrush).Color);

        item.SelectionArriving = false;
        Assert.Equal(SelectionInk, ((SolidColorBrush)border.BorderBrush).Color);
        return Task.CompletedTask;
    });

    [Fact]
    public Task RenamingARevealedCardKeepsTheNameRevealClosedUntilTheEditorEnds() => RunSta(async () =>
    {
        using var fixture = new Fixture(gridView: true, fileOperations: new StartMenuTestFileOperations())
        {
            StandardTotal = 2,
            StandardPrefix = "cok-uzun-bir-rapor-taslagi-ki-karta-asla-sigmaz-ve-kisaltilir"
        };
        using var keyboardSource = ApplicationKeySource();
        await fixture.Search("rapor", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Layout(Assert.IsAssignableFrom<FrameworkElement>(fixture.Window.FindName("ResultsGridScroll")));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var card = Assert.IsType<Button>(typeof(MainWindow).GetMethod("FileSearchFocusTarget", flags)!.Invoke(fixture.Window, [1]));
        CardNameReveal.Open(card, animate: false);
        Assert.Equal(1, CardNameReveal.Progress(card));

        typeof(MainWindow).GetMethod("RenameItem", flags)!.Invoke(fixture.Window, [fixture.Model.SearchResults[1].FullPath]);
        var session = typeof(MainWindow).GetField("_nameEdit", flags)!.GetValue(fixture.Window)!;
        var anchor = (TextBlock)session.GetType().GetProperty("Anchor", flags)!.GetValue(session)!;
        var editor = (InlineNameEditor)session.GetType().GetProperty("Editor", flags)!.GetValue(session)!;
        Assert.Equal(0, CardNameReveal.Progress(card));
        Assert.Null(CardNameReveal.RevealedText(card));
        Assert.Equal(0, anchor.Opacity);

        CardNameReveal.SetCurrent(card);
        CardNameReveal.SetCurrent(null);
        CardNameReveal.Open(card, animate: false);
        CardNameReveal.Close(card, animate: false);
        Assert.Equal(0, CardNameReveal.Progress(card));
        Assert.Equal(0, anchor.Opacity);

        SendApplicationKey(editor.Box, keyboardSource, Key.Escape);
        Assert.Null(typeof(MainWindow).GetField("_nameEdit", flags)!.GetValue(fixture.Window));
        Assert.Equal(1, anchor.Opacity);
        CardNameReveal.Open(card, animate: false);
        Assert.Equal(1, CardNameReveal.Progress(card));
        CardNameReveal.Close(card, animate: false);
    });

    [Fact]
    public Task TabMovesResultsThroughTheSharedSelectionPath() => RunSta(async () =>
    {
        using var fixture = new Fixture(gridView: true) { StandardTotal = 6 };
        await fixture.Search("fatura", naturalLanguage: false);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Layout(Assert.IsAssignableFrom<FrameworkElement>(fixture.Window.FindName("ResultsGridScroll")));
        var step = typeof(MainWindow).GetMethod("StepFileSearchFocus", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var list = ApplicationElement<ListView>(fixture, "ResultsList");

        list.SelectedIndex = 0;
        step.Invoke(fixture.Window, [1]);
        step.Invoke(fixture.Window, [1]);

        Assert.Equal(2, list.SelectedIndex);
        Assert.Equal([fixture.Model.SearchResults[2]], list.SelectedItems.Cast<object>());
        Assert.All(fixture.Model.SearchResults, result => Assert.False(result.SelectionArriving));
    });
}
