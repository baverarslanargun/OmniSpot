using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Indexing;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Services;

public partial class IndexManager
{
    private sealed record LiveRoot(string Path, string Directory);
    private sealed record LiveControl(int Version, bool BuildComplete, bool BootstrapPending, long LastScanUtc, LiveRoot[] Roots, string[] PendingRepairs, string[]? RootRepairs = null, string[]? LinkScopes = null);
    private LiveControl? _liveControl;
    private readonly List<LiveCatalogStore> _liveStores = [];
    private readonly object _liveWriteGate = new();
    private readonly HashSet<string> _liveRootRepairs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _liveLinkScopes = new(StringComparer.OrdinalIgnoreCase);
    private bool _useLiveCatalog;
    private string? _liveStoragePath;
    internal bool UsesLiveCatalog => _useLiveCatalog;
    private string LiveDirectory => _liveStoragePath ?? _db.DatabasePath + ".live";
    private bool IsLiveStoragePath(string path) => _useLiveCatalog && IsSameOrDescendantPath(path, LiveDirectory);
    private string LiveControlPath => Path.Combine(LiveDirectory, "control.bin");
    internal string[] LiveWatcherRoots { get; private set; } = [];
    internal string[] LiveServiceRoots { get; private set; } = [];
    internal bool LiveCoverageReady { get; private set; }
    internal void UpdateLiveCoverage(string[] serviceRoots, string[] watcherRoots)
    {
        LiveServiceRoots = serviceRoots;
        LiveCoverageReady = true;
        Interlocked.Exchange(ref _liveResubscribeNeeded, 0);
        watcherRoots = NormalizeRootPaths(watcherRoots.Concat(_liveLinkScopes)).ToArray();
        if (!LiveWatcherRoots.SequenceEqual(watcherRoots, StringComparer.OrdinalIgnoreCase))
        { LiveWatcherRoots = watcherRoots; SetupWatchers(watcherRoots); }
    }

    public void EnableLiveCatalog()
    {
        if (_isInitialized || _layout != SearchStateLayout.Compact) throw new InvalidOperationException("Live katalog açılıştan önce compact arama ile seçilmeli.");
        _useLiveCatalog = true;
        _liveStoragePath = Path.GetFullPath(_db.DatabasePath + ".live");
    }

    internal async Task InitializeLiveAsync(IReadOnlyList<string> requestedRoots, IIndexInventorySource? inventory,
        ChangeFeedIndexBridge? bridge, CancellationToken ct)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        var started = Stopwatch.StartNew();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopBackgroundSyncAsync().ConfigureAwait(false);
            _watcher.Stop(); _watcher.ClearWatches();
            _activeRootPaths = NormalizeRootPaths(requestedRoots).OrderBy(path => path.Length)
                .Aggregate(new List<string>(), (kept, path) => { if (!kept.Any(root => IsSameOrDescendantPath(path, root))) kept.Add(path); return kept; });
            Directory.CreateDirectory(LiveDirectory);
            try { _liveControl = ReadLiveControl(); }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
            { NotifyError("Live katalog başlığı okunamadı: " + error.Message); _liveControl = null; }
            var cached = _liveControl is { Version: 1, BuildComplete: true } &&
                !File.Exists(Path.Combine(LiveDirectory, "rebuild.request")) &&
                _liveControl.Roots.Select(root => root.Path).SequenceEqual(_activeRootPaths, StringComparer.OrdinalIgnoreCase);
            if (cached)
            {
                ReportProgress("Katalog açılıyor...", 0, 0, started.ElapsedMilliseconds, isIndeterminate: true, phase: "cache_load");
                try
                {
                    await Task.Run(() => { foreach (var root in _liveControl!.Roots) _liveStores.Add(new LiveCatalogStore(LiveRootDirectory(root))); }, ct).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidDataException)
                {
                    foreach (var store in _liveStores) store.Dispose(); _liveStores.Clear(); cached = false;
                    NotifyError("Katalog dosyaları doğrulanamadı; yeni katalog oluşturulacak: " + error.Message);
                }
            }
            if (!cached)
            {
                _liveControl = new(1, false, true, DateTime.UtcNow.Ticks, [], []);
                SaveLiveControl(_liveControl);
                File.Delete(Path.Combine(LiveDirectory, "rebuild.request"));
            }
            ReportProgress("Değişiklik takibi hazırlanıyor...", 0, 0, started.ElapsedMilliseconds, isIndeterminate: true, phase: "live_feed_prepare");
            var prepared = bridge is null
                ? new ContinuousPreparation([], _activeRootPaths.ToArray(), true, null)
                : await bridge.PrepareContinuousAsync(_activeRootPaths, !cached, ct).ConfigureAwait(false);
            if (cached) foreach (var path in _liveControl!.LinkScopes ?? []) _liveLinkScopes.Add(path);
            LiveWatcherRoots = NormalizeRootPaths(prepared.WatcherRoots.Concat(_liveLinkScopes)).ToArray(); LiveServiceRoots = prepared.ServiceRoots;
            LiveCoverageReady = prepared.Available;
            if (!prepared.Available && !cached) throw new IOException(prepared.Diagnostic);
            if (!prepared.Available) NotifyError(prepared.Diagnostic ?? "USN servisi bekleniyor; önbellek kullanılacak.");
            if (LiveWatcherRoots.Length > 0 && !SetupWatchers(LiveWatcherRoots, dispatchPaused: true, captureAllChanges: true))
                throw new IOException("USN dışındaki kökler izlenemedi.");
            if (!cached)
            {
                await BuildLiveAsync(inventory, started, ct).ConfigureAwait(false);
                _liveControl = _liveControl! with { BuildComplete = true, Roots = _liveStores.Select(store => new LiveRoot(store.RootPath, Path.GetFileName(store.DirectoryPath))).ToArray(), LinkScopes = _liveLinkScopes.ToArray() };
                SaveLiveControl(_liveControl);
            }
            lock (_lock)
            {
                PublishLiveState(); _compactRootAvailable = true;
                _compactSingleRootPath = _activeRootPaths.Count == 1 ? _activeRootPaths[0] : null;
                foreach (var path in _liveControl!.PendingRepairs) _pendingRepairs[ValidateRepairPath(SmartFileLauncher.Core.ChangeFeed.Ipc.ChangeFeedDeliveryContract.DecodeScope(path))] = ++_repairVersion;
                foreach (var path in _liveStores.SelectMany(store => store.PendingRepairs))
                    _pendingRepairs[ValidateRepairPath(SmartFileLauncher.Core.ChangeFeed.Ipc.ChangeFeedDeliveryContract.DecodeScope(path))] = ++_repairVersion;
                foreach (var path in _liveControl.RootRepairs ?? []) _liveRootRepairs.Add(ValidateRepairPath(path));
                _isInitialized = true; _changeFeedGuarding = true; _changeFeedCoversDowntime = prepared.Available;
            }
            if (prepared.Available && bridge is not null && LiveServiceRoots.Length > 0)
            {
                ReportProgress("Son değişiklikler uygulanıyor...", 0, CurrentSearchState.ItemCount, started.ElapsedMilliseconds, isIndeterminate: true, phase: "handoff");
                var drained = await bridge.DrainContinuousAsync(ct).ConfigureAwait(false);
                var consumed = await bridge.ConsumeContinuousAsync(ct).ConfigureAwait(false);
                if (drained && consumed.Available && consumed.CaughtUp) CompleteLiveBootstrap();
                else NotifyError(consumed.Diagnostic ?? "Kalan USN teslimleri arka planda uygulanacak.");
            }
            else if (prepared.Available) CompleteLiveBootstrap();
            if (LiveWatcherRoots.Length > 0) _watcher.ResumeDispatch();
            ReleaseCompactStartupWorkspace();
            ReportProgress(cached ? "Katalog hazır" : "İndeksleme tamamlandı", 100, CurrentSearchState.ItemCount,
                started.ElapsedMilliseconds, phase: "ready");
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task BuildLiveAsync(IIndexInventorySource? inventory, Stopwatch started, CancellationToken ct)
    {
        var builders = _activeRootPaths.Select(root => new LiveCatalog(Path.Combine(LiveDirectory, "root-" + Guid.NewGuid().ToString("N")), root, _liveControl!.LastScanUtc)).ToArray();
        var accepted = 0; long lastProgress = 0;
        void Progress()
        {
            if (started.ElapsedMilliseconds - lastProgress < 250) return;
            lastProgress = started.ElapsedMilliseconds;
            ReportProgress("Dosyalar okunuyor ve kataloğa yerleştiriliyor...", 0, accepted, started.ElapsedMilliseconds, isIndeterminate: true, phase: "live_inventory");
        }
        void Receive(ReconciliationEntry entry)
        {
            if (IsLiveStoragePath(entry.Path)) return;
            if (entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) != 0 && !IsHiddenOrSystem(entry.Attributes))
                TrackLiveLink(entry.Path, dispatchPaused: true);
            var builder = builders.First(candidate => IsSameOrDescendantPath(entry.Path, candidate.RootPath));
            try { builder.Add(ToLiveRecord(entry, builder.RootPath)); }
            catch (Exception error) { throw new InvalidOperationException("Live katalog kaydı yazılamadı: " + entry.Path, error); }
            accepted++; Progress();
        }
        try
        {
            ReportProgress("Dosyalar okunuyor ve kataloğa yerleştiriliyor...", 0, 0, started.ElapsedMilliseconds, isIndeterminate: true, phase: "live_inventory");
            if (inventory is not null)
            {
                var excluded = new List<string>();
                await using var session = await inventory.ReadAsync(_activeRootPaths, entry =>
                {
                    Receive(new(entry.Path, entry.IsDirectory, entry.LastWriteTimeUtc, entry.SizeBytes, entry.Attributes, entry.CreatedTimeUtc));
                    if (IsHiddenOrSystem(entry.Attributes) && !_activeRootPaths.Contains(entry.Path, StringComparer.OrdinalIgnoreCase)) excluded.Add(entry.Path);
                }, ct).ConfigureAwait(false);
                if (session is null || !await session.ValidateAsync(ct).ConfigureAwait(false)) throw new IOException("MFT envanteri doğrulanamadı.");
                foreach (var path in excluded.OrderBy(path => path.Length)) builders.First(builder => IsSameOrDescendantPath(path, builder.RootPath)).ApplyMutation(new(path, null));
                foreach (var link in NormalizeRootPaths(_liveLinkScopes))
                {
                    var builder = builders.First(candidate => IsSameOrDescendantPath(link, candidate.RootPath));
                    var captured = CaptureDiskSnapshot([link], ct, followReparsePoints: true, entrySink: entry =>
                    {
                        try { builder.ApplyMutation(new(entry.Path, ToLiveRecord(entry, builder.RootPath))); }
                        catch (Exception error) { throw new InvalidOperationException("Bağlantı kataloğu yazılamadı.", error); }
                    });
                    if (captured.UnreadableScopes.Count > 0) throw new IOException("MFT bağlantı kapsamı okunamadı.");
                }
            }
            else
            {
                var snapshot = await Task.Run(() => CaptureDiskSnapshot(_activeRootPaths, ct, followReparsePoints: true, entrySink: Receive), ct).ConfigureAwait(false);
                if (snapshot.Errors.Count > 0) NotifyError($"İlk taramada {snapshot.Errors.Count} erişilemeyen kapsam atlandı: {string.Join(" | ", snapshot.Errors.Take(3))}");
            }
            ReportProgress("Katalog yazımı tamamlanıyor...", 0, accepted, started.ElapsedMilliseconds, isIndeterminate: true, phase: "live_flush");
            await Task.Run(() =>
            {
                foreach (var builder in builders)
                {
                    ct.ThrowIfCancellationRequested();
                    if (builder.FindCurrentRecord(builder.RootPath) is null) throw new IOException("İndeks kökü okunamadı: " + builder.RootPath);
                    _liveStores.Add(new LiveCatalogStore(Path.Combine(LiveDirectory, builder.StorageDirectoryName), builder));
                }
            }, ct).ConfigureAwait(false);
        }
        catch { foreach (var builder in builders) builder.Dispose(); throw; }
    }

    private PackedRecord ToLiveRecord(ReconciliationEntry entry, string root, int openCount = 0)
    {
        var parent = string.Equals(entry.Path, root, StringComparison.OrdinalIgnoreCase) ? "" : Path.GetDirectoryName(entry.Path);
        var item = entry.IsDirectory ? CompactDirectoryItem(PathName(entry.Path), entry.Path, parent)
            : new SearchItem(PathName(entry.Path), entry.Path, false, entry.SizeBytes,
                new DateTime(entry.CreatedTimeUtc, DateTimeKind.Utc).ToLocalTime(), new DateTime(entry.LastWriteTimeUtc, DateTimeKind.Utc).ToLocalTime(), openCount, parent);
        return new(item, entry.LastWriteTimeUtc, entry.CreatedTimeUtc, (entry.Attributes & FileAttributes.Hidden) != 0,
            (entry.Attributes & FileAttributes.System) != 0, _liveControl!.LastScanUtc);
    }
    private void PublishLiveState() => Volatile.Write(ref _publishedSearchState, new LiveSearchState(_liveStores.Select(store => store.State).ToArray()));
    private void TrackLiveLink(string path, bool dispatchPaused)
    {
        if (LiveWatcherRoots.Any(root => IsSameOrDescendantPath(path, root))) return;
        _liveLinkScopes.Add(path);
        LiveWatcherRoots = NormalizeRootPaths(LiveWatcherRoots.Append(path)).ToArray();
        _watcher.Watch(path); _watcher.Start(dispatchPaused, captureAllChanges: true);
        if (_isInitialized) lock (_lock) SaveLiveControl(_liveControl! with { LinkScopes = _liveLinkScopes.ToArray() });
    }
    internal void CompleteLiveBootstrap()
    {
        lock (_lock)
        {
            if (_liveControl is not { BootstrapPending: true } || _pendingRepairs.Count > 0) return;
            SaveLiveControl(_liveControl with { BootstrapPending = false });
            CleanupLiveGenerations();
        }
    }
    private string LiveRootDirectory(LiveRoot root)
    {
        if (!root.Directory.StartsWith("root-", StringComparison.Ordinal) || Path.GetFileName(root.Directory) != root.Directory || !Guid.TryParseExact(root.Directory[5..], "N", out _))
            throw new InvalidDataException("Live katalog dizini geçersiz.");
        return Path.Combine(LiveDirectory, root.Directory);
    }
    private void CleanupLiveGenerations()
    {
        var kept = _liveControl!.Roots.Select(root => root.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(LiveDirectory, "root-*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (kept.Contains(name) || !Guid.TryParseExact(name[5..], "N", out _) ||
                !string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), Path.GetFullPath(LiveDirectory), StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private LiveControl? ReadLiveControl()
    {
        if (!File.Exists(LiveControlPath)) return null;
        var bytes = File.ReadAllBytes(LiveControlPath);
        if (bytes.Length < 32 || bytes.Length > MaximumPendingRepairBytes + 65536 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)), bytes.AsSpan(bytes.Length - 32)))
            throw new InvalidDataException("Live kontrol checksum uyuşmuyor.");
        return JsonSerializer.Deserialize<LiveControl>(bytes.AsSpan(0, bytes.Length - 32));
    }
    private void SaveLiveControl(LiveControl control)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(control);
        using (var output = new FileStream(LiveControlPath + ".next", FileMode.Create, FileAccess.Write, FileShare.None))
        { output.Write(bytes); output.Write(SHA256.HashData(bytes)); output.Flush(true); }
        File.Move(LiveControlPath + ".next", LiveControlPath, true); _liveControl = control;
    }
    private IndexStats GetLiveStats()
    {
        lock (_lock) return new() { FileCount = _liveStores.Sum(store => store.State.ItemCount - store.DirectoryCount),
            DirectoryCount = _liveStores.Sum(store => store.DirectoryCount), TokenCount = CurrentSearchState.TokenCount,
            DatabasePath = LiveControlPath, LastScanTime = _liveControl is null ? null : new DateTime(_liveControl.LastScanUtc, DateTimeKind.Utc).ToLocalTime() };
    }
    private void IncrementLiveOpenCount(string path)
    {
        lock (_liveWriteGate)
        {
            var store = LiveStoreFor(path); var record = store?.FindRecord(path);
            if (record is null || record.Item.IsDirectory) return;
            store!.Commit([new(path, record with { Item = record.Item with { OpenCount = checked(record.Item.OpenCount + 1) } })]);
            PublishLiveState();
        }
    }
    private LiveCatalogStore? LiveStoreFor(string path) => _liveStores.FirstOrDefault(store => IsSameOrDescendantPath(path, store.RootPath));
}
