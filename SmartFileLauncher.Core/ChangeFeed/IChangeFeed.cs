namespace SmartFileLauncher.Core.ChangeFeed;

/// <summary>
/// A persistent, per-root change feed.
/// </summary>
/// <remarks>
/// <para>
/// Delivery is at-least-once. <see cref="Read"/> returns the same batch until
/// <see cref="Accept"/> advances the feed, so the consumer must commit the index
/// first and only then accept. A crash between the two replays the batch, which
/// is safe as long as the consumer applies events idempotently.
/// </para>
/// <para>
/// A directory event stands for its whole subtree: moving or renaming a
/// directory produces one event for the directory and none for its children.
/// </para>
/// <para>
/// A feed never reports a path outside <see cref="RootPath"/>. Implementations
/// are single-consumer and not thread-safe.
/// </para>
/// </remarks>
public interface IChangeFeed : IDisposable
{
    string ProviderId { get; }

    string RootPath { get; }

    ChangeFeedRootIdentity RootIdentity { get; }

    /// <summary>
    /// Reads every change since the last accepted position. Repeatable until
    /// <see cref="Accept"/>.
    /// </summary>
    ChangeFeedBatch Read(CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances the feed past the batch returned by the last <see cref="Read"/>.
    /// Does nothing when the last read reported a gap or was never called.
    /// </summary>
    void Accept();
}
