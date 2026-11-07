using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Search.Paged;
using SmartFileLauncher.Core.Services;

namespace SmartFileLauncher.Core.Application.Indexing;

public sealed class SharedIndexLifecycleService : IIndexLifecycleService
{
    private readonly IChangeFeedRequestChannel _channel;
    private readonly IIndexedLocationProvider _locations;
    private readonly IndexScanMethod _method;
    private readonly IndexBuildOptions _buildOptions;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private PagedSearchState? _state;
    private SharedCatalogStatus? _status;
    private Task? _polling;
    private string? _session;
    private bool _disposed;
    private long _publishes;
    private DateTime? _lastPublish;
    private TimeSpan _publishDuration;
    private int _tokens;
    private int _partTokenCount = -1;
    private IndexLocations? _activeLocations;

    public SharedIndexLifecycleService(IChangeFeedRequestChannel channel, IIndexedLocationProvider locations,
        IndexScanMethod method, string controlPath, IndexBuildOptions? buildOptions = null)
    {
        _channel = channel; _locations = locations; _method = method;
        _buildOptions = IndexBuildOptions.Normalize(buildOptions);
        DatabasePath = Path.GetFullPath(controlPath);
    }

    public event Action<IndexProgress>? ProgressChanged;
    public event Action<FileChangeEvent>? FileChanged;
    public event Action<string>? Error;
    public event Action<string>? Notice;
    public event Action<int, int, int>? ReconciliationProgressChanged;
    public event Action<bool>? ReconciliationStateChanged;
    public bool IsInitialized { get; private set; }
    public bool IsTokenIndexReady => _status?.TokenIndexReady == true;
    public string DatabasePath { get; }
    public string? CatalogDirectory => _status?.Parts.FirstOrDefault() is { } part ? Path.GetDirectoryName(part.Directory) : null;
    public IndexReconciliationStatus ReconciliationStatus { get; private set; } = new(false, 100, 0, 0);

    public async Task<IndexStartupResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null) throw new InvalidOperationException("İndeks zaten başlatıldı.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var locations = _locations.Resolve();
        _activeLocations = locations;
        var marker = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "rebuild.request");
        await RequireBuildCapabilitiesAsync(linked.Token).ConfigureAwait(false);
        var response = await _channel.SendAsync(new(ChangeFeedProtocol.Version, ChangeFeedRequestKind.OpenCatalog,
            CatalogOptions: new(locations.RootPaths.Select(ChangeFeedDeliveryContract.EncodeScope).ToArray(), _method, File.Exists(marker),
                Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(DatabasePath.AsSpan()))), _buildOptions)), linked.Token).ConfigureAwait(false);
        var status = RequireStatus(response);
        _session = status.Session;
        while (true)
        {
            await ApplyStatusAsync(status, linked.Token).ConfigureAwait(false);
            if (status.Ready) break;
            await Task.Delay(100, linked.Token).ConfigureAwait(false);
            status = RequireStatus(await _channel.SendAsync(new(ChangeFeedProtocol.Version, ChangeFeedRequestKind.PollCatalog,
                Token: _session), linked.Token).ConfigureAwait(false));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        File.WriteAllText(DatabasePath, CatalogDirectory);
        if (File.Exists(marker)) File.Delete(marker);
        IsInitialized = true;
        Notice?.Invoke("Ortak servis kataloğu hazır.");
        _polling = Task.Run(PollLoopAsync);
        return new(locations.DesktopPath, locations.RootPaths, GetStats());
    }

    private async Task PollLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(200, _stop.Token).ConfigureAwait(false);
                try { await PollAsync(_stop.Token).ConfigureAwait(false); }
                catch (ChangeFeedUntrustedServerException) { throw; }
                catch (ChangeFeedPipeException)
                {
                    DropState();
                    _session = null;
                    Notice?.Invoke("İndeks servisine yeniden bağlanılıyor.");
                    await Task.Delay(1000, _stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception failure)
        {
            DropState();
            Error?.Invoke("Servis kataloğu güncellenemedi: " + failure.Message);
        }
    }

    private async Task<SharedCatalogStatus> PollAsync(CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var response = _session is null ? null : await _channel.SendAsync(new(ChangeFeedProtocol.Version,
                ChangeFeedRequestKind.PollCatalog, Token: _session), ct).ConfigureAwait(false);
            if (response is null || response.Status == ChangeFeedResponseStatus.StaleChain || response.Catalog?.Closed == true)
            {
                DropState();
                await RequireBuildCapabilitiesAsync(ct).ConfigureAwait(false);
                response = await _channel.SendAsync(new(ChangeFeedProtocol.Version, ChangeFeedRequestKind.OpenCatalog,
                    CatalogOptions: new(_activeLocations!.RootPaths.Select(ChangeFeedDeliveryContract.EncodeScope).ToArray(), _method,
                        CacheKey: Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(DatabasePath.AsSpan()))), BuildOptions: _buildOptions)), ct).ConfigureAwait(false);
                _session = RequireStatus(response).Session;
            }
            var status = RequireStatus(response);
            await ApplyStatusAsync(status, ct).ConfigureAwait(false);
            if (status.Ready) IsInitialized = true;
            return status;
        }
        finally { _pollGate.Release(); }
    }

    private async Task ApplyStatusAsync(SharedCatalogStatus status, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await Task.Run(() => ApplyStatus(status, linked.Token), linked.Token).ConfigureAwait(false);
    }

    private void ApplyStatus(SharedCatalogStatus status, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (status.Failure is not null) { DropState(); throw new IOException(status.Failure); }
        if (!status.Ready)
        {
            ProgressChanged?.Invoke(new() { Status = status.Phase, Phase = status.Phase, ItemCount = status.Items,
                ElapsedMs = status.ElapsedMilliseconds, IsIndeterminate = true, IsCatalogBuild = true });
            return;
        }
        if (_status?.Revision == status.Revision) return;
        var watch = Stopwatch.StartNew();
        var profile = new SharedCatalogProfile();
        var opened = new List<(CatalogStore Store, bool ExcludeRoot)>();
        PagedSearchState? next = null;
        try
        {
            foreach (var part in status.Parts)
            {
                CatalogStore? store;
                lock (_gate) store = _state?.Reuse(part.Directory, part.Frame);
                using (profile.Measure("client-open")) store ??= CatalogStore.Open(part.Directory, frame: part.Frame);
                opened.Add((store, part.ExcludeRoot));
                using var catalog = store.Acquire();
                if (catalog.Data.Generation != part.Generation || catalog.Data.RootPath != ChangeFeedDeliveryContract.DecodeScope(part.RootUtf16))
                    throw new InvalidDataException("Ortak katalog sürümü eşleşmedi.");
                if (!IsInitialized) ProgressChanged?.Invoke(new() { Status = "Arama adları ve sıralaması hazırlanıyor", Phase = "literal-preparing", ItemCount = status.Items, IsIndeterminate = true, IsCatalogBuild = true });
                using (profile.Measure("client-literal")) catalog.PrepareLiteralNameIndex(cancellationToken, _buildOptions,
                    (stage, milliseconds) => profile.Record(stage, milliseconds));
            }
            next = new PagedSearchState(opened);
            if (!status.TokenIndexReady) { _tokens = 0; _partTokenCount = -1; }
            else if (_partTokenCount != status.Tokens)
            {
                using var query = next.Acquire();
                using (profile.Measure("client-tokens")) _tokens = query.TokenCount;
                _partTokenCount = status.Tokens;
            }
            PagedSearchState? old;
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                old = _state;
                _state = next;
                next = null;
                _status = status;
                _publishes++;
                _lastPublish = DateTime.UtcNow;
                _publishDuration = watch.Elapsed;
            }
            old?.Dispose();
            if (!IsInitialized) profile.Save(Path.GetDirectoryName(DatabasePath)!, "client-ready");
        }
        catch
        {
            if (next is not null) next.Dispose();
            else foreach (var part in opened) part.Store.Dispose();
            throw;
        }
        if (IsInitialized)
            FileChanged?.Invoke(new() { ChangeType = FileChangeType.Modified, FullPath = "Katalog güncellendi" });
        else ProgressChanged?.Invoke(new() { Status = "Hazır", Phase = "ready", Percentage = 100,
            ItemCount = status.Items, TotalItemCount = status.Items, ElapsedMs = status.ElapsedMilliseconds });
        ProgressChanged?.Invoke(new() { Status = status.Phase, Phase = status.TokenIndexReady ? "tokens-ready" : "tokens-preparing",
            IsBackgroundPreparation = true, ItemCount = status.Items });
    }

    private async Task RequireBuildCapabilitiesAsync(CancellationToken ct)
    {
        var response = await _channel.SendAsync(new(ChangeFeedProtocol.Version, ChangeFeedRequestKind.GetCapabilities), ct).ConfigureAwait(false);
        if (response.Status == ChangeFeedResponseStatus.Unavailable) throw new ChangeFeedPipeException(response.Message ?? "İndeks servisi yanıt vermiyor.");
        if (response.Status != ChangeFeedResponseStatus.Ok || response.Capabilities?.Contains("shared-build-options-v1") != true ||
            response.Capabilities?.Contains("two-phase-index-v1") != true)
            throw new IOException("Tarama ve sıralama ayarları için OmniSpot hizmeti güncellenmeli.");
    }

    private SharedCatalogStatus RequireStatus(ChangeFeedResponse response)
    {
        if (response.Status == ChangeFeedResponseStatus.Unavailable) throw new ChangeFeedPipeException(response.Message ?? "İndeks servisi yanıt vermiyor.");
        if (response.Catalog is { } catalog && catalog.AppliedBuildOptions != _buildOptions)
            throw new IOException("OmniSpot hizmeti seçilen tarama ayarlarını uygulamadı.");
        return response.Status == ChangeFeedResponseStatus.Ok && response.Catalog is { } status ? status :
            throw new IOException(response.Message ?? "Güncel OmniSpot katalog servisine erişilemiyor.");
    }

    public async Task<bool> EnsureSyncedAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var response = await _channel.SendAsync(new(ChangeFeedProtocol.Version, ChangeFeedRequestKind.SynchronizeCatalog,
            RootPath: ChangeFeedDeliveryContract.EncodeScope(path), Token: _session), linked.Token).ConfigureAwait(false);
        if (response.Status != ChangeFeedResponseStatus.Ok || response.Catalog is not { AcceptedOperation: > 0 } accepted) return false;
        ReconciliationStatus = new(true, 0, 0, 1);
        ReconciliationStateChanged?.Invoke(true);
        try
        {
            while (true)
            {
                var status = await PollAsync(linked.Token).ConfigureAwait(false);
                if (status.CompletedOperation >= accepted.AcceptedOperation) return true;
                await Task.Delay(30, linked.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            ReconciliationStatus = new(false, 100, 1, 1);
            ReconciliationProgressChanged?.Invoke(1, 1, 100);
            ReconciliationStateChanged?.Invoke(false);
        }
    }

    public ISearchStateReader CreateSearchState(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _state?.Acquire() ?? (ISearchStateReader)SearchState.Empty;
        }
    }

    public IReadOnlyList<FileSystemNode> GetIndexedRoots(CancellationToken cancellationToken = default)
    {
        var state = CreateSearchState(cancellationToken);
        using var lease = state as IQuerySnapshotLease;
        return state is IQueryCatalogSnapshot catalog ? catalog.GetRoots(cancellationToken).Select(item => ToNode(item, null)).ToArray() : [];
    }

    private FileSystemNode ToNode(SearchItem item, FileSystemNode? parent)
    {
        FileSystemNode? node = null;
        return node = new(item.Name, item.FullPath, item.IsDirectory, parent,
            item.IsDirectory ? () => Children(item.FullPath, node!) : null)
        {
            Metadata = new() { SizeBytes = item.SizeBytes, CreatedTime = item.CreatedTime, LastWriteTime = item.LastWriteTime, OpenCount = item.OpenCount }
        };
    }
    private IReadOnlyList<FileSystemNode> Children(string path, FileSystemNode parent)
    {
        var state = CreateSearchState();
        using var lease = state as IQuerySnapshotLease;
        return state is IQueryCatalogSnapshot catalog ? catalog.GetChildren(path).Select(item => ToNode(item, parent)).ToArray() : [];
    }
    public IndexTokenMatches GetTokenMatches(string token, CancellationToken cancellationToken = default)
    {
        if (!IsTokenIndexReady) return new(0, []);
        var state = CreateSearchState(cancellationToken);
        using var lease = state as IQuerySnapshotLease;
        var matches = state.Get(token, cancellationToken);
        return new(matches.Count, matches.Take(3).Select(item => item.Name).ToArray());
    }
    public IndexStats GetStats() => new() { DatabasePath = DatabasePath, FileCount = _status?.Files ?? 0,
        DirectoryCount = _status?.Directories ?? 0, TokenCount = _tokens, LastScanTime = _lastPublish };
    public IndexDiagnosticsReport GetDiagnosticsReport() => new(0, null, TimeSpan.Zero, TimeSpan.Zero, 0, false,
        _publishes, _lastPublish, _publishDuration, _status?.Items ?? 0);

    public void RecordOpened(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = RecordAsync(path);
    }
    private async Task RecordAsync(string path)
    {
        try
        {
            var response = await _channel.SendAsync(new(ChangeFeedProtocol.Version, ChangeFeedRequestKind.RecordCatalogOpen,
                RootPath: ChangeFeedDeliveryContract.EncodeScope(path), Token: _session), _stop.Token).ConfigureAwait(false);
            if (response.Status != ChangeFeedResponseStatus.Ok) Notice?.Invoke("Kullanım geçmişi kaydedilemedi: " + response.Message);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception failure) { Notice?.Invoke("Kullanım geçmişi kaydedilemedi: " + failure.Message); }
    }

    private void DropState()
    {
        PagedSearchState? previous;
        lock (_gate) { previous = _state; _state = null; _status = null; _tokens = 0; _partTokenCount = -1; IsInitialized = false; }
        previous?.Dispose();
    }
    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _stop.Cancel();
        DropState();
        if (_session is not null) _ = CloseAsync(_session);
    }

    private async Task CloseAsync(string session)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _channel.SendAsync(new(ChangeFeedProtocol.Version, ChangeFeedRequestKind.CloseCatalog, Token: session), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
    }
}
