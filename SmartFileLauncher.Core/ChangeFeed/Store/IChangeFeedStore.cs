namespace SmartFileLauncher.Core.ChangeFeed.Store;

public interface IChangeFeedStore
{
    ChangeFeedSubscription? ReadSubscription();

    void WriteSubscription(ChangeFeedSubscription subscription);

    void DeleteSubscription();

    IReadOnlyList<ChangeFeedQueueEntry> ReadPending();

    ChangeFeedQueueEntry Enqueue(
        string volumeId,
        ulong journalId,
        long fromUsn,
        long toUsn,
        IReadOnlyList<ChangeFeedRootDelivery> roots);

    void Acknowledge(long sequence);

    int DiscardUncommitted(string volumeId, ulong journalId, long committedUsn);
}
