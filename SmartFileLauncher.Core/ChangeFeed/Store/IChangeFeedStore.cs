namespace SmartFileLauncher.Core.ChangeFeed.Store;

public interface IChangeFeedStore
{
    IDisposable EnterOwnerScope(CancellationToken cancellationToken = default);

    ChangeFeedSubscription? ReadSubscription();

    void WriteSubscription(ChangeFeedSubscription subscription);

    void DeleteSubscription();

    ChangeFeedQueueEpoch ReadEpoch();

    ChangeFeedSecurityStamp ReadSecurityStamp();

    void NoteSecurityChange();

    ChangeFeedWatcherLease ReadLease();

    ChangeFeedWatcherLease HoldLease(TimeSpan duration);

    void ReleaseLease();

    ChangeFeedQueueSlice ReadPending(ChangeFeedReadBudget? budget = null);

    IReadOnlyList<ChangeFeedQueueEntry> Enqueue(
        string volumeId,
        ulong journalId,
        long fromUsn,
        long toUsn,
        IReadOnlyList<ChangeFeedRootDelivery> roots);

    void Acknowledge(long sequence);

    int DiscardUncommitted(string volumeId, ulong journalId, long committedUsn);
}
