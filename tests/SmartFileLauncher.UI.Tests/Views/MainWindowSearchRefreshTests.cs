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
public sealed class MainWindowSearchRefreshTests
{
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
        internal MainWindow Window { get; }

        internal Fixture()
        {
            var search = new SearchApplicationService(
                (_, _, _, _) => { Interlocked.Increment(ref StandardCalls); return [Result("ilk-fatura")]; },
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
                _ => throw new InvalidOperationException("Rule-based parser çağrılmamalı"));
            var settings = new AppSettings { ThumbnailPreviewsEnabled = false };
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
                Stub<IConnectivityMonitor>(method => method.Name == "get_IsConnected"
                    ? true : throw new NotSupportedException(method.Name)),
                Stub<IFileOperationService>(), Stub<IApplicationShellService>(), Log,
                ApplicationStartupOptions.Default, null) { SearchTimeProvider = Clock };
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
