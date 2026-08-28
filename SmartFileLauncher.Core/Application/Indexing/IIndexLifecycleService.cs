using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;

namespace SmartFileLauncher.Core.Application.Indexing;

public interface IIndexLifecycleService : IDisposable
{
    event Action<IndexProgress>? ProgressChanged;
    event Action<FileChangeEvent>? FileChanged;
    event Action<string>? Error;
    event Action<int, int, int>? ReconciliationProgressChanged;
    event Action<bool>? ReconciliationStateChanged;

    bool IsInitialized { get; }
    string DatabasePath { get; }
    IndexReconciliationStatus ReconciliationStatus { get; }

    Task<IndexStartupResult> InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> EnsureSyncedAsync(
        string path,
        CancellationToken cancellationToken = default);
    IReadOnlyList<FileSystemNode> GetIndexedRoots(
        CancellationToken cancellationToken = default);
    IndexTokenMatches GetTokenMatches(
        string token,
        CancellationToken cancellationToken = default);
    SearchState CreateSearchState(
        CancellationToken cancellationToken = default);
    IndexStats GetStats();
    IndexDiagnosticsReport GetDiagnosticsReport();
    void RecordOpened(string path);
}
