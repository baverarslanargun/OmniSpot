using System.Diagnostics;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Application.Settings;

namespace SmartFileLauncher.Core.Application.Indexing;

public sealed class VolumeIndexLifecycleService : IIndexLifecycleService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SaveQuietPeriod = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RescanBackoff = TimeSpan.FromMinutes(10);
    private readonly IChangeFeedRequestChannel _channel;
    private readonly IIndexedLocationProvider _locations;
    private readonly string _directory;
    private readonly IndexBuildOptions _buildOptions;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private List<Volume> _volumes = [];
    private Task? _loop;
    private DateTime? _lastChange;
    private long _publishes;
    private bool _disposed;
    private bool _reportedFailure;

    private sealed class Volume(string root, IReadOnlyList<string>? scope, string snapshotPath)
    {
        internal readonly string Root = root;
        internal readonly IReadOnlyList<string>? Scope = scope;
        internal readonly string SnapshotPath = snapshotPath;
        internal FileIndex Index = null!;
        internal IndexCheckpoint Checkpoint;
        internal bool Dirty;
        internal DateTime LastChange = DateTime.UtcNow;
        internal long SavedUsn;
        internal long FirstUsn;
        internal DateTime LastRescan = DateTime.MinValue;
    }

    public VolumeIndexLifecycleService(IChangeFeedRequestChannel channel, IIndexedLocationProvider locations, string directory, IndexBuildOptions? buildOptions = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _buildOptions = IndexBuildOptions.Normalize(buildOptions);
    }

    public event Action<IndexProgress>? ProgressChanged;
    public event Action<FileChangeEvent>? FileChanged;
    public event Action<string>? Error { add { } remove { } }
    public event Action<string>? Notice;
    public event Action<int, int, int>? ReconciliationProgressChanged { add { } remove { } }
    public event Action<bool>? ReconciliationStateChanged { add { } remove { } }

    public bool IsInitialized { get; private set; }
    public bool IsTokenIndexReady => IsInitialized;
    public string DatabasePath => Path.Combine(_directory, "control.bin");
    public IndexReconciliationStatus ReconciliationStatus { get; private set; } = new(false, 100, 0, 0);

    public async Task<IndexStartupResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null) throw new InvalidOperationException("İndeks zaten başlatıldı.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var locations = _locations.Resolve();
        Directory.CreateDirectory(_directory);
        var marker = Path.Combine(_directory, "rebuild.request");
        var rebuild = File.Exists(marker);
        var clock = Stopwatch.StartNew();
        var volumes = new List<Volume>();
        foreach (var group in locations.RootPaths.GroupBy(UsnVolumeJournalReader.ResolveVolumeRoot, StringComparer.OrdinalIgnoreCase))
        {
            var roots = group.Select(Path.GetFullPath).ToArray();
            var scope = roots.Any(root => Path.TrimEndingDirectorySeparator(root).Equals(Path.TrimEndingDirectorySeparator(group.Key),
                StringComparison.OrdinalIgnoreCase)) ? null : roots;
            volumes.Add(await Task.Run(() => OpenVolumeAsync(group.Key, scope, rebuild, clock, linked.Token), linked.Token).ConfigureAwait(false));
        }
        lock (_gate) _volumes = volumes;
        IndexMemory.Release();
        if (rebuild) File.Delete(marker);
        await File.WriteAllTextAsync(DatabasePath, string.Join(Environment.NewLine, volumes.Select(volume => volume.SnapshotPath)), linked.Token)
            .ConfigureAwait(false);
        IsInitialized = true;
        _lastChange = DateTime.UtcNow;
        var stats = GetStats();
        ProgressChanged?.Invoke(new() { Status = "Hazır", Phase = "ready", Percentage = 100,
            ItemCount = stats.FileCount + stats.DirectoryCount, TotalItemCount = stats.FileCount + stats.DirectoryCount,
            ElapsedMs = clock.ElapsedMilliseconds });
        Notice?.Invoke("İndeks hazır.");
        _loop = Task.Run(LoopAsync);
        foreach (var volume in volumes) PrepareWords(volume.Index);
        return new IndexStartupResult(locations.DesktopPath, locations.RootPaths, stats);
    }

    private async Task<Volume> OpenVolumeAsync(string root, IReadOnlyList<string>? scope, bool rebuild, Stopwatch clock, CancellationToken ct)
    {
        var info = await SendAsync(ChangeFeedRequestKind.IndexVolumeOpen, root, null, null, ct).ConfigureAwait(false);
        var volume = new Volume(root, scope, Path.Combine(_directory, $"{info.VolumeSerial:X16}.idx"));
        if (!rebuild)
        {
            Report($"İndeks yükleniyor: {root}", 0, clock);
            var loaded = FileIndex.Load(volume.SnapshotPath, out var checkpoint);
            if (loaded is not null && loaded.RootReference == info.RootReference && checkpoint.VolumeSerial == info.VolumeSerial &&
                checkpoint.JournalId == info.JournalId && checkpoint.NextUsn >= info.FirstUsn && checkpoint.NextUsn <= info.NextUsn &&
                string.Equals(loaded.RootPath, root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                volume.Index = loaded;
                volume.Checkpoint = checkpoint;
                volume.SavedUsn = checkpoint.NextUsn;
                volume.FirstUsn = info.FirstUsn;
                return volume;
            }
            loaded?.Dispose();
        }
        (volume.Index, volume.Checkpoint) = await ScanAsync(root, clock, ct).ConfigureAwait(false);
        Save(volume);
        return volume;
    }

    private async Task<(FileIndex Index, IndexCheckpoint Checkpoint)> ScanAsync(string root, Stopwatch? clock, CancellationToken ct)
    {
        var info = await SendAsync(ChangeFeedRequestKind.IndexVolumeOpen, root, null, null, ct).ConfigureAwait(false);
        var checkpoint = new IndexCheckpoint(info.VolumeSerial, info.JournalId, info.NextUsn);
        var read = 0;
        IEnumerable<IndexEntry> Entries()
        {
            using var scanStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            string? token = null;
            Task<IndexVolumePageDto>? pending = null;
            try
            {
                while (true)
                {
                    var page = (pending ?? SendAsync(ChangeFeedRequestKind.IndexVolumeScan, root, token, null, scanStop.Token))
                        .GetAwaiter().GetResult();
                    pending = null;
                    if (!page.Done)
                    {
                        token = page.Token ?? throw new IOException("Tarama oturumu belirteci eksik.");
                        var nextToken = token;
                        pending = Task.Run(() => SendAsync(ChangeFeedRequestKind.IndexVolumeScan, root, nextToken, null, scanStop.Token), scanStop.Token);
                    }
                    foreach (var entry in IndexEntryCodec.UnpackEntries(page.Entries)) yield return entry;
                    read += page.Count;
                    if (clock is not null) Report($"Disk taranıyor: {root}", read, clock);
                    if (page.Done) yield break;
                }
            }
            finally
            {
                if (pending is not null)
                {
                    scanStop.Cancel();
                    try { pending.GetAwaiter().GetResult(); }
                    catch (Exception) { }
                }
            }
        }
        var index = await Task.Run(() => FileIndex.Build(root, info.RootReference, Entries(), ct), ct).ConfigureAwait(false);
        return (index, checkpoint);
    }

    private void Report(string status, int items, Stopwatch clock) =>
        ProgressChanged?.Invoke(new() { Status = status, Phase = status, ItemCount = items, ElapsedMs = clock.ElapsedMilliseconds,
            IsIndeterminate = true, IsCatalogBuild = true });

    private async Task<IndexVolumePageDto> SendAsync(ChangeFeedRequestKind kind, string root, string? token,
        IndexJournalPositionDto? journal, CancellationToken ct)
    {
        var response = await _channel.SendAsync(new ChangeFeedRequest(ChangeFeedProtocol.Version, kind, RootPath: root,
            Token: token, Journal: journal, IndexBuild: kind == ChangeFeedRequestKind.IndexVolumeScan && token is null ? _buildOptions : null), ct).ConfigureAwait(false);
        if (response.Status == ChangeFeedResponseStatus.Unavailable && response.Volume is null)
            throw new ChangeFeedPipeException(response.Message ?? "İndeks servisi yanıt vermiyor.");
        if (response.Status != ChangeFeedResponseStatus.Ok || response.Volume is null)
            throw new IOException(response.Message ?? "İndeks servisi isteği tamamlayamadı.");
        return response.Volume;
    }

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _wake.WaitAsync(PollInterval, ct).ConfigureAwait(false);
                try
                {
                    await PollAsync(ct).ConfigureAwait(false);
                    _reportedFailure = false;
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    if (!_reportedFailure) Notice?.Invoke("İndeks güncellemesi bekliyor: " + failure.Message);
                    _reportedFailure = true;
                }
                await SaveIfQuietAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<Volume> volumes;
            lock (_gate) volumes = _volumes;
            var changed = false;
            HashSet<string>? affectedNames = new(StringComparer.Ordinal);
            var includesDirectories = false;
            foreach (var volume in volumes)
            {
                while (true)
                {
                    var page = await SendAsync(ChangeFeedRequestKind.IndexVolumeChanges, volume.Root, null,
                        new IndexJournalPositionDto(volume.Checkpoint.JournalId, volume.Checkpoint.NextUsn), ct).ConfigureAwait(false);
                    if (page.RescanRequired)
                    {
                        if (DateTime.UtcNow - volume.LastRescan < RescanBackoff) break;
                        await RescanAsync(volume, RescanReason(volume.Checkpoint, page), ct).ConfigureAwait(false);
                        changed = true;
                        affectedNames = null;
                        break;
                    }
                    volume.FirstUsn = page.FirstUsn;
                    var states = IndexEntryCodec.UnpackStates(page.States);
                    if (states.Count > 0)
                    {
                        var result = volume.Index.Reconcile(states, out var impact, ct);
                        if (result.Applied > 0)
                        {
                            changed = true;
                            includesDirectories |= impact?.IncludesDirectories == true;
                            if (impact?.Names is not { } names) affectedNames = null;
                            else if (affectedNames != null)
                            {
                                affectedNames.UnionWith(names);
                                if (affectedNames.Count > 4096) affectedNames = null;
                            }
                            volume.LastChange = DateTime.UtcNow;
                        }
                    }
                    if (page.NextUsn != volume.Checkpoint.NextUsn)
                    {
                        volume.Checkpoint = volume.Checkpoint with { NextUsn = page.NextUsn };
                        volume.Dirty = true;
                    }
                    if (page.Done) break;
                }
            }
            if (!changed) return;
            _lastChange = DateTime.UtcNow;
            _publishes++;
            FileChanged?.Invoke(new()
            {
                ChangeType = FileChangeType.Modified,
                FullPath = "Katalog güncellendi",
                IsDirectory = includesDirectories,
                AffectedNames = affectedNames?.ToArray()
            });
        }
        finally { _pollGate.Release(); }
    }

    private static string RescanReason(IndexCheckpoint checkpoint, IndexVolumePageDto page) =>
        page.JournalId != checkpoint.JournalId ? "USN günlüğü yeniden oluşturuldu"
        : checkpoint.NextUsn < page.FirstUsn
            ? $"USN günlüğü Omni'nin konumunu sildi ({(page.FirstUsn - checkpoint.NextUsn) / 1048576} MB geride kalındı)"
            : "USN konumu günlüğün ilerisinde";

    private async Task RescanAsync(Volume volume, string reason, CancellationToken ct)
    {
        volume.LastRescan = DateTime.UtcNow;
        Notice?.Invoke($"{reason}; {volume.Root} yeniden taranıyor.");
        var (index, checkpoint) = await ScanAsync(volume.Root, null, ct).ConfigureAwait(false);
        FileIndex previous;
        lock (_gate)
        {
            previous = volume.Index;
            volume.Index = index;
            volume.Checkpoint = checkpoint;
        }
        Save(volume);
        previous.Dispose();
        IndexMemory.Release();
        PrepareWords(index);
    }

    private void PrepareWords(FileIndex index)
    {
        CancellationToken token;
        try { token = _stop.Token; }
        catch (ObjectDisposedException) { return; }
        _ = Task.Run(() =>
        {
            try
            {
                index.PrepareWords(token);
                IndexMemory.Release();
            }
            catch (Exception failure) when (failure is OperationCanceledException or ObjectDisposedException) { }
        });
    }

    private async Task SaveIfQuietAsync(CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<Volume> volumes;
            lock (_gate) volumes = _volumes;
            var now = DateTime.UtcNow;
            foreach (var volume in volumes)
                if (volume.Dirty && now - volume.LastChange >= SaveQuietPeriod &&
                    volume.SavedUsn < volume.FirstUsn + (volume.Checkpoint.NextUsn - volume.FirstUsn) / 4)
                    Save(volume);
        }
        finally { _pollGate.Release(); }
    }

    private void Save(Volume volume)
    {
        try
        {
            volume.Index.Save(volume.SnapshotPath, volume.Checkpoint);
            volume.Dirty = false;
            volume.SavedUsn = volume.Checkpoint.NextUsn;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Notice?.Invoke("İndeks diske kaydedilemedi: " + failure.Message);
        }
    }

    public async Task<bool> EnsureSyncedAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsInitialized) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await PollAsync(linked.Token).ConfigureAwait(false);
        return true;
    }

    public ISearchStateReader CreateSearchState(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _volumes.Count == 0 ? SearchState.Empty : new FileIndexReader(_volumes.Select(volume => (volume.Index, volume.Scope)).ToArray());
        }
    }

    public IReadOnlyList<FileSystemNode> GetIndexedRoots(CancellationToken cancellationToken = default)
    {
        var state = CreateSearchState(cancellationToken);
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
        return state is IQueryCatalogSnapshot catalog ? catalog.GetChildren(path).Select(item => ToNode(item, parent)).ToArray() : [];
    }

    public IndexTokenMatches GetTokenMatches(string token, CancellationToken cancellationToken = default) => new(0, []);

    public IndexStats GetStats()
    {
        List<Volume> volumes;
        lock (_gate) volumes = _volumes;
        var files = 0;
        var directories = 0;
        foreach (var volume in volumes)
        {
            var (volumeFiles, volumeDirectories) = volume.Index.Counts();
            files += volumeFiles;
            directories += volumeDirectories;
        }
        return new() { DatabasePath = DatabasePath, FileCount = files, DirectoryCount = directories, TokenCount = 0, LastScanTime = _lastChange };
    }

    public IndexDiagnosticsReport GetDiagnosticsReport()
    {
        var stats = GetStats();
        return new(0, null, TimeSpan.Zero, TimeSpan.Zero, 0, false, _publishes, _lastChange, TimeSpan.Zero, stats.FileCount + stats.DirectoryCount);
    }

    public void RecordOpened(string path) { }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        List<Volume> volumes;
        lock (_gate) volumes = _volumes;
        foreach (var volume in volumes)
        {
            if (volume.Dirty) Save(volume);
            volume.Index.Dispose();
        }
    }
}
