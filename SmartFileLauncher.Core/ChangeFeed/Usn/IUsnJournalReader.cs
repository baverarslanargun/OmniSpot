namespace SmartFileLauncher.Core.ChangeFeed.Usn;

/// <summary>Volume-level access to the USN change journal.</summary>
public interface IUsnJournalReader : IDisposable
{
    /// <summary>Current journal identity and retained USN range.</summary>
    /// <exception cref="UsnJournalUnavailableException">
    /// The journal is disabled, being deleted, or otherwise unreadable.
    /// </exception>
    UsnJournalDescriptor QueryJournal();

    /// <summary>
    /// Reads one page of records starting at <paramref name="startUsn"/>.
    /// </summary>
    /// <exception cref="UsnJournalUnavailableException">
    /// The journal identity no longer matches <paramref name="journalId"/>, or
    /// the journal became unreadable.
    /// </exception>
    UsnReadPage ReadPage(long startUsn, ulong journalId);
}

/// <summary>Journal identity and retained range, from <c>FSCTL_QUERY_USN_JOURNAL</c>.</summary>
public readonly record struct UsnJournalDescriptor(
    ulong JournalId,
    long FirstUsn,
    long NextUsn,
    long LowestValidUsn,
    long MaxUsn,
    ulong MaximumSize,
    ulong AllocationDelta);

/// <summary>
/// One page of journal output: the continuation cursor plus the raw record
/// region, with the leading USN header already stripped.
/// </summary>
public sealed record UsnReadPage(long NextUsn, ReadOnlyMemory<byte> Records);
