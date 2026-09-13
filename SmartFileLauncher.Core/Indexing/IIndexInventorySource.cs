using SmartFileLauncher.Core.Indexing.Ntfs;

namespace SmartFileLauncher.Core.Indexing;

public interface IIndexInventorySource
{
    Task<IIndexInventorySession?> ReadAsync(IReadOnlyList<string> roots,
        Action<IndexInventoryEntry> receive, CancellationToken cancellationToken);
}

public interface IIndexInventorySession : IAsyncDisposable
{
    Task<bool> ValidateAsync(CancellationToken cancellationToken);
}
