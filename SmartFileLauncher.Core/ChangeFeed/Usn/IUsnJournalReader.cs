namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public interface IUsnJournalReader : IDisposable
{
    UsnJournalDescriptor QueryJournal();

    UsnReadPage ReadPage(long startUsn, ulong journalId);
}

public readonly record struct UsnJournalDescriptor(
    ulong JournalId,
    long FirstUsn,
    long NextUsn,
    long LowestValidUsn,
    long MaxUsn,
    ulong MaximumSize,
    ulong AllocationDelta);

public sealed record UsnReadPage(long NextUsn, ReadOnlyMemory<byte> Records);
