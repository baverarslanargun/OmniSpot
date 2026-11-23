using System.Globalization;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class StartMenuApplicationsTests
{
    [Fact]
    public void GeneralShowsThreeStrongMatchesAndCountsEveryStrongMatch()
    {
        var catalog = Catalog(
            App("word-a", "A Clip Viewer"),
            App("word-b", "A Clip Tool"),
            App("word-c", "Other xclip·Clip viewer"),
            App("exact", "Clip"),
            App("prefix-b", "Clipper"),
            App("prefix-a", "Clip Tool"),
            App("interior", "Eclipse"));

        var result = catalog.MatchGeneral(" clip ");

        Assert.Equal(6, result.TotalCount);
        Assert.Equal(["exact", "prefix-a", "prefix-b"], result.Items.Select(app => app.Id));
        Assert.Equal("exact", result.UniqueFirst?.Id);

        var wordResult = Catalog(App("word", "Other xclip·Clip viewer"), App("interior", "Eclipse"))
            .MatchGeneral("clip");
        Assert.Equal("word", Assert.Single(wordResult.Items).Id);
        Assert.Equal("word", wordResult.UniqueFirst?.Id);
    }

    [Fact]
    public void GeneralDoesNotAutoSelectBetweenEquallyStrongPrefixes()
    {
        var catalog = Catalog(App("1", "Map Maker"), App("2", "Mapping"), App("3", "World Map"));

        var result = catalog.MatchGeneral("Map");

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(["1", "2", "3"], result.Items.Select(app => app.Id));
        Assert.Null(result.UniqueFirst);
    }

    [Fact]
    public void IdenticalNamesKeepDistinctIdentitiesAndDisambiguatingDetails()
    {
        var catalog = Catalog(
            App("1", "Terminal", "Publisher"),
            App("2", "terminal", "publisher "),
            App("3", "Terminal"),
            App("4", "Terminal", "Independent"),
            App("5", "Terminal", "3"),
            App("3", "Terminal duplicate", "Ignored"),
            App("unique", "Another App", "Omitted"));

        var matches = catalog.MatchGeneral("terminal");

        Assert.Equal(5, matches.TotalCount);
        Assert.Null(matches.UniqueFirst);
        Assert.Equal(["1", "2", "3"], matches.Items.Select(app => app.Id));
        Assert.Equal(["1", "2", "3", "Independent", "5"], catalog.MatchAll("terminal").Select(app => app.Detail));
        Assert.Empty(catalog.MatchAll("another")[0].Detail);
    }

    [Fact]
    public void AllApplicationsListNamesStartingWithTheQueryFirst()
    {
        var catalog = Catalog(
            App("micro", "Microtool"),
            App("power", "Power Tool"),
            App("kit", "Tool Kit"),
            App("exact", "Tool"),
            App("box", "Tool Box"),
            App("another", "Another Tool"),
            App("other", "Other"));

        Assert.Equal(["exact", "box", "kit", "another", "power", "micro"], catalog.MatchAll("tool").Select(app => app.Id));
        Assert.Equal(["other", "another"], catalog.MatchAll("oth").Select(app => app.Id));
        Assert.Equal(7, catalog.MatchAll("").Count);
    }

    [Fact]
    public void PinnedApplicationsLeadTheFullListAndWinTiesButNotStrongerMatches()
    {
        var catalog = Catalog(
            App("tool", "Tool"),
            App("box", "Tool Box"),
            App("kit", "Tool Kit"),
            App("mytool", "My Tool"),
            App("zeta", "Zeta"));
        var pinned = new HashSet<string>(StringComparer.Ordinal) { "kit", "zeta", "removed" };

        Assert.Equal(["kit", "zeta", "mytool", "tool", "box"], catalog.MatchAll("", pinned).Select(app => app.Id));
        Assert.Equal(["mytool", "tool", "box", "kit", "zeta"], catalog.MatchAll("", new HashSet<string>()).Select(app => app.Id));
        Assert.Equal(["tool", "kit", "box", "mytool"], catalog.MatchAll("tool", pinned).Select(app => app.Id));

        Assert.Null(catalog.MatchGeneral("to").UniqueFirst);
        var tie = catalog.MatchGeneral("to", pinned);
        Assert.Equal("kit", tie.UniqueFirst?.Id);
        Assert.Equal(["kit", "tool", "box"], tie.Items.Select(app => app.Id));
        Assert.Equal("kit", new StartMenuApplications.SearchSession(catalog, pinned).MatchGeneral("to").UniqueFirst?.Id);
        Assert.Equal("tool", catalog.MatchGeneral("tool", pinned).UniqueFirst?.Id);
    }

    [Fact]
    public void IdenticalNamesShowOnlyThePartOfTheTargetThatDiffers()
    {
        var catalog = Catalog(
            App("help-a", "Help index", "https://docs.example.com/display/VMAYA"),
            App("help-b", "Help index", "https://docs.example.com/display/VRAY4MAYA"),
            App("classes-a", "Classes", @"C:\Program Files\Chaos Group\V-Ray\Maya 2015 for x64\docs\vrayclasses.chm"),
            App("classes-b", "Classes", @"C:\Program Files\Chaos Group\V-Ray\Maya 2022 for x64\vray\docs\vrayclasses.chm"),
            App("docs-a", "Docs", "file:///C:/Program%20Files/Chaos%20Group/V-Ray/Maya%202022%20for%20x64/vray/docs/index.html"),
            App("docs-b", "Docs", @"C:\Program Files\Chaos Group\V-Ray\Maya 2015 for x64\docs\index.html"),
            App(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Chaos Group\V-Ray\Maya 2022 for x64\vray\bin\vrmesh_viewer.exe", "Viewer"),
            App(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Chaos Group\V-Ray\Maya 2015 for x64\bin\vrmesh_viewer.exe", "Viewer"),
            App("tool-a", "Tool", @"C:\Tools\Alpha\tool.exe"),
            App("tool-b", "Tool", @"D:\Beta\tool.exe"),
            App("same-a", "Same", @"C:\Apps\same.exe"),
            App("same-b", "Same", @"C:\Apps\same.exe"));

        string[] Details(string name) => catalog.MatchAll(name).Select(app => app.Detail).ToArray();

        Assert.Equal(["VMAYA", "VRAY4MAYA"], Details("Help index"));
        Assert.Equal(["Maya 2015 for x64", "Maya 2022 for x64"], Details("Classes"));
        Assert.Equal(["Maya 2022 for x64", "Maya 2015 for x64"], Details("Docs"));
        Assert.Equal(["Maya 2015 for x64", "Maya 2022 for x64"], Details("Viewer"));
        Assert.Equal(["Alpha", "Beta"], Details("Tool"));
        Assert.Equal(["same-a", "same-b"], Details("Same"));
    }

    [Theory]
    [InlineData("istanbul", "dotted")]
    [InlineData("İSTANBUL", "dotted")]
    [InlineData("ıstanbul", "dotless")]
    [InlineData("ISTANBUL", "dotless")]
    public void GeneralAndAllUseTurkishDottedAndDotlessCasePairs(string query, string expected)
    {
        var catalog = new StartMenuApplications(
            [App("dotted", "İstanbul"), App("dotless", "Istanbul")],
            CultureInfo.GetCultureInfo("tr-TR"));

        Assert.Equal(expected, catalog.MatchGeneral(query).UniqueFirst?.Id);
        Assert.Equal(expected, Assert.Single(catalog.MatchAll(query)).Id);
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData("cmd.exe")]
    [InlineData("Rapor.XLSX")]
    [InlineData("application.lnk")]
    [InlineData(@"C:\program\paint.net")]
    [InlineData(@"C:paint.net")]
    [InlineData(@"\\server\share")]
    [InlineData(@"..\paint.net")]
    [InlineData("folder/paint.net")]
    [InlineData("*.pdf")]
    public void FileIntentSuppressesGeneralEvenIfACatalogNameMatches(string query)
    {
        var catalog = Catalog(App("match", query));

        var result = catalog.MatchGeneral(query);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Null(result.UniqueFirst);
        Assert.Equal("match", Assert.Single(catalog.MatchAll(query)).Id);
    }

    [Fact]
    public void DottedNamesCanMatchAndLeadingDotDoesNotMatchUnrelatedPrefixes()
    {
        var catalog = Catalog(
            App("paint", "paint.net"),
            App("net", ".NET"),
            App("net-tools", ".NET Tools"),
            App("private", ".private Utilities"));

        Assert.Equal("paint", catalog.MatchGeneral("paint.net").UniqueFirst?.Id);
        Assert.Equal("net", catalog.MatchGeneral(".net").UniqueFirst?.Id);
        Assert.Equal("net-tools", catalog.MatchGeneral(".net tools").UniqueFirst?.Id);
        Assert.Empty(catalog.MatchGeneral(".private").Items);
    }

    [Fact]
    public void AllListsNamesAlphabeticallyAndRanksMatchesInsideWordsLast()
    {
        var catalog = Catalog(
            App("c", "Third Tool"),
            App("b", "Clip"),
            App("a", "A Clip Tool"),
            App("e", "Eclipse"));

        Assert.Empty(catalog.MatchGeneral(" ").Items);
        Assert.Empty(catalog.MatchGeneral(null).Items);
        Assert.Empty(catalog.MatchGeneral("lips").Items);
        Assert.Equal(["a", "b", "e", "c"], catalog.MatchAll(null).Select(app => app.Id));
        Assert.Equal(["e"], catalog.MatchAll("lips").Select(app => app.Id));
        Assert.Equal(["b", "a", "e"], catalog.MatchAll("clip").Select(app => app.Id));
    }

    [Fact]
    public void AppendingNarrowsAllCandidatesIncludingThoseOutsideTheVisibleThree()
    {
        var catalog = Catalog(App("a", "Cal"), App("b", "Cam"), App("c", "Cat"), App("d", "Google Chrome"),
            App("e", "Other"), App("f", "Tool"));
        var session = new StartMenuApplications.SearchSession(catalog);

        var first = session.MatchGeneral("c");
        Assert.Equal(4, first.TotalCount);
        Assert.DoesNotContain(first.Items, app => app.Id == "d");
        Assert.Equal(4, session.CandidateCount);
        Assert.Equal("d", session.MatchGeneral("ch").UniqueFirst?.Id);
        Assert.Equal(1, session.CandidateCount);
        Assert.Empty(session.MatchGeneral("chrome.").Items);
        Assert.Equal(0, session.CandidateCount);
        Assert.Empty(session.MatchGeneral("chrome.ex").Items);
        Assert.Equal(4, session.MatchGeneral("c").TotalCount);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("cs-CZ")]
    [InlineData("hu-HU")]
    public void IncrementalResultsAgreeWithFreshRankingAfterEditsAndQueryModeChanges(string culture)
    {
        var catalog = new StartMenuApplications(
            [App("chrome", "Google Chrome"), App("mark", "Čistič"), App("telegram", "Tele\u00ADgram"),
             App("i", "İstanbul"), App("dotless", "Istanbul"), App("dzs", "Dzs Tool"),
             App("net", ".NET"), App("tools", ".NET Tools"), App("pdf", "report.pdf helper")],
            CultureInfo.GetCultureInfo(culture));
        var session = new StartMenuApplications.SearchSession(catalog);

        foreach (var query in new[] { "c", "ch", "chr", "chrome", "chrome.", "chrome.e", "chrome.ex", "chrome.exe",
                     "chrome.ex", "c", "c\u030C", "d", "dz", "dzs", ".", ".n", ".net", ".net t", ".net tools",
                     "report", "report.pdf", "report.pdf h", "te", "tele", "telegram", "i", "is", "İs", "ı", "ıs",
                     "  ", "Google", " Google C ", "C:/", "Google Chrome" })
        {
            var expected = catalog.MatchGeneral(query);
            var actual = session.MatchGeneral(query);
            Assert.Equal(expected.Items.ToArray(), actual.Items.ToArray());
            Assert.Equal(expected.TotalCount, actual.TotalCount);
            Assert.Equal(expected.UniqueFirst, actual.UniqueFirst);
        }
    }

    private static StartMenuApplications Catalog(params StartMenuApplication[] apps) =>
        new(apps, CultureInfo.GetCultureInfo("en-US"));

    private static StartMenuApplication App(string id, string name, string detail = "") => new(id, name, detail);
}

public sealed class StartMenuCatalogTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ActivationReadsOffThreadAndCoalescesNotificationsIntoOneExtraRead()
    {
        int callCount = 0;
        int callerThread = Environment.CurrentManagedThreadId;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowFirstRead = new ManualResetEventSlim();
        var observations = new List<(int ThreadId, ApartmentState Apartment)>();
        using var catalog = new StartMenuCatalog(token =>
        {
            int call = Interlocked.Increment(ref callCount);
            observations.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
            if (call == 1)
            {
                started.SetResult();
                allowFirstRead.Wait(token);
            }
            return [new StartMenuApplication(call.ToString(CultureInfo.InvariantCulture), "Tool", "")];
        });

        var published = new List<string>();
        catalog.SnapshotChanged += () => published.Add(catalog.Current.MatchAll(null).Single().Id);
        Assert.False(catalog.IsReady);
        Assert.Empty(catalog.Current.MatchAll(null));

        catalog.OnActivated();
        var completion = catalog.RefreshCompletion;
        await started.Task.WaitAsync(Timeout);
        for (int signal = 0; signal < 30; signal++)
        {
            catalog.RequestRefresh();
            catalog.OnActivated();
        }

        Assert.Equal(1, Volatile.Read(ref callCount));
        allowFirstRead.Set();
        await completion.WaitAsync(Timeout);

        Assert.True(catalog.IsReady);
        Assert.Equal(2, callCount);
        Assert.Equal(["1", "2"], published);
        Assert.All(observations, read =>
        {
            Assert.NotEqual(callerThread, read.ThreadId);
            Assert.Equal(ApartmentState.STA, read.Apartment);
        });
        Assert.Equal(observations[0].ThreadId, observations[1].ThreadId);
    }

    [Fact]
    public async Task ActivationChecksTheTenMinuteDeadlineAndRetriesAFailedReadWithoutLosingResults()
    {
        var time = new TestClock();
        var errors = new List<Exception>();
        int callCount = 0;
        using var catalog = new StartMenuCatalog(
            _ => ++callCount == 2
                ? throw new InvalidOperationException("Test read failure")
                : [new StartMenuApplication("id", callCount == 1 ? "Before" : "After", "")],
            timeProvider: time,
            reportError: errors.Add);

        catalog.OnActivated();
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        var successfulSnapshot = catalog.Current;
        Assert.True(catalog.IsReady);
        Assert.Equal("Before", Assert.Single(successfulSnapshot.MatchAll(null)).Name);

        time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        catalog.OnActivated();
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.Equal(1, callCount);
        Assert.Same(successfulSnapshot, catalog.Current);

        time.Advance(TimeSpan.FromTicks(1));
        catalog.OnActivated();
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.Equal(2, callCount);
        Assert.True(catalog.IsReady);
        Assert.Same(successfulSnapshot, catalog.Current);
        Assert.IsType<InvalidOperationException>(Assert.Single(errors));

        catalog.OnActivated();
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.Equal(3, callCount);
        Assert.Equal("After", Assert.Single(catalog.Current.MatchAll(null)).Name);
        Assert.Equal("Before", Assert.Single(successfulSnapshot.MatchAll(null)).Name);
    }

    [Fact]
    public async Task CompleteWatchRefreshesOnNotificationAndActivationRetriesFailedReads()
    {
        var time = new TestClock();
        int reads = 0;
        using var catalog = new StartMenuCatalog(_ => ++reads == 2
            ? throw new InvalidOperationException("Test read failure")
            : [new StartMenuApplication(reads.ToString(CultureInfo.InvariantCulture), "Tool", "")],
            timeProvider: time);

        catalog.OnActivated(hasCompleteWatch: true);
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        var initial = catalog.Current;
        Assert.Equal("1", Assert.Single(initial.MatchAll(null)).Id);

        time.Advance(TimeSpan.FromDays(1));
        catalog.OnActivated(hasCompleteWatch: true);
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.Equal(1, reads);

        catalog.RequestRefresh();
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.Equal(2, reads);
        Assert.Same(initial, catalog.Current);

        catalog.OnActivated(hasCompleteWatch: true);
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.Equal("3", Assert.Single(catalog.Current.MatchAll(null)).Id);

        catalog.RequestRefresh();
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.Equal("4", Assert.Single(catalog.Current.MatchAll(null)).Id);

        time.Advance(TimeSpan.FromMinutes(10));
        catalog.OnActivated(hasCompleteWatch: false);
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.Equal(5, reads);
    }

    [Fact]
    public async Task InitialFailureLeavesCatalogUnreadyUntilSuccessfulEmptyRead()
    {
        int callCount = 0;
        using var catalog = new StartMenuCatalog(_ => ++callCount == 1
            ? throw new InvalidOperationException("Test read failure")
            : []);

        catalog.OnActivated();
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.False(catalog.IsReady);

        catalog.OnActivated();
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.True(catalog.IsReady);
        Assert.Empty(catalog.Current.MatchAll(null));
    }

    [Fact]
    public async Task DisposeCancelsWithoutWaitingAndSuppressesPendingReadsAndLatePublication()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowReadToReturn = new ManualResetEventSlim();
        int callCount = 0;
        int publications = 0;
        using var catalog = new StartMenuCatalog(token =>
        {
            Interlocked.Increment(ref callCount);
            started.SetResult(token);
            allowReadToReturn.Wait();
            return [new StartMenuApplication("id", "Too late", "")];
        });
        catalog.SnapshotChanged += () => Interlocked.Increment(ref publications);

        catalog.OnActivated();
        var completion = catalog.RefreshCompletion;
        try
        {
            CancellationToken token = await started.Task.WaitAsync(Timeout);
            catalog.RequestRefresh();

            await Task.Run(catalog.Dispose).WaitAsync(Timeout);
            Assert.True(token.IsCancellationRequested);
            catalog.RequestRefresh();
            catalog.OnActivated();
            Assert.False(completion.IsCompleted);
        }
        finally
        {
            allowReadToReturn.Set();
        }

        await completion.WaitAsync(Timeout);
        Assert.False(catalog.IsReady);
        Assert.Equal(1, callCount);
        Assert.Equal(0, publications);
    }

    [Fact]
    public async Task ItemNotificationsPatchOnlyTheIdentifiedRows()
    {
        var fullReads = 0;
        var peersRead = new List<string>();
        var original = new StartMenuApplication("one", "Editor", "");
        using var catalog = new StartMenuCatalog(_ =>
        {
            fullReads++;
            return [original, new("other", "Reader", "")];
        }, id => { peersRead.Add(id); return original with { Detail = "Windows" }; });
        catalog.OnActivated();
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        var initial = catalog.Current;

        async Task Notify(string? oldId, StartMenuApplication? app)
        {
            catalog.RequestChange(() => new(oldId, app));
            await catalog.RefreshCompletion.WaitAsync(Timeout);
        }

        await Notify(null, new("two", "Editor", "Portable"));
        Assert.Equal(["Windows", "Portable"], catalog.Current.MatchAll("Editor").Select(app => app.Detail));
        Assert.Equal(["one"], peersRead);
        Assert.Equal(["two"], catalog.Current.ChangedIds!.ToArray());
        await Notify(null, new("two", "Editor", "Updated"));
        Assert.Equal("Updated", catalog.Current.MatchAll("Editor").Last().Detail);
        await Notify("two", new("new-id", "Studio", ""));
        Assert.Empty(Assert.Single(catalog.Current.MatchAll("Editor")).Detail);
        Assert.Equal("new-id", Assert.Single(catalog.Current.MatchAll("Studio")).Id);
        Assert.True(catalog.Current.ChangedIds!.SetEquals(["two", "new-id"]));
        await Notify("new-id", null);
        Assert.Empty(catalog.Current.MatchAll("Studio"));
        Assert.Equal(initial.MatchAll(null).ToArray(), catalog.Current.MatchAll(null).ToArray());
        Assert.Equal(["one"], peersRead);
        Assert.Equal(1, fullReads);
    }

    [Theory]
    [InlineData("unresolved")]
    [InlineData("unknown-delete")]
    [InlineData("error")]
    [InlineData("conflicting-rename")]
    public async Task AmbiguousItemFallsBackToOneAuthoritativeRead(string kind)
    {
        int reads = 0;
        using var catalog = new StartMenuCatalog(_ => ++reads == 1
            ? [new("a", "First", ""), new("b", "Other", "")]
            : [new("c", "Current", "")], _ => null);
        catalog.OnActivated();
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        catalog.RequestChange(() => kind switch
        {
            "unknown-delete" => new("missing", null),
            "conflicting-rename" => new("a", new("b", "Renamed", "")),
            "error" => throw new InvalidOperationException(),
            _ => null
        });
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.Equal(2, reads);
        Assert.Equal("c", Assert.Single(catalog.Current.MatchAll(null)).Id);
        Assert.Null(catalog.Current.ChangedIds);
    }

    [Fact]
    public async Task BroadNotificationSupersedesQueuedItemsWhileFullReadIsInFlight()
    {
        var fullStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int reads = 0, resolutions = 0;
        using var catalog = new StartMenuCatalog(token =>
        {
            if (++reads == 2) { fullStarted.SetResult(); release.Wait(token); }
            return [new("base", "Base", "")];
        }, _ => null);
        catalog.RequestRefresh();
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        catalog.RequestRefresh();
        await fullStarted.Task.WaitAsync(Timeout);
        try
        {
            catalog.RequestChange(() => { resolutions++; return new(null, new("obsolete", "Obsolete", "")); });
            catalog.RequestChange(null);
        }
        finally { release.Set(); }
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.Equal(3, reads);
        Assert.Equal(0, resolutions);
        Assert.Equal("base", Assert.Single(catalog.Current.MatchAll(null)).Id);
    }

    [Fact]
    public async Task DeltaDoesNotPostponeTheFallbackDeadlineAndFailurePreservesTheSnapshot()
    {
        int reads = 0;
        var time = new TestClock();
        using var catalog = new StartMenuCatalog(_ => ++reads == 2
            ? throw new InvalidOperationException("full read failed")
            : [new("base", "Base", "")], _ => null, timeProvider: time);
        catalog.OnActivated();
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        time.Advance(TimeSpan.FromMinutes(9));
        catalog.RequestChange(() => new(null, new("new", "New", "")));
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        var patched = catalog.Current;
        Assert.Equal(2, patched.MatchAll(null).Count);
        time.Advance(TimeSpan.FromMinutes(1));
        catalog.OnActivated(hasCompleteWatch: false);
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.Equal(2, reads);
        Assert.Same(patched, catalog.Current);
        catalog.RequestChange(() => new("base", null));
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.Equal(3, reads);
        Assert.Equal("base", Assert.Single(catalog.Current.MatchAll(null)).Id);
    }

    [Fact]
    public async Task ActivationDuringAnItemReadQueuesAnOverdueFullRead()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var time = new TestClock();
        int reads = 0;
        using var catalog = new StartMenuCatalog(_ => [new((++reads).ToString(), "Base", "")],
            _ => null, timeProvider: time);
        catalog.OnActivated();
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        time.Advance(TimeSpan.FromMinutes(10));
        catalog.RequestChange(() =>
        {
            started.SetResult();
            release.Wait(Timeout);
            return new(null, new("delta", "Delta", ""));
        });
        await started.Task.WaitAsync(Timeout);
        try { catalog.OnActivated(hasCompleteWatch: false); }
        finally { release.Set(); }
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.Equal(2, reads);
        Assert.Equal("2", Assert.Single(catalog.Current.MatchAll(null)).Id);
    }

    [Fact]
    public async Task UnresolvedBatchAndFailedFullReadPreserveLastPublishedEntries()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int reads = 0;
        using var catalog = new StartMenuCatalog(_ => ++reads == 2
            ? throw new InvalidOperationException("Full read failed")
            : [new("base", "Base", "")], _ => null);
        catalog.OnActivated();
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        catalog.RequestChange(() =>
        {
            started.SetResult();
            release.Wait(Timeout);
            return new(null, new("first", "First", ""));
        });
        await started.Task.WaitAsync(Timeout);
        try
        {
            catalog.RequestChange(() => new("base", null));
            catalog.RequestChange(() => null);
        }
        finally { release.Set(); }
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.Equal(["base", "first"], catalog.Current.MatchAll(null).Select(app => app.Id));
        catalog.OnActivated(hasCompleteWatch: true);
        await catalog.RefreshCompletion.WaitAsync(Timeout);
        Assert.Equal(3, reads);
        Assert.Equal("base", Assert.Single(catalog.Current.MatchAll(null)).Id);
    }

    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
