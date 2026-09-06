using System.Collections.Concurrent;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class FileWatcherServiceTests
{
    [Fact]
    public void StartStopStart_ReusesOneProcessorAndDeduplicatesRoots()
    {
        using var workspace = new TemporaryDirectory();
        var firstRoot = workspace.CreateDirectory("first");
        var secondRoot = workspace.CreateDirectory("second");
        var watcher = new FileWatcherService(debounceMs: 1);
        Task processorTask;

        try
        {
            watcher.Watch(firstRoot);
            watcher.Watch(firstRoot + Path.DirectorySeparatorChar);
            watcher.Watch(secondRoot);

            watcher.Start();
            processorTask = Assert.IsAssignableFrom<Task>(watcher.ProcessorTask);

            watcher.Start();
            Assert.Same(processorTask, watcher.ProcessorTask);

            watcher.Stop();
            watcher.Start();

            Assert.Same(processorTask, watcher.ProcessorTask);
            Assert.Equal(2, watcher.WatchedPathCount);
        }
        finally
        {
            watcher.Dispose();
        }

        Assert.True(processorTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Stop_WaitsForAnInFlightCallback()
    {
        var watcher = new FileWatcherService(debounceMs: 1);
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();

        watcher.OnChange += _ =>
        {
            callbackEntered.Set();
            releaseCallback.Wait(TimeSpan.FromSeconds(5));
        };

        try
        {
            watcher.Start();
            watcher.TriggerEvent(new FileChangeEvent { FullPath = "in-flight" });
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(2)));

            var stopTask = Task.Run(watcher.Stop);
            Assert.True(SpinWait.SpinUntil(() => !watcher.IsWatching, TimeSpan.FromSeconds(2)));
            Assert.False(stopTask.IsCompleted);

            releaseCallback.Set();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseCallback.Set();
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task PausedDispatch_KeepsEventsUntilDispatchIsResumed()
    {
        var watcher = new FileWatcherService(debounceMs: 1);
        var delivered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        watcher.OnChange += _ => delivered.TrySetResult();

        try
        {
            watcher.Start(dispatchPaused: true);
            watcher.TriggerEvent(new FileChangeEvent
            {
                ChangeType = FileChangeType.Created,
                FullPath = "captured-while-paused"
            });

            await Task.Delay(150);
            Assert.False(delivered.Task.IsCompleted);
            Assert.True(watcher.IsDispatchPaused);

            watcher.ResumeDispatch();

            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(watcher.IsDispatchPaused);
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Fact]
    public void FatalWatcherFault_DisablesTheWatcher()
    {
        var watcher = new FileWatcherService(debounceMs: 1);
        try
        {
            watcher.Start();

            watcher.TriggerFault(new IOException("Test: watcher fault."));

            Assert.False(watcher.IsWatching);
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task Dispose_FromOnChange_DoesNotWaitOnItsOwnProcessor()
    {
        var watcher = new FileWatcherService(debounceMs: 1);
        var callbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        watcher.OnChange += _ =>
        {
            watcher.Dispose();
            callbackCompleted.TrySetResult();
        };

        watcher.Start();
        var processorTask = Assert.IsAssignableFrom<Task>(watcher.ProcessorTask);
        watcher.TriggerEvent(new FileChangeEvent { FullPath = "dispose" });

        await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await processorTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(processorTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Debounce_PreservesDeleteCreateOrderAndSerializesCallbacks()
    {
        var watcher = new FileWatcherService(debounceMs: 25);
        var delivered = new ConcurrentQueue<FileChangeType>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCallbacks = 0;
        var maxActiveCallbacks = 0;

        watcher.OnChange += evt =>
        {
            var active = Interlocked.Increment(ref activeCallbacks);
            InterlockedExtensions.Max(ref maxActiveCallbacks, active);
            try
            {
                delivered.Enqueue(evt.ChangeType);
                Thread.Sleep(10);
                if (delivered.Count == 2)
                {
                    completed.TrySetResult();
                }
            }
            finally
            {
                Interlocked.Decrement(ref activeCallbacks);
            }
        };

        try
        {
            watcher.Start();
            watcher.TriggerEvent(new FileChangeEvent
            {
                ChangeType = FileChangeType.Deleted,
                FullPath = "replace-me",
                IsDirectory = true
            });
            watcher.TriggerEvent(new FileChangeEvent
            {
                ChangeType = FileChangeType.Created,
                FullPath = "replace-me",
                IsDirectory = true
            });

            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            watcher.Stop();

            Assert.Equal(
                new[] { FileChangeType.Deleted, FileChangeType.Created },
                delivered.ToArray());
            Assert.Equal(1, maxActiveCallbacks);
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task Debounce_PreservesGlobalStructuralEventOrderAcrossPaths()
    {
        var watcher = new FileWatcherService(debounceMs: 25);
        var delivered = new ConcurrentQueue<(FileChangeType Type, string Path)>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parent = Path.Combine("root", "parent");
        var child = Path.Combine(parent, "child.txt");

        watcher.OnChange += evt =>
        {
            delivered.Enqueue((evt.ChangeType, evt.FullPath));
            if (delivered.Count == 3)
            {
                completed.TrySetResult();
            }
        };

        try
        {
            watcher.Start();
            watcher.TriggerEvent(new FileChangeEvent
            {
                ChangeType = FileChangeType.Deleted,
                FullPath = parent,
                IsDirectory = true
            });
            watcher.TriggerEvent(new FileChangeEvent
            {
                ChangeType = FileChangeType.Deleted,
                FullPath = child,
                IsDirectory = false
            });
            watcher.TriggerEvent(new FileChangeEvent
            {
                ChangeType = FileChangeType.Created,
                FullPath = parent,
                IsDirectory = true
            });

            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(
                new[]
                {
                    (FileChangeType.Deleted, parent),
                    (FileChangeType.Deleted, child),
                    (FileChangeType.Created, parent)
                },
                delivered.ToArray());
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task ClearPendingEvents_DropsLocalStateWithoutSuppressingTheNextGeneration()
    {
        var watcher = new FileWatcherService(debounceMs: 500);
        var delivered = new ConcurrentQueue<FileChangeType>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        watcher.OnChange += evt =>
        {
            delivered.Enqueue(evt.ChangeType);
            completed.TrySetResult();
        };

        try
        {
            watcher.Start();
            watcher.TriggerEvent(new FileChangeEvent
            {
                ChangeType = FileChangeType.Created,
                FullPath = "same-path"
            });

            Assert.True(SpinWait.SpinUntil(
                () => watcher.PendingEventCount == 0,
                TimeSpan.FromSeconds(2)));

            watcher.ClearPendingEvents();
            watcher.TriggerEvent(new FileChangeEvent
            {
                ChangeType = FileChangeType.Modified,
                FullPath = "same-path"
            });

            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(new[] { FileChangeType.Modified }, delivered.ToArray());
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task ClearWatches_WinsAgainstAConcurrentStartWhileCallbackIsInFlight()
    {
        var watcher = new FileWatcherService(debounceMs: 1);
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();

        watcher.OnChange += _ =>
        {
            callbackEntered.Set();
            releaseCallback.Wait(TimeSpan.FromSeconds(5));
        };

        try
        {
            watcher.Start();
            watcher.TriggerEvent(new FileChangeEvent { FullPath = "in-flight" });
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(2)));

            var clearTask = Task.Run(watcher.ClearWatches);
            Assert.True(SpinWait.SpinUntil(() => !watcher.IsWatching, TimeSpan.FromSeconds(2)));

            watcher.Start();
            releaseCallback.Set();
            await clearTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(watcher.IsWatching);
            Assert.Equal(0, watcher.WatchedPathCount);
        }
        finally
        {
            releaseCallback.Set();
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task TwoPhysicalRoots_DeliverCreatedEventsThroughTheSharedProcessor()
    {
        using var workspace = new TemporaryDirectory();
        var firstRoot = workspace.CreateDirectory("physical-first");
        var secondRoot = workspace.CreateDirectory("physical-second");
        var firstFile = Path.Combine(firstRoot, "first.txt");
        var secondFile = Path.Combine(secondRoot, "second.txt");
        var expected = new HashSet<string>(new[] { firstFile, secondFile }, StringComparer.OrdinalIgnoreCase);
        var delivered = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new FileWatcherService(debounceMs: 25);

        watcher.OnChange += evt =>
        {
            if (evt.ChangeType != FileChangeType.Created || !expected.Contains(evt.FullPath))
                return;

            delivered.TryAdd(evt.FullPath, 0);
            if (delivered.Count == expected.Count)
            {
                completed.TrySetResult();
            }
        };

        try
        {
            watcher.Watch(firstRoot);
            watcher.Watch(secondRoot);
            watcher.Start();

            File.WriteAllText(firstFile, "first");
            File.WriteAllText(secondFile, "second");

            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains(firstFile, delivered.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(secondFile, delivered.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.NotNull(watcher.ProcessorTask);
        }
        finally
        {
            watcher.Dispose();
        }
    }

    [Fact]
    public void AWatcherError_StopsTheWatcherAndReportsAFault()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("kok");
        using var watcher = new FileWatcherService(debounceMs: 1);
        var faults = 0;
        var errors = 0;
        watcher.OnFault += _ => Interlocked.Increment(ref faults);
        watcher.OnError += _ => Interlocked.Increment(ref errors);

        watcher.Watch(root);
        watcher.Start();
        Assert.True(watcher.IsWatching);

        watcher.SimulateWatcherError(new IOException("Test: izleme sürdürülemiyor."));

        Assert.False(
            watcher.IsWatching,
            "FileSystemWatcher.Error sonrası izleyici çalışıyor sayılmamalı.");
        Assert.Equal(1, Volatile.Read(ref faults));
        Assert.Equal(1, Volatile.Read(ref errors));
    }

    [Fact]
    public async Task AWatcherError_StopsDeliveringLaterChanges()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("kok");
        using var watcher = new FileWatcherService(debounceMs: 1);
        var delivered = 0;
        watcher.OnChange += _ => Interlocked.Increment(ref delivered);

        watcher.Watch(root);
        watcher.Start();
        watcher.SimulateWatcherError(new IOException("Test: izleme sürdürülemiyor."));

        File.WriteAllText(Path.Combine(root, "sonra.txt"), "içerik");
        await Task.Delay(400);

        Assert.Equal(0, Volatile.Read(ref delivered));
    }

    [Fact]
    public async Task InitializeAndRescan_ReplacesRootsWithoutCreatingAnotherProcessor()
    {
        using var workspace = new TemporaryDirectory();
        var firstRoot = workspace.CreateDirectory("first");
        var nestedRoot = workspace.CreateDirectory(Path.Combine("first", "nested"));
        var secondRoot = workspace.CreateDirectory("second");
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(new[] { firstRoot, nestedRoot, secondRoot });
            var processorTask = Assert.IsAssignableFrom<Task>(watcher.ProcessorTask);

            Assert.Equal(2, watcher.WatchedPathCount);

            await manager.RescanAsync(firstRoot);

            Assert.Equal(1, watcher.WatchedPathCount);
            Assert.Same(processorTask, watcher.ProcessorTask);
        }
        finally
        {
            manager.Dispose();
        }
    }
}

internal static class InterlockedExtensions
{
    public static void Max(ref int location, int value)
    {
        var current = Volatile.Read(ref location);
        while (current < value)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
}
