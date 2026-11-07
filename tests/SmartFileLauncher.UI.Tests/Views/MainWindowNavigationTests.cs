using System.IO;
using System.Reflection;
using System.Windows;
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

[Collection("MainWindow search")]
public sealed class MainWindowNavigationTests
{
    private const string Root = @"C:\Test";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BackFromAFolderOpenedFromSearchRestoresTheResultsWithoutSearchingAgain(bool naturalLanguage) => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Search("fatura", naturalLanguage);
        var result = Assert.Single(fixture.Model.SearchResults);

        await fixture.Navigate(result.FullPath);
        Assert.Equal(result.FullPath, fixture.Model.CurrentFolderPath);
        Assert.Equal(string.Empty, fixture.SearchBox.Text);
        Assert.Equal(Visibility.Visible, fixture.Element("BackButton").Visibility);
        Assert.Equal(Visibility.Visible, fixture.Element("FolderPathBar").Visibility);

        await fixture.Back();

        Assert.Null(fixture.Model.CurrentFolderPath);
        Assert.Equal("fatura", fixture.SearchBox.Text);
        Assert.Same(result, Assert.Single(fixture.Model.SearchResults));
        Assert.Equal(Visibility.Visible, fixture.Element("ResultsContainer").Visibility);
        Assert.Equal(Visibility.Collapsed, fixture.Element("FolderPathBar").Visibility);
        Assert.Null(fixture.Window.FindName("ForwardButton"));
        Assert.Contains("İleri: ilk-fatura", (string)fixture.Element("BackButton").ToolTip);
        Assert.Equal(naturalLanguage ? 1 : 0, fixture.ParserCalls);
        Assert.Equal(naturalLanguage ? 1 : 0, fixture.LocalCalls);
        Assert.Equal(naturalLanguage ? 0 : 1, fixture.StandardCalls);

        await fixture.Forward();

        Assert.Equal(result.FullPath, fixture.Model.CurrentFolderPath);
        Assert.Equal(string.Empty, fixture.SearchBox.Text);
        Assert.Equal(Visibility.Collapsed, fixture.Element("ResultsContainer").Visibility);
        Assert.Equal(naturalLanguage ? 0 : 1, fixture.StandardCalls);
    });

    [Fact]
    public Task FileChangesWhileAwayRefreshTheRestoredAiSearchLocally() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        fixture.RefreshedName = "yeni-fatura";
        await fixture.Search("fatura", naturalLanguage: true);
        await fixture.Navigate(fixture.Model.SearchResults[0].FullPath);

        await fixture.Change(new FileChangeEvent { AffectedNames = ["yeni-fatura"] });
        await fixture.Back();
        await fixture.CurrentSearchCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, fixture.ParserCalls);
        Assert.Equal(2, fixture.LocalCalls);
        Assert.Equal("yeni-fatura", Assert.Single(fixture.Model.SearchResults).Name);
    });

    [Fact]
    public Task BackAfterGoingUpReturnsToTheChildInsteadOfTheParentsParent() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var child = Path.Combine(Root, "Belgeler", "Muhasebe");
        await fixture.Navigate(child);

        await fixture.Invoke("NavigateToParentAsync");
        Assert.Equal(Path.Combine(Root, "Belgeler"), fixture.Model.CurrentFolderPath);
        Assert.Contains(fixture.Model.DesktopIcons, icon => icon.IsTransferSelected && icon.FullPath == child);

        await fixture.Back();
        Assert.Equal(child, fixture.Model.CurrentFolderPath);

        await fixture.Back();
        Assert.Null(fixture.Model.CurrentFolderPath);
        Assert.Equal(Visibility.Collapsed, fixture.Element("BackButton").Visibility);
    });

    [Fact]
    public Task AFolderThatNoLongerExistsIsDroppedFromHistory() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        var removed = Path.Combine(Root, "Silinecek");
        var current = Path.Combine(Root, "Belgeler");
        await fixture.Navigate(removed);
        await fixture.Navigate(current);
        fixture.Missing.Add(removed);

        await fixture.Back();

        Assert.Equal(current, fixture.Model.CurrentFolderPath);
        await fixture.Back();
        Assert.Null(fixture.Model.CurrentFolderPath);
    });

    [Fact]
    public Task PathBarShowsTheTrailFromTheIndexedRoot() => RunSta(async () =>
    {
        using var fixture = new Fixture();
        await fixture.Navigate(Path.Combine(Root, "Belgeler", "Muhasebe"));

        var segments = (StackPanel)fixture.Element("FolderPathSegments");
        var labels = segments.Children.OfType<FrameworkElement>()
            .Select(element => element switch
            {
                Button { Content: TextBlock text } => text.Text,
                Button => "ev",
                Border { Child: TextBlock text } => text.Text,
                _ => null
            })
            .Where(label => label != null);

        Assert.Equal(["ev", "Test", "Belgeler", "Muhasebe"], labels);
        Assert.Equal("2 öğe", ((TextBlock)fixture.Element("FolderItemCountText")).Text);
    });

    internal static Task RunSta(Func<Task> action)
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

    internal sealed class Fixture : IDisposable
    {
        internal readonly MainWindowViewModel Model = new() { IsIndexed = true, IndexedRootPaths = [Root] };
        internal readonly HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase);
        internal int ParserCalls;
        internal int LocalCalls;
        internal int StandardCalls;
        internal string RefreshedName = "ilk-fatura";
        internal SearchResult[] StandardResults = [Result("ilk-fatura")];
        internal MainWindow Window { get; }

        internal Fixture()
        {
            var search = new SearchApplicationService(
                (_, _, _, _) => { Interlocked.Increment(ref StandardCalls); return StandardResults; },
                (_, _, _, _) =>
                {
                    var call = Interlocked.Increment(ref LocalCalls);
                    return [Result(call == 1 ? "ilk-fatura" : RefreshedName)];
                },
                (query, _) =>
                {
                    Interlocked.Increment(ref ParserCalls);
                    return Task.FromResult(new StructuredQuery { Keywords = [query] });
                },
                _ => throw new InvalidOperationException("Rule-based parser çağrılmamalı"));
            var settings = new AppSettings { ThumbnailPreviewsEnabled = false };
            Window = new MainWindow(Model, settings,
                Stub<ISettingsApplicationService>(), Stub<IIndexMaintenanceService>(),
                Stub<IIndexLifecycleService>((method, _) => method.Name switch
                {
                    "get_IsTokenIndexReady" or "get_IsInitialized" => true,
                    "get_ReconciliationStatus" => new IndexReconciliationStatus(false, 0, 0, 100),
                    "GetIndexedRoots" => Array.Empty<FileSystemNode>(),
                    _ => throw new NotSupportedException(method.Name)
                }),
                Stub<IIndexedLocationProvider>(), search,
                Stub<ISearchDiagnosticsService>((method, _) => method.Name == "Tokenize"
                    ? Array.Empty<string>() : throw new NotSupportedException(method.Name)),
                Stub<IThumbnailService>(),
                Stub<IFolderNavigationService>((method, args) => method.Name switch
                {
                    "OpenAsync" => Task.FromResult(Page((string)args[0]!)),
                    "GetParentWithinRoots" => Parent((string)args[0]!),
                    _ => throw new NotSupportedException(method.Name)
                }),
                Stub<IConnectivityMonitor>((method, _) => method.Name == "get_IsConnected"
                    ? true : throw new NotSupportedException(method.Name)),
                Stub<IFileOperationService>((method, args) => method.Name == "GetItemKind"
                    ? Missing.Contains((string)args[0]!) ? FileItemKind.Missing : FileItemKind.Directory
                    : throw new NotSupportedException(method.Name)),
                Stub<IApplicationShellService>(), new ApplicationLog(),
                ApplicationStartupOptions.Default, null);
        }

        internal TextBox SearchBox => (TextBox)Window.FindName("SearchBox");

        internal FrameworkElement Element(string name) => (FrameworkElement)Window.FindName(name);

        internal Task Search(string query, bool naturalLanguage)
        {
            Model.IsIndexed = false;
            Model.IsNaturalLanguageMode = naturalLanguage;
            SearchBox.Text = query;
            Model.IsIndexed = true;
            return Invoke("StartSearchAsync", query, new SearchRequest(query, naturalLanguage, true), true, null, false);
        }

        internal Task Navigate(string folder) => Invoke("NavigateToFolderAsync", folder, null);

        internal Task Back() => Invoke("NavigateBackAsync");

        internal Task Forward() => Invoke("NavigateForwardAsync");

        internal Task Change(FileChangeEvent change)
        {
            typeof(MainWindow).GetMethod("HandleFileSystemChange", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Window, [change]);
            return Invoke("ProcessFileChangeAsync");
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

        internal Task Invoke(string name, params object?[] args) =>
            (Task)typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(Window, args)!;

        public void Dispose()
        {
            Window.PrepareForShutdown();
            Window.Close();
        }

        private static FolderPage Page(string folder) => new(
            [
                new FolderEntry("Muhasebe", Path.Combine(folder, "Muhasebe"), true),
                new FolderEntry("not.txt", Path.Combine(folder, "not.txt"), false)
            ],
            false);

        private static string? Parent(string folder) =>
            string.Equals(folder.TrimEnd('\\'), Root, StringComparison.OrdinalIgnoreCase)
                ? null
                : Path.GetDirectoryName(folder.TrimEnd('\\'));

        internal static SearchResult Result(string name) => new()
        {
            Name = name, FullPath = Path.Combine(Root, name), IsDirectory = true, Score = 200
        };
    }

    private static T Stub<T>(Func<MethodInfo, object?[], object?>? invoke = null) where T : class
    {
        var stub = DispatchProxy.Create<T, ServiceStub>();
        ((ServiceStub)(object)stub).Call = invoke;
        return stub;
    }

    public class ServiceStub : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?>? Call { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.ReturnType == typeof(void) ? null
                : Call != null ? Call(method, args ?? []) : throw new NotSupportedException(method.Name);
    }
}
