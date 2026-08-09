using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class SearchConcurrencyTests
{
    [Fact]
    public async Task SearchesUseStableSnapshotsWhileTheIndexMutates()
    {
        var index = new InvertedIndex();
        var nodes = Enumerable.Range(0, 200)
            .Select(i => new FileSystemNode(
                $"document-{i}.txt",
                $@"C:\files\document-{i}.txt",
                false))
            .ToArray();

        foreach (var (node, indexValue) in nodes.Select((node, i) => (node, i)))
        {
            index.Add("document", node);
            index.Add($"token-{indexValue}", node);
        }

        var engine = new SearchEngine(
            index,
            new BasicTokenizer(),
            new BasicScoringStrategy());
        using var start = new ManualResetEventSlim();

        var writer = Task.Run(() =>
        {
            start.Wait();
            for (var iteration = 0; iteration < 500; iteration++)
            {
                var node = nodes[iteration % nodes.Length];
                index.RemoveByPath(node.FullPath);
                index.Add("document", node);
                index.Add($"token-{iteration % nodes.Length}", node);
            }
        });

        var readers = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                for (var iteration = 0; iteration < 250; iteration++)
                {
                    var results = engine.Search("document", 50);
                    Assert.InRange(results.Count, 0, 50);

                    var snapshot = index.CreateSnapshot();
                    _ = snapshot.GetPartial("doc").Count;
                    _ = snapshot.GetFuzzy("documant", cancellationToken: default).Count;
                    _ = snapshot.GetAllTokens().Count();
                }
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(readers.Append(writer))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEmpty(engine.Search("document", 50));
    }

    [Fact]
    public void SearchChecksCancellationAfterTokenizationBeforeEmptyResult()
    {
        var index = new InvertedIndex();
        index.Add(
            "document",
            new FileSystemNode("document.txt", @"C:\files\document.txt", false));
        using var cancellation = new CancellationTokenSource();
        var engine = new SearchEngine(
            index,
            new CancellingTokenizer(cancellation),
            new BasicScoringStrategy());

        Assert.Throws<OperationCanceledException>(() =>
            engine.Search("", cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ManagerSnapshotStaysConsistentDuringFileEvents()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        for (var i = 0; i < 20; i++)
        {
            workspace.CreateFile(Path.Combine("root", $"document-{i}.txt"));
        }

        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();

            var standard = new SearchEngine(
                manager.CreateSearchState,
                new BasicTokenizer(),
                new BasicScoringStrategy());
            var advanced = new AdvancedSearchEngine(
                manager.CreateSearchState,
                new BasicTokenizer(),
                new BasicScoringStrategy());
            var query = new StructuredQuery
            {
                Keywords = new List<string> { "document" }
            };
            using var start = new ManualResetEventSlim();

            var writer = Task.Run(() =>
            {
                start.Wait();
                for (var i = 0; i < 30; i++)
                {
                    var path = workspace.CreateFile(
                        Path.Combine("root", $"document-live-{i}.txt"));
                    manager.ApplyFileChange(new FileChangeEvent
                    {
                        ChangeType = FileChangeType.Created,
                        FullPath = path,
                        IsDirectory = false
                    });

                    File.Delete(path);
                    manager.ApplyFileChange(new FileChangeEvent
                    {
                        ChangeType = FileChangeType.Deleted,
                        FullPath = path,
                        IsDirectory = false
                    });
                }
            });

            var readers = Enumerable.Range(0, 3)
                .Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    for (var i = 0; i < 75; i++)
                    {
                        Assert.NotEmpty(standard.Search("document", 25));
                        Assert.NotEmpty(advanced.Search(query, 25));
                    }
                }))
                .ToArray();

            start.Set();
            await Task.WhenAll(readers.Append(writer))
                .WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            manager.Dispose();
        }
    }

    private sealed class CancellingTokenizer(
        CancellationTokenSource cancellation) : ITokenizer
    {
        private readonly BasicTokenizer _inner = new();

        public IEnumerable<string> Tokenize(string input)
        {
            cancellation.Cancel();
            return _inner.Tokenize(input);
        }
    }
}
