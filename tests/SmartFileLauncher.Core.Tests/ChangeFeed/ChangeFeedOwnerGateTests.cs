using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedOwnerGateTests : IDisposable
{
    private static readonly string OwnerSid = TestStoreOwner.Sid;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private const string Root = @"C:\Kok";
    private const ulong JournalId = 7;
    private const string VolumeId = "ntfs-vsn:0x000000000000ABCD";

    private readonly TemporaryDirectory _storeRoot = new();

    public void Dispose() => _storeRoot.Dispose();

    [Theory]
    [InlineData("enqueue")]
    [InlineData("read")]
    [InlineData("acknowledge")]
    [InlineData("discard")]
    [InlineData("subscription")]
    [InlineData("delete")]
    [InlineData("repair")]
    [InlineData("overflow")]
    public async Task EveryStoreOperation_WaitsForAHeldOwnerScope(string operation)
    {
        var holder = CreateStore();
        var contender = CreateStore();

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var started = new ManualResetEventSlim();
        var finished = new ManualResetEventSlim();

        var blocker = Task.Run(() =>
        {
            using var scope = holder.EnterOwnerScope();
            entered.Set();
            release.Wait(Budget);
        });

        Assert.True(entered.Wait(Budget), "Kapı tutulamadı.");

        var worker = Task.Run(() =>
        {
            started.Set();
            Run(contender, operation);
            finished.Set();
        });

        Assert.True(started.Wait(Budget), "İkinci iş başlamadı.");
        Assert.False(
            finished.Wait(TimeSpan.FromMilliseconds(300)),
            $"{operation} kapı tutulurken tamamlandı.");

        release.Set();

        Assert.True(finished.Wait(Budget), $"{operation} kapı bırakılınca tamamlanmadı.");
        await blocker;
        await worker;
    }

    [Fact]
    public async Task ADifferentOwnerDirectory_IsNotSerializedByTheFirst()
    {
        using var otherRoot = new TemporaryDirectory();
        var first = CreateStore();
        var second = new FileSystemChangeFeedStore(
            ChangeFeedStoreLayout.ForOwner(otherRoot.Path, OwnerSid));

        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var finished = new ManualResetEventSlim();

        var blocker = Task.Run(() =>
        {
            using var scope = first.EnterOwnerScope();
            entered.Set();
            release.Wait(Budget);
        });

        Assert.True(entered.Wait(Budget), "Kapı tutulamadı.");

        var worker = Task.Run(() =>
        {
            Run(second, "enqueue");
            finished.Set();
        });

        Assert.True(
            finished.Wait(Budget),
            "Farklı sahip dizini birinci kapıda bekledi.");

        release.Set();
        await blocker;
        await worker;
    }

    [Fact]
    public void TheGate_ScopesByKeyRatherThanGlobally()
    {
        using var held = ChangeFeedOwnerGate.Enter("sahip-a");

        Assert.True(ChangeFeedOwnerGate.IsHeld("sahip-a"));
        Assert.False(ChangeFeedOwnerGate.IsHeld("sahip-b"));

        using var other = ChangeFeedOwnerGate.Enter("sahip-b");
        Assert.True(ChangeFeedOwnerGate.IsHeld("sahip-b"));
    }

    [Fact]
    public void AHeldScope_IsReentrantForTheSameThread()
    {
        var store = CreateStore();

        using var outer = store.EnterOwnerScope();

        store.Enqueue(VolumeId, JournalId, 0, 10, new[] { Delivery() });
        var slice = store.ReadPending(new ChangeFeedReadBudget(int.MaxValue, long.MaxValue));

        Assert.Single(slice.Entries);
    }

    [Fact]
    public async Task ACompoundReadModifyWrite_IsNotInterleaved()
    {
        var writer = CreateStore();
        var contender = CreateStore();
        var observed = new List<int>();

        using var entered = new ManualResetEventSlim();
        var finished = new ManualResetEventSlim();

        var compound = Task.Run(() =>
        {
            using var scope = writer.EnterOwnerScope();
            entered.Set();
            Thread.Sleep(200);
            writer.WriteSubscription(Subscription(Root));
            observed.Add(1);
        });

        Assert.True(entered.Wait(Budget), "Kapı tutulamadı.");

        var reader = Task.Run(() =>
        {
            contender.ReadSubscription();
            observed.Add(2);
            finished.Set();
        });

        Assert.True(finished.Wait(Budget), "Okuyucu tamamlanmadı.");
        await compound;
        await reader;

        Assert.Equal(new[] { 1, 2 }, observed);
    }

    private void Run(IChangeFeedStore store, string operation)
    {
        switch (operation)
        {
            case "enqueue":
                store.Enqueue(VolumeId, JournalId, 0, 10, new[] { Delivery() });
                break;
            case "read":
                store.ReadPending(new ChangeFeedReadBudget(int.MaxValue, long.MaxValue));
                break;
            case "acknowledge":
                store.Acknowledge(1);
                break;
            case "discard":
                store.DiscardUncommitted(VolumeId, JournalId, 5);
                break;
            case "subscription":
                store.WriteSubscription(Subscription(Root));
                break;
            case "delete":
                store.DeleteSubscription();
                break;
            case "repair":
                Corrupt();
                store.WriteSubscription(Subscription(Root));
                store.ReadPending(new ChangeFeedReadBudget(int.MaxValue, long.MaxValue));
                break;
            case "overflow":
                store.Enqueue(VolumeId, JournalId, 0, 10, new[] { Delivery() });
                store.Enqueue(VolumeId, JournalId, 10, 20, new[] { Delivery() });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private void Corrupt()
    {
        var queue = ChangeFeedStoreLayout.ForOwner(_storeRoot.Path, OwnerSid).QueueDirectory;
        Directory.CreateDirectory(queue);
        File.WriteAllText(Path.Combine(queue, "0000000000000000009.json"), "{bozuk");
    }

    private static ChangeFeedSubscription Subscription(string rootPath) =>
        new(
            OwnerSid,
            new[]
            {
                new ChangeFeedSubscribedRoot(
                    rootPath,
                    new ChangeFeedRootIdentity("ntfs-vsn:0x0000000000000001", "0x0000000000000002"),
                    ChangeFeedRootGeneration.New())
            });

    private static ChangeFeedRootDelivery Delivery() =>
        new(
            Root,
            ChangeFeedBatch.Ok(new[]
            {
                new ChangeFeedEvent(ChangeFeedEventKind.Created, Path.Combine(Root, "yeni.txt"), false)
            }),
            ChangeFeedRootGeneration.New());

    private FileSystemChangeFeedStore CreateStore() =>
        new(ChangeFeedStoreLayout.ForOwner(_storeRoot.Path, OwnerSid));
}
