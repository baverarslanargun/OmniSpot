using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.UI.Views;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Views;

public sealed partial class MainWindowSearchRefreshTests
{
    [Fact]
    public Task AnEmptySearchOffersASuggestionAfterTheUserStopsTyping() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 0, Suggestion = "fatura" };
        var button = (Button)fixture.Window.FindName("NoResultsSuggestionButton");

        fixture.Type("faturs", naturalLanguage: false);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Visibility.Collapsed, button.Visibility);
        Assert.Empty(fixture.SuggestedQueries);

        await fixture.Clock.WaitForTimer();
        Assert.Equal(TimeSpan.FromMilliseconds(350), fixture.Clock.LastDelay);
        fixture.Clock.Elapse();
        await WaitUntil(() => button.Visibility == Visibility.Visible);

        Assert.Equal(new[] { "faturs" }, fixture.SuggestedQueries);
        Assert.Equal("fatura", ((System.Windows.Documents.Run)fixture.Window.FindName("NoResultsSuggestionText")).Text);
        Assert.Equal("fatura", fixture.Window.CurrentSearchSuggestion);
    });

    [Fact]
    public Task TypingAgainBeforeThePauseNeverAsksForASuggestion() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 0, Suggestion = "fatura" };

        fixture.Type("fatu", naturalLanguage: false);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Clock.WaitForTimer();
        fixture.Type("fatur", naturalLanguage: false);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Clock.WaitForTimer();
        fixture.Clock.Elapse();
        await WaitUntil(() => fixture.SuggestedQueries.Count > 0);

        Assert.Equal(new[] { "fatur" }, fixture.SuggestedQueries);
    });

    [Fact]
    public Task SearchesWithResultsAndAiSearchesNeverAskForASuggestion() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 3, Suggestion = "fatura" };

        fixture.Type("fatura", naturalLanguage: false);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.Clock.ScheduledCount);
        Assert.Empty(fixture.SuggestedQueries);
        Assert.Null(fixture.Window.CurrentSearchSuggestion);
    });

    [Fact]
    public Task AFilterThatHidesEveryResultOffersToRemoveItInsteadOfASpellingFix() => RunSta(async () =>
    {
        using var fixture = new Fixture
        {
            StandardTotal = 0, Suggestion = "fatura", CountFor = view => view.Filter.IsActive ? 0 : 12
        };
        typeof(MainWindow).GetField("_searchView", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(fixture.Window,
            ResultView.SearchDefault with { Filter = new ItemFilter(Kind: FilterItemKind.FoldersOnly) });
        var clear = (Button)fixture.Window.FindName("NoResultsClearFilterButton");

        fixture.Type("fatura", naturalLanguage: false);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Clock.WaitForTimer();
        fixture.Clock.Elapse();
        await WaitUntil(() => clear.Visibility == Visibility.Visible);

        Assert.Equal(12, fixture.Window.CurrentFilterHiddenCount);
        Assert.Equal("· 12 sonuç",((System.Windows.Documents.Run)fixture.Window.FindName("NoResultsClearFilterCount")).Text);
        Assert.Empty(fixture.SuggestedQueries);
        Assert.Null(fixture.Window.CurrentSearchSuggestion);

        var calls = fixture.StandardCalls;
        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        var view = (ResultView)typeof(MainWindow).GetField("_searchView", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(fixture.Window)!;
        Assert.False(view.Filter.IsActive);
        Assert.True(fixture.StandardCalls > calls);
    });

    [Fact]
    public Task AFilterThatHidesNothingFallsBackToTheSpellingSuggestion() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 0, Suggestion = "fatura", CountFor = _ => 0 };
        typeof(MainWindow).GetField("_searchView", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(fixture.Window,
            ResultView.SearchDefault with { Filter = new ItemFilter(Kind: FilterItemKind.FoldersOnly) });

        fixture.Type("faturs", naturalLanguage: false);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Clock.WaitForTimer();
        fixture.Clock.Elapse();
        await WaitUntil(() => fixture.Window.CurrentSearchSuggestion != null);

        Assert.Equal("fatura", fixture.Window.CurrentSearchSuggestion);
        Assert.Null(fixture.Window.CurrentFilterHiddenCount);
    });

    [Fact]
    public Task StandardResultsCarryTheQueryForHighlightingButAiResultsDoNot() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 3 };
        var results = (System.Collections.ObjectModel.ObservableCollection<SmartFileLauncher.UI.ViewModels.SearchResultViewModel>)
            typeof(MainWindow).GetProperty("_searchResults", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Window)!;

        fixture.Type("fatura", naturalLanguage: false);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(results);
        Assert.All(results, item => Assert.Equal("fatura", item.HighlightQuery));

        fixture.Type("fatura-", naturalLanguage: false);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, item => Assert.Equal("fatura-", item.HighlightQuery));

        fixture.ReleaseParser.TrySetResult();
        await fixture.Search("fatura bul", naturalLanguage: true);
        Assert.NotEmpty(results);
        Assert.All(results, item => Assert.Null(item.HighlightQuery));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TheSuggestionReplacesTheQueryByClickOrEnter(bool enter) => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 0, Suggestion = "fatura" };
        var box = (TextBox)fixture.Window.FindName("SearchBox");
        var button = (Button)fixture.Window.FindName("NoResultsSuggestionButton");

        fixture.Type("faturs", naturalLanguage: false);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Clock.WaitForTimer();
        fixture.Clock.Elapse();
        await WaitUntil(() => button.Visibility == Visibility.Visible);

        fixture.StandardTotal = 2;
        if (enter)
        {
            using var keyboardSource = ApplicationKeySource();
            SendApplicationKey(box, keyboardSource, Key.Enter);
        }
        else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("fatura", box.Text);
        Assert.Equal(Visibility.Collapsed, button.Visibility);
        Assert.Null(fixture.Window.CurrentSearchSuggestion);
    });
}
