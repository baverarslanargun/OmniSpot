using SmartFileLauncher.Core.ChangeFeed.Store;
using Xunit;

namespace SmartFileLauncher.Core.Tests.TestInfrastructure;

internal static class ChangeFeedStoreTestExtensions
{
    public static ChangeFeedQueueEntry EnqueueOne(
        this IChangeFeedStore store,
        string volumeId,
        ulong journalId,
        long fromUsn,
        long toUsn,
        IReadOnlyList<ChangeFeedRootDelivery> roots) =>
        Assert.Single(store.Enqueue(volumeId, journalId, fromUsn, toUsn, roots));
}
