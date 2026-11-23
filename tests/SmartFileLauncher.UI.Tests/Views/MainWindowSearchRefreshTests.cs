using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using SmartFileLauncher.Core.Application.Connectivity;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.Diagnostics;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.ViewModels;
using SmartFileLauncher.UI.Views;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Views;

[CollectionDefinition("MainWindow search", DisableParallelization = true)]
public sealed class MainWindowSearchCollection { }

[Collection("MainWindow search")]
public sealed partial class MainWindowSearchRefreshTests
{
    [Fact]
    public Task IndexWarningReflectsSearchAvailabilityAndClearsAfterRecovery() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var window = fixture.Window;
        var show = typeof(MainWindow).GetMethod("ShowIndexHealth", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var banner = (Border)window.FindName("IndexHealthBanner");
        var search = (TextBox)window.FindName("SearchBox");
        var issue = new IndexIssue(IndexIssueReason.JournalInactive, @"E:\", "FSCTL_QUERY_USN_JOURNAL", "1179", 1179);

        show.Invoke(window, [new IndexHealth([issue], SearchAvailable: false)]);
        Assert.Equal(System.Windows.Visibility.Visible, banner.Visibility);
        Assert.False(search.IsEnabled);
        await fixture.Search("rapor", naturalLanguage: false);
        Assert.Equal(0, fixture.StandardCalls);
        string Text(string name) => ((System.Windows.Documents.Run)window.FindName(name)).Text;
        Assert.Contains("kullanılamıyor", Text("IndexHealthEffect"));
        Assert.Contains("günlüğü etkin değil", Text("IndexHealthCause"));
        Assert.Equal(@"E:\", ((TextBlock)window.FindName("IndexHealthLocation")).Text);
        Assert.Equal(System.Windows.Visibility.Visible, ((Border)window.FindName("IndexHealthLocationChip")).Visibility);
        Assert.StartsWith("Ayarlar’da bu disk için", Text("IndexHealthNextStep"));

        search.Clear();
        show.Invoke(window, [new IndexHealth([issue], SearchAvailable: true)]);
        Assert.True(search.IsEnabled);
        Assert.Contains("güncel olmayabilir", Text("IndexHealthEffect"));
        show.Invoke(window, [new IndexHealth([], SearchAvailable: true)]);
        Assert.Equal(System.Windows.Visibility.Collapsed, banner.Visibility);
        await fixture.Search("rapor", naturalLanguage: false);
        Assert.Equal(1, fixture.StandardCalls);
    });

    [Fact]
    public Task AiTypingWaitsForTheLastKeystrokeAndSubmitsOnlyTheCompleteQuery() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.ReleaseParser.TrySetResult();
        foreach (var text in new[] { "m", "mi", "mic", "micro", "microsoft" })
        {
            fixture.Type(text, naturalLanguage: true);
            await fixture.Clock.WaitForTimer();
            Assert.Equal(0, fixture.ParserCalls);
            Assert.Equal(TimeSpan.FromMilliseconds(600), fixture.Clock.LastDelay);
        }
        var pending = fixture.CurrentSearchCompletion;
        fixture.Clock.Elapse();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, fixture.ParserCalls);
        Assert.Equal(new[] { "microsoft" }, fixture.ExecutedQueries);
        Assert.Equal(0, fixture.StandardCalls);
    });

    [Fact]
    public Task NormalTypingDoesNotUseTheAiDelay() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.Type("rapor", naturalLanguage: false);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.StandardCalls);
        Assert.Equal(0, fixture.ParserCalls);
        Assert.Equal(0, fixture.Clock.ScheduledCount);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ClearOrShutdownCancelsAnAiQueryBeforeItIsSent(bool shutdown) => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.Type("rapor", naturalLanguage: true);
        await fixture.Clock.WaitForTimer();
        var pending = fixture.CurrentSearchCompletion;
        if (shutdown) fixture.Window.PrepareForShutdown();
        else fixture.Type("", naturalLanguage: true);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Elapse();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, fixture.ParserCalls);
        Assert.Equal(0, fixture.StandardCalls);
    });

    [Fact]
    public Task SwitchingToNormalModeCancelsThePendingAiDelay() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.Type("rapor", naturalLanguage: true);
        await fixture.Clock.WaitForTimer();
        typeof(MainWindow).GetMethod("DisableNaturalLanguageMode", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(fixture.Window, null);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Elapse();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(1, fixture.StandardCalls);
        Assert.Equal(0, fixture.ParserCalls);
    });

    [Fact]
    public Task IndexNotificationsCannotBypassTheAiInputDelay() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.ReleaseParser.TrySetResult();
        await fixture.Search("önceki");
        fixture.Type("rapor", naturalLanguage: true);
        await fixture.Clock.WaitForTimer();
        var refresh = fixture.Change(new());
        Assert.False(refresh.IsCompleted);
        Assert.Equal(1, fixture.ParserCalls);
        fixture.Clock.Elapse();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.ParserCalls);
        Assert.Equal(3, fixture.LocalCalls);
    });

    [Fact]
    public Task IndexRefreshWaitsForAiAndReusesItsIntentWithoutCancellingIt() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.RefreshedName = "ikinci-fatura";
        var initial = fixture.Search("faturaları bul");
        await fixture.ParserEntered.Task;
        var refresh = fixture.Refresh();

        Assert.False(refresh.IsCompleted);
        Assert.False(fixture.ParserToken.IsCancellationRequested);
        Assert.Equal(0, fixture.LocalCalls);
        fixture.ReleaseParser.TrySetResult();
        await Task.WhenAll(initial, refresh);

        Assert.Equal(1, fixture.ParserCalls);
        Assert.Equal(2, fixture.LocalCalls);
        Assert.Equal(0, fixture.StandardCalls);
        Assert.False(fixture.ParserToken.IsCancellationRequested);
        Assert.Equal("ikinci-fatura", Assert.Single(fixture.Model.SearchResults).Name);
        Assert.Single(fixture.Log.GetSnapshot(), line => line.Contains("Arama sorgusu:"));
        Assert.Single(fixture.Log.GetSnapshot(), line => line.Contains("Arama sonuçları güncellendi:"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task UnchangedResultsDoNotRepeatPresentationOrDiagnosticMessages(bool naturalLanguage) => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.ReleaseParser.TrySetResult();
        await fixture.Search("fatura", naturalLanguage);
        var changes = 0;
        fixture.Model.SearchResults.CollectionChanged += (_, _) => changes++;
        var before = fixture.Log.GetSnapshot().ToArray();
        var item = Assert.Single(fixture.Model.SearchResults);

        await fixture.Refresh();
        await fixture.Refresh();

        Assert.Equal(0, changes);
        Assert.Same(item, Assert.Single(fixture.Model.SearchResults));
        Assert.Equal(before, fixture.Log.GetSnapshot());
        Assert.Equal(naturalLanguage ? 1 : 0, fixture.ParserCalls);
        Assert.Equal(naturalLanguage ? 0 : 3, fixture.StandardCalls);
    });

    [Fact]
    public Task ANewUserQueryCancelsTheOldSearchAndDiscardsItsWaitingRefresh() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var oldSearch = fixture.Search("eski");
        await fixture.ParserEntered.Task;
        var oldToken = fixture.ParserToken;
        var refresh = fixture.Refresh();
        var latest = fixture.Search("yeni");
        fixture.ReleaseParser.TrySetResult();
        await Task.WhenAll(oldSearch, latest, refresh);

        Assert.True(oldToken.IsCancellationRequested);
        Assert.Equal("yeni", fixture.Model.LastSearchQuery);
        Assert.Equal(new[] { "yeni" }, fixture.ExecutedQueries);
        Assert.Equal(1, fixture.LocalCalls);
        Assert.DoesNotContain(fixture.Log.GetSnapshot(), line => line.Contains("Arama sonuçları güncellendi:"));
    });

    [Fact]
    public Task AFailedLocalAiRefreshKeepsThePreviousResultsAndDoesNotRunStandardSearch() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.ReleaseParser.TrySetResult();
        await fixture.Search("fatura");
        var item = Assert.Single(fixture.Model.SearchResults);
        fixture.FailRefresh = true;

        await fixture.Refresh();

        Assert.Equal(1, fixture.ParserCalls);
        Assert.Equal(0, fixture.StandardCalls);
        Assert.Same(item, Assert.Single(fixture.Model.SearchResults));
        Assert.Contains(fixture.Log.GetSnapshot(), line => line.Contains("Arama sonuçları güncellenemedi:"));
    });

    [Fact]
    public Task StandardResultsLoadPageByPageUntilTheCountedTotal() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 450 };
        await fixture.Search("fatura", naturalLanguage: false);
        Assert.Equal(100, fixture.Model.SearchResults.Count);
        Assert.Equal("450 sonuç", await fixture.WaitForCount("450 sonuç"));

        await fixture.LoadNextPage();
        Assert.Equal(300, fixture.Model.SearchResults.Count);
        await fixture.LoadNextPage();
        Assert.Equal(450, fixture.Model.SearchResults.Count);
        await fixture.LoadNextPage();

        Assert.Equal(new[] { (100, 200), (300, 200) }, fixture.PageCalls);
        Assert.Equal(450, fixture.Model.SearchResults.Select(result => result.FullPath).Distinct().Count());
        Assert.Equal("fatura-0449", fixture.Model.SearchResults[^1].Name);
    });

    [Fact]
    public Task ARefreshReloadsEveryLoadedPageAndANewQueryStartsOver() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 450 };
        await fixture.Search("fatura", naturalLanguage: false);
        await fixture.LoadNextPage();
        Assert.Equal(300, fixture.Model.SearchResults.Count);

        await fixture.Refresh();
        Assert.Equal(300, fixture.LastStandardLimit);
        Assert.Equal(300, fixture.Model.SearchResults.Count);
        await fixture.LoadNextPage();
        Assert.Equal(450, fixture.Model.SearchResults.Count);

        fixture.StandardTotal = 40;
        await fixture.Search("rapor", naturalLanguage: false);
        Assert.Equal(100, fixture.LastStandardLimit);
        Assert.Equal(40, fixture.Model.SearchResults.Count);
        Assert.Equal("40 sonuç", await fixture.WaitForCount("40 sonuç"));
        await fixture.LoadNextPage();
        Assert.Equal(new[] { (100, 200), (300, 200) }, fixture.PageCalls);
    });

    [Fact]
    public Task AiResultsAreNotPagedOrCounted() => RunSta(async () =>
    {
        using var fixture = new Fixture { StandardTotal = 450 };
        fixture.ReleaseParser.TrySetResult();
        await fixture.Search("fatura", naturalLanguage: true);
        await fixture.LoadNextPage();

        Assert.Single(fixture.Model.SearchResults);
        Assert.Empty(fixture.PageCalls);
        Assert.NotEqual(System.Windows.Visibility.Visible, ((TextBlock)fixture.Window.FindName("SearchCountText")).Visibility);
    });

    [Fact]
    public Task GridViewRealizesOnlyTheVisibleRowsOfEveryLoadedPage() => RunSta(async () =>
    {
        using var fixture = new Fixture(gridView: true) { StandardTotal = 450 };
        await fixture.Search("fatura", naturalLanguage: false);
        await fixture.LoadNextPage();
        await fixture.LoadNextPage();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        var grid = (ListBox)fixture.Window.FindName("ResultsGrid");
        var rows = grid.Items.Cast<SearchResultRowViewModel>().ToArray();
        Assert.Equal(113, rows.Length);
        Assert.All(rows[..^1], row => Assert.Equal(MainWindow.SearchGridColumns, row.Items.Count));
        Assert.Equal(fixture.Model.SearchResults, rows.SelectMany(row => row.Items));

        var host = (System.Windows.FrameworkElement)fixture.Window.FindName("ResultsGridScroll");
        Assert.Equal(System.Windows.Visibility.Visible, host.Visibility);
        Layout(host);
        Assert.NotNull(grid.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.InRange(Realized(grid), 1, 12);

        var scroll = (ScrollViewer)typeof(MainWindow)
            .GetMethod("SearchResultsScroll", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(fixture.Window, null)!;
        Assert.Same(grid, scroll.TemplatedParent);
        for (var pass = 0; pass < 5 && grid.ItemContainerGenerator.ContainerFromIndex(rows.Length - 1) == null; pass++)
        {
            scroll.ScrollToBottom();
            Layout(host);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        Assert.NotNull(grid.ItemContainerGenerator.ContainerFromIndex(rows.Length - 1));
        Assert.Null(grid.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.InRange(Realized(grid), 1, 12);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ANewQueryAfterDeepPagingNeverPresentsTheStaleResultsAgain(bool gridView) => RunSta(async () =>
    {
        using var fixture = new Fixture(gridView: gridView) { StandardTotal = 10_100 };
        await fixture.Search("fatura", naturalLanguage: false);
        for (var page = 0; page < 50; page++) await fixture.LoadNextPage();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(10_100, fixture.Model.SearchResults.Count);
        var list = (ListBox)fixture.Window.FindName(gridView ? "ResultsGrid" : "ResultsList");
        var host = gridView ? (System.Windows.FrameworkElement)fixture.Window.FindName("ResultsGridScroll") : list;
        Layout(host);
        var scroll = (ScrollViewer)typeof(MainWindow)
            .GetMethod("SearchResultsScroll", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(fixture.Window, null)!;
        for (var pass = 0; pass < 5 && list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) == null; pass++)
        {
            scroll.ScrollToBottom();
            Layout(host);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1));
        Assert.True(scroll.VerticalOffset > 0);

        var stale = fixture.Model.SearchResults.ToHashSet();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var staleYields = 0;
        var rendering = false;
        fixture.Model.SearchResults.CollectionChanged += (_, _) => rendering = true;
        void Posted(object? sender, DispatcherHookEventArgs e)
        {
            if (e.Operation.Priority == DispatcherPriority.ApplicationIdle && dispatcher.CheckAccess() &&
                rendering && fixture.Model.SearchResults.Any(stale.Contains)) staleYields++;
        }
        dispatcher.Hooks.OperationPosted += Posted;
        fixture.StandardPrefix = "rapor";
        await fixture.Search("rapor", naturalLanguage: false);
        dispatcher.Hooks.OperationPosted -= Posted;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Layout(host);

        Assert.Equal(100, fixture.Model.SearchResults.Count);
        Assert.DoesNotContain(fixture.Model.SearchResults, stale.Contains);
        Assert.Equal(0, staleYields);
        Assert.Equal(0, scroll.VerticalOffset);
        Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TypingNeverChangesTheTrayHeight(bool gridView) => RunSta(async () =>
    {
        using var fixture = new Fixture(gridView: gridView) { StandardTotal = 450 };
        var root = (System.Windows.FrameworkElement)fixture.Window.Content;
        double Height()
        {
            root.Measure(new System.Windows.Size(680, double.PositiveInfinity));
            return root.DesiredSize.Height;
        }
        fixture.Type("fatura", naturalLanguage: false);
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("450 sonuç", await fixture.WaitForCount("450 sonuç"));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var steady = Height();

        var dispatcher = Dispatcher.CurrentDispatcher;
        var heights = new HashSet<double>();
        void Posted(object? sender, DispatcherHookEventArgs e)
        {
            if (e.Operation.Priority <= DispatcherPriority.Background && dispatcher.CheckAccess()) heights.Add(Height());
        }
        dispatcher.Hooks.OperationPosted += Posted;
        foreach (var (text, prefix) in new[] { ("fatural", "fatura"), ("faturala", "rapor"), ("faturalar", "belge") })
        {
            fixture.StandardPrefix = prefix;
            fixture.Type(text, naturalLanguage: false);
            await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("450 sonuç", await fixture.WaitForCount("450 sonuç"));
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        dispatcher.Hooks.OperationPosted -= Posted;

        Assert.NotEmpty(heights);
        Assert.All(heights, height => Assert.Equal(steady, height, 1));
        Assert.Equal("ilk-belge", fixture.Model.SearchResults[0].Name);
    });

    [Fact]
    public Task UnrelatedFileChangesDoNotExecuteAnOpenLiteralSearch() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Search("fatura", naturalLanguage: false);

        await fixture.Change(new() { AffectedNames = ["cache.tmp"] });
        Assert.Equal(1, fixture.StandardCalls);

        await fixture.Change(new() { AffectedNames = ["fatura.pdf", "arsiv.pdf"] });
        Assert.Equal(2, fixture.StandardCalls);

        await fixture.Change(new() { IsDirectory = true, AffectedNames = ["Belgeler", "Arsiv"] });
        Assert.Equal(3, fixture.StandardCalls);
    });

    [Fact]
    public Task AiFileChangeNotificationsKeepTheResolvedQuery() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.ReleaseParser.TrySetResult();
        await fixture.Search("fatura");

        await fixture.Change(new() { AffectedNames = ["unrelated.txt"] });

        Assert.Equal(1, fixture.ParserCalls);
        Assert.Equal(2, fixture.LocalCalls);
        Assert.Equal(0, fixture.StandardCalls);
    });

    [Fact]
    public Task ShutdownReleasesARefreshWaitingForAi() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var initial = fixture.Search("fatura");
        await fixture.ParserEntered.Task;
        var refresh = fixture.Refresh();

        fixture.Window.PrepareForShutdown();
        await Task.WhenAll(initial, refresh);

        Assert.True(fixture.ParserToken.IsCancellationRequested);
        Assert.Equal(0, fixture.LocalCalls);
    });

    [Fact]
    public Task ShutdownDoesNotRestartConnectivityAfterTheInitialProbeCompletes() => RunSta(async () =>
    {
        var probe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connectivity = new ConnectivityMonitor(_ => probe.Task, TimeSpan.FromHours(1));
        using var fixture = new Fixture(connectivity);
        var initialization = (Task)typeof(MainWindow)
            .GetMethod("InitializeConnectivityAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(fixture.Window, null)!;
        Assert.False(initialization.IsCompleted);

        fixture.Window.PrepareForShutdown();
        connectivity.Dispose();
        probe.SetResult(true);

        await initialization.WaitAsync(TimeSpan.FromSeconds(5));
    });

    private static void Layout(System.Windows.FrameworkElement element)
    {
        element.Measure(new System.Windows.Size(680, 500));
        element.Arrange(new System.Windows.Rect(0, 0, 680, 500));
        element.UpdateLayout();
    }

    private static int Realized(ListBox list) =>
        Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);

    private static Task RunSta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception failure) { completion.TrySetException(failure); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly MainWindowViewModel Model = new() { IsIndexed = true };
        internal readonly ApplicationLog Log = new();
        internal readonly DebounceClock Clock = new();
        internal readonly TaskCompletionSource ParserEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseParser = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> ExecutedQueries = [];
        internal CancellationToken ParserToken;
        internal int ParserCalls;
        internal int LocalCalls;
        internal int StandardCalls;
        internal string RefreshedName = "ilk-fatura";
        internal bool FailRefresh;
        internal int StandardTotal = 1;
        internal string StandardPrefix = "fatura";
        internal int LastStandardLimit;
        internal readonly List<(int Offset, int Limit)> PageCalls = [];
        internal readonly List<string?> StandardScopes = [];
        internal MainWindow Window { get; }

        internal Fixture(IConnectivityMonitor? connectivity = null, bool gridView = false,
            IStartMenuShell? startMenuShell = null, IFileOperationService? fileOperations = null)
        {
            var search = new SearchApplicationService(
                (_, limit, view, _) =>
                {
                    Interlocked.Increment(ref StandardCalls);
                    lock (StandardScopes) StandardScopes.Add(view.Scope);
                    LastStandardLimit = limit;
                    return Standard(0, limit);
                },
                (query, _, _, _) =>
                {
                    var call = Interlocked.Increment(ref LocalCalls);
                    ExecutedQueries.Add(query.Keywords.Single());
                    if (FailRefresh && call > 1) throw new InvalidOperationException("Yerel indeks hazır değil");
                    return [Result(call == 1 ? "ilk-fatura" : RefreshedName)];
                },
                async (query, token) =>
                {
                    Interlocked.Increment(ref ParserCalls);
                    ParserToken = token;
                    ParserEntered.TrySetResult();
                    await ReleaseParser.Task.WaitAsync(token);
                    return new StructuredQuery { Keywords = [query] };
                },
                _ => throw new InvalidOperationException("Rule-based parser çağrılmamalı"),
                (_, offset, limit, _, _) =>
                {
                    lock (PageCalls) PageCalls.Add((offset, limit));
                    return Standard(offset, limit);
                },
                (_, _, _) => StandardTotal);
            var settings = new AppSettings { AnimationsEnabled = false, ThumbnailPreviewsEnabled = false, GridViewEnabled = gridView };
            Window = new MainWindow(Model, settings,
                Stub<ISettingsApplicationService>(), Stub<IIndexMaintenanceService>(),
                Stub<IIndexLifecycleService>(method => method.Name switch
                {
                    "get_IsTokenIndexReady" or "get_IsInitialized" => true,
                    "get_ReconciliationStatus" => new IndexReconciliationStatus(false, 0, 0, 100),
                    "GetIndexedRoots" => Array.Empty<FileSystemNode>(),
                    _ => throw new NotSupportedException(method.Name)
                }),
                Stub<IIndexedLocationProvider>(), search,
                Stub<ISearchDiagnosticsService>(method => method.Name == "Tokenize"
                    ? Array.Empty<string>() : throw new NotSupportedException(method.Name)),
                Stub<IThumbnailService>(), Stub<IFolderNavigationService>(),
                connectivity ?? Stub<IConnectivityMonitor>(method => method.Name == "get_IsConnected"
                    ? true : throw new NotSupportedException(method.Name)),
                fileOperations ?? Stub<IFileOperationService>(), Stub<IApplicationShellService>(), Log,
                ApplicationStartupOptions.Default, null, startMenuShell: startMenuShell) { SearchTimeProvider = Clock };
        }

        internal Task Search(string query, bool naturalLanguage = true)
        {
            Model.IsIndexed = false;
            ((TextBox)Window.FindName("SearchBox")).Text = query;
            Model.IsIndexed = true;
            return Invoke("StartSearchAsync", query, new SearchRequest(query, naturalLanguage, true), true, null, false);
        }

        internal void Type(string text, bool naturalLanguage)
        {
            Model.IsNaturalLanguageMode = naturalLanguage;
            ((TextBox)Window.FindName("SearchBox")).Text = text;
        }

        internal Task CurrentSearchCompletion
        {
            get
            {
                var operation = typeof(MainWindow).GetField("_currentSearch", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(Window);
                return operation == null ? Task.CompletedTask :
                    ((TaskCompletionSource)operation.GetType().GetProperty("Completion")!.GetValue(operation)!).Task;
            }
        }

        internal Task Refresh() => Invoke("RefreshSearchResultsAsync");

        internal Task Change(FileChangeEvent change)
        {
            typeof(MainWindow).GetMethod("HandleFileSystemChange", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Window, [change]);
            return Invoke("ProcessFileChangeAsync");
        }

        private Task Invoke(string name, params object?[] args) =>
            (Task)typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Window, args)!;

        public void Dispose()
        {
            Window.PrepareForShutdown();
            Window.Close();
        }

        private SearchResult[] Standard(int offset, int limit) =>
            Enumerable.Range(offset, Math.Max(0, Math.Min(limit, StandardTotal - offset)))
                .Select(i => Result(i == 0 ? $"ilk-{StandardPrefix}" : $"{StandardPrefix}-{i:D4}")).ToArray();

        internal Task LoadNextPage() => Invoke("LoadNextSearchPageAsync");

        internal async Task<string> WaitForCount(string expected)
        {
            var text = (TextBlock)Window.FindName("SearchCountText");
            for (var i = 0; i < 200 && text.Text != expected; i++) await Task.Delay(10);
            return text.Text;
        }

        private static SearchResult Result(string name) => new()
        {
            Name = name, FullPath = Path.Combine(@"C:\Test", name), IsDirectory = true, Score = 200
        };
    }

    private sealed class DebounceClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<DelayTimer> _timers = [];
        private readonly SemaphoreSlim _created = new(0);
        internal int ScheduledCount;
        internal TimeSpan LastDelay;
        internal async Task WaitForTimer() => Assert.True(await _created.WaitAsync(TimeSpan.FromSeconds(5)));
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new DelayTimer(callback, state);
            lock (_gate) { _timers.Add(timer); LastDelay = dueTime; ScheduledCount++; }
            _created.Release();
            return timer;
        }
        internal void Elapse()
        {
            DelayTimer[] timers;
            lock (_gate) { timers = _timers.ToArray(); _timers.Clear(); }
            foreach (var timer in timers) timer.Fire();
        }
        private sealed class DelayTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;
            internal void Fire() { if (Interlocked.Exchange(ref _disposed, 1) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static T Stub<T>(Func<MethodInfo, object?>? invoke = null) where T : class
    {
        var stub = DispatchProxy.Create<T, ServiceStub>();
        ((ServiceStub)(object)stub).Call = invoke;
        return stub;
    }

    public class ServiceStub : DispatchProxy
    {
        public Func<MethodInfo, object?>? Call { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.ReturnType == typeof(void) ? null
                : Call != null ? Call(method) : throw new NotSupportedException(method.Name);
    }
}
