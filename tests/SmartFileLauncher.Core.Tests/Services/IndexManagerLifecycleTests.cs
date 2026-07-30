using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IndexManagerLifecycleTests
{
    [Fact]
    public async Task InitializeTwiceFromTheSameCache_ReplacesRatherThanDuplicatesMemoryState()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var file = workspace.CreateFile(Path.Combine("root", "document.txt"));
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            await manager.InitializeAsync(root);

            Assert.Single(manager.InvertedIndex.Get("document"), node =>
                string.Equals(node.FullPath, file, StringComparison.OrdinalIgnoreCase));
            Assert.Single(manager.MetadataMap, entry =>
                string.Equals(entry.Key, file, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(manager.GetNode(file));
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task ProgressObserver_CanDisposeWhileInitializationOwnsTheLifecycleGate()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "document.txt"));
        using var tokenizer = new BlockingTokenizer();
        using var observerEntered = new ManualResetEventSlim();
        var disposeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher, tokenizer);
        var notificationCount = 0;

        manager.OnProgress += _ =>
        {
            if (Interlocked.Exchange(ref notificationCount, 1) != 0)
                return;

            observerEntered.Set();
            manager.Dispose();
            disposeCompleted.TrySetResult();
        };

        try
        {
            var initializeTask = Task.Run(() => manager.InitializeAsync(root));

            Assert.True(observerEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(tokenizer.Entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(disposeCompleted.Task.IsCompleted);

            tokenizer.Release.Set();
            await initializeTask.WaitAsync(TimeSpan.FromSeconds(3));
            await disposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            tokenizer.Release.Set();
            manager.Dispose();
        }
    }

    private sealed class BlockingTokenizer : ITokenizer, IDisposable
    {
        private readonly BasicTokenizer _inner = new();

        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public IEnumerable<string> Tokenize(string input)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(5));
            return _inner.Tokenize(input);
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }
}
