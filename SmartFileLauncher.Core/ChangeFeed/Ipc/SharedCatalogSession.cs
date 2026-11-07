using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Search.Paged;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

[SupportedOSPlatform("windows")]
internal sealed class SharedCatalogSession : IChangeFeedIndexTarget
{
    private readonly ChangeFeedCaller _caller;
    private readonly ChangeFeedStoreLayout _layout;
    private readonly IChangeFeedStore _store;
    private readonly string[] _roots;
    private readonly IndexScanMethod _method;
    private readonly IndexBuildOptions _buildOptions;
    private readonly bool _rebuild;
    private readonly string _cacheKey;
    private SharedCatalogSession? _previous;
    private readonly Func<string, CancellationToken, UsnDrainRunner.BootstrapLease>? _bootstrap;
    private readonly ChangeFeedIndexBridge _bridge;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly ConcurrentQueue<(string Path, bool Open, long Operation, bool SynchronizeOnly)> _commands = new();
    private readonly object _commandGate = new();
    private readonly object _statusGate = new();
    private readonly ConcurrentDictionary<string, byte> _pendingRepairs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan MinimumWatcherCycle = TimeSpan.FromSeconds(2);
    private long _acceptedOperation, _completedOperation, _interactiveOperation;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SharedCatalogProfile _profile = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly string _id = Guid.NewGuid().ToString("N");
    private List<SharedCatalogBuilder.Part> _parts = [];
    private SharedCatalogStatus _status;
    private string _directory = "";
    private string _signature = "";
    private long _revision;
    private long _startupProcessedEvents, _startupThroughSequence;
    private bool _allNames;
    private SharedCatalogBuildMetrics? _build;
    private long _lastSeen = Stopwatch.GetTimestamp();
    private Timer? _leaseTimer;
    private Task _namePreparation = Task.CompletedTask;
    private int _nameOrderPublished;

    internal SharedCatalogSession(ChangeFeedAdmissionService admission, ChangeFeedCaller caller,
        ChangeFeedStoreLayout layout, IChangeFeedStore store, string[] roots, IndexScanMethod method,
        bool rebuild, SharedCatalogSession? previous, string cacheKey, IndexBuildOptions? buildOptions = null)
    {
        _caller = caller; _layout = layout; _store = store; _roots = roots; _method = method;
        _rebuild = rebuild; _previous = previous;
        _cacheKey = cacheKey;
        _buildOptions = IndexBuildOptions.Normalize(buildOptions);
        _bootstrap = admission.CatalogBootstrap;
        _allNames = method == IndexScanMethod.Mft;
        _bridge = new(new LocalChannel(admission, caller, () => _allNames, _profile), this);
        _status = new(_id, "Servis kataloğu hazırlanıyor", 0, 0, false, 0, [], AppliedBuildOptions: _buildOptions, TokenIndexReady: false);
    }

    internal SharedCatalogStatus Status => Volatile.Read(ref _status);
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal void Start()
    {
        _leaseTimer = new Timer(_ =>
        {
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastSeen)) > TimeSpan.FromMinutes(2)) Stop();
        }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        using (ExecutionContext.SuppressFlow()) Completion = Task.Run(RunAsync);
    }
    internal void Touch() => Interlocked.Exchange(ref _lastSeen, Stopwatch.GetTimestamp());
    internal void Stop()
    {
        _stop.Cancel();
        lock (_statusGate)
            Volatile.Write(ref _status, Status with { Ready = false, Closed = true, Phase = "Katalog oturumu kapatıldı" });
        Wake();
    }
    private void UpdateStatus(Func<SharedCatalogStatus, SharedCatalogStatus> update)
    {
        lock (_statusGate)
        {
            if (_stop.IsCancellationRequested) return;
            Volatile.Write(ref _status, update(Status));
        }
    }
    internal bool Enqueue(string path, bool opened) => EnqueueRequest(path, opened, interactive: false) > 0;
    internal long EnqueueRequest(string path, bool opened, bool synchronizeOnly = false, bool interactive = true)
    {
        if (_stop.IsCancellationRequested || Status.Failure is not null || _commands.Count >= 10_000 ||
            !_roots.Any(root => UsnRootScope.TryCanonicalize(root, path, out _)) || IsPrivate(path)) return 0;
        lock (_commandGate)
        {
            var operation = ++_acceptedOperation;
            _commands.Enqueue((path, opened, operation, synchronizeOnly));
            if (interactive) Interlocked.Exchange(ref _interactiveOperation, operation);
            Wake();
            return operation;
        }
    }
    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        try
        {
            if (_previous is not null) await _previous.Completion.ConfigureAwait(false);
            _previous = null;
            ct.ThrowIfCancellationRequested();
            _layout.EnsureCreated();
            _directory = Path.Combine(_layout.OwnerDirectory, "catalog");
            var profile = ChangeFeedStorePermissionProfile.ForSharedCatalog(
                _layout.TrustedPrincipalSid ?? throw new InvalidOperationException("Ortak katalog güvenilir servis deposu gerektirir."), _layout.OwnerSid);
            if (!Directory.Exists(_directory)) ChangeFeedStoreSecurity.Create(_directory, profile);
            ChangeFeedStoreSecurity.Verify(_directory, profile);
            _directory = Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_cacheKey))));
            Directory.CreateDirectory(_directory);
            _signature = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { Version = 2, Roots = _roots.Select(ChangeFeedDeliveryContract.EncodeScope).Order().ToArray() })));
            var cached = !_rebuild && TryLoad();
            using var bootstrap = !cached ? _bootstrap?.Invoke(_layout.OwnerSid, ct) : null;
            var preparation = await _bridge.PrepareContinuousAsync(_roots, !cached && bootstrap is null, ct).ConfigureAwait(false);
            if (!preparation.Available) throw new IOException(preparation.Diagnostic);
            if (!cached && _method == IndexScanMethod.Mft && preparation.WatcherRoots.Length != 0)
                throw new NotSupportedException("MFT yöntemi yalnız yerel NTFS konumlarında kullanılabilir.");
            foreach (var root in _roots) Watch(root, preparation.WatcherRoots.Contains(root, StringComparer.OrdinalIgnoreCase));
            if (!cached)
            {
                var maps = new Dictionary<string, IReadOnlyList<UsnDirectoryEntry>>(StringComparer.OrdinalIgnoreCase);
                var boundaries = new Dictionary<string, UsnJournalDescriptor>(StringComparer.OrdinalIgnoreCase);
                if (bootstrap is not null && preparation.ServiceRoots.Length != 0)
                {
                    UpdateProgress("İlk taramanın değişiklik sınırı hazırlanıyor", 0);
                    bootstrap.DiscardPending(ct);
                    foreach (var volume in preparation.ServiceRoots.Select(UsnVolumeJournalReader.ResolveVolumeRoot).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        using var journal = new UsnVolumeJournalReader(volume);
                        boundaries.Add(volume, journal.QueryJournal());
                    }
                }
                var builder = new SharedCatalogBuilder(_caller, _directory, _layout.StoreRoot, UpdateProgress,
                    (root, entries) => maps[root] = entries, _buildOptions);
                _parts = _method == IndexScanMethod.Mft
                    ? builder.ReadMft(Subscriptions(_roots), ct)
                    : builder.ReadDirectories(_roots, ct);
                _build = builder.Metrics;
                Save();
                if (bootstrap is not null && preparation.ServiceRoots.Length != 0)
                {
                    var states = Subscriptions(preparation.ServiceRoots).Select(root =>
                    {
                        var boundary = boundaries[UsnVolumeJournalReader.ResolveVolumeRoot(root.RootPath)];
                        var serial = Convert.ToUInt64(root.Identity.VolumeId[(root.Identity.VolumeId.LastIndexOf("0x", StringComparison.Ordinal) + 2)..], 16);
                        var reference = Convert.ToUInt64(root.Identity.NodeId[2..], 16);
                        return new UsnChangeFeedState(root.RootPath, new(serial, UsnFileReference.FromNtfs(reference)),
                            boundary.JournalId, boundary.NextUsn, maps[root.RootPath], boundary.NextUsn);
                    }).ToArray();
                    bootstrap.Install(states, ct);
                }
            }
            bootstrap?.Dispose();
            var vanishedLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in _parts.Where(part => part.ExcludeRoot))
            {
                using var catalog = part.Store.Acquire();
                if (!TryWatchLink(catalog.Data.RootPath)) vanishedLinks.Add(catalog.Data.RootPath);
            }
            if (vanishedLinks.Count != 0)
            {
                RefreshLinks(vanishedLinks, ct);
                Save();
            }
            UpdateProgress(cached ? "Katalog önbellekten açıldı; son değişiklikler alınıyor" : "Son dosya olayları uygulanıyor", Count());
            var initialCommands = Interlocked.Read(ref _acceptedOperation);
            if (preparation.ServiceRoots.Length != 0)
            {
                long? throughSequence = null;
                while (true)
                {
                    if (throughSequence is null)
                    {
                        UpdateProgress("USN günlüğünün başlangıç sınırı bekleniyor", Count());
                        if (await _bridge.DrainContinuousAsync(ct).ConfigureAwait(false))
                        {
                            using var scope = _store.EnterOwnerScope(ct);
                            throughSequence = _store.ReadPending().AvailableThroughSequence;
                        }
                    }
                    if (throughSequence is not null)
                    {
                        UpdateProgress("Bekleyen USN dosya olayları uygulanıyor", Count());
                        var caught = await ConsumePendingAsync(ct, throughSequence).ConfigureAwait(false);
                        if (caught.Available && caught.CaughtUp) break;
                        if (!caught.Available && !caught.Retryable) throw new IOException(caught.Diagnostic ?? "İlk USN teslimi tamamlanmadı.");
                        if (caught.Retryable) throughSequence = null;
                    }
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }
            UpdateProgress("USN başlangıç komutları uygulanıyor", Count());
            using (_profile.Measure("initial-commands")) ProcessCommands(ct, initialCommands);
            UpdateProgress("USN servis kataloğu yayımlanıyor", Count());
            using (_profile.Measure("service-publish")) Publish();
            _profile.Save(_directory, "service-ready", _startupThroughSequence, _startupProcessedEvents);
            PrepareNameOrder(ct);
            var lastCycle = 0L;
            while (!ct.IsCancellationRequested)
            {
                await _wake.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                await Task.Delay(30, ct).ConfigureAwait(false);
                for (var idle = MinimumWatcherCycle - Stopwatch.GetElapsedTime(lastCycle);
                     idle > TimeSpan.Zero && Interlocked.Read(ref _interactiveOperation) <= _completedOperation;
                     idle = MinimumWatcherCycle - Stopwatch.GetElapsedTime(lastCycle))
                    await _wake.WaitAsync(idle, ct).ConfigureAwait(false);
                lastCycle = Stopwatch.GetTimestamp();
                var before = _revision;
                var commandsThrough = Interlocked.Read(ref _acceptedOperation);
                if (preparation.ServiceRoots.Length != 0)
                {
                    if (!await _bridge.DrainContinuousAsync(ct).ConfigureAwait(false))
                    {
                        UpdateStatus(status => status with { Phase = "USN değişiklikleri bekleniyor" });
                        continue;
                    }
                    var consumed = await ConsumePendingAsync(ct).ConfigureAwait(false);
                    if (!consumed.Available && !consumed.Retryable) throw new IOException(consumed.Diagnostic ?? "USN teslimi alınamadı.");
                    if (consumed.Retryable) { Wake(); continue; }
                    if (!consumed.CaughtUp) Wake();
                }
                ProcessCommands(ct, commandsThrough);
                if (Interlocked.Exchange(ref _nameOrderPublished, 0) != 0 || _revision != before) Publish();
                else UpdateStatus(status => status with { CompletedOperation = _completedOperation });
                PrepareNameOrder(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception failure)
        {
            UpdateStatus(status => status with { Ready = false, Failure = failure.Message, Phase = "Katalog işlemi durdu" });
        }
        finally
        {
            _stop.Cancel();
            await _namePreparation.ConfigureAwait(false);
            _leaseTimer?.Dispose();
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            foreach (var part in _parts) part.Store.Dispose();
            _parts.Clear();
            _watchers.Clear();
            _caller.Dispose();
        }
    }

    private async Task<ContinuousConsumption> ConsumePendingAsync(CancellationToken ct, long? throughSequence = null)
    {
        using var waiting = _profile.Measure("consume-total");
        UsnDrainRunner.BootstrapLease? acquired;
        using (_profile.Measure("delivery-gate")) acquired = _bootstrap?.Invoke(_layout.OwnerSid, ct);
        using var delivery = acquired;
        return await _bridge.ConsumeContinuousAsync(ct, throughSequence: throughSequence).ConfigureAwait(false);
    }

    private void PrepareNameOrder(CancellationToken ct)
    {
        if (!_namePreparation.IsCompleted) return;
        var pending = _parts.Select(part => part.Store).Where(store =>
        {
            using var catalog = store.Acquire();
            return catalog.Data.SortedCount == 0 || TokenIndexing.Enabled && !catalog.Data.TermsReady;
        }).ToArray();
        if (pending.Length == 0) return;
        _namePreparation = Task.Run(async () =>
        {
            foreach (var store in pending)
            {
                try
                {
                    if (await store.PrepareNameOrderAsync(ct, _buildOptions).ConfigureAwait(false))
                    {
                        Interlocked.Exchange(ref _nameOrderPublished, 1);
                        Wake();
                    }
                    while (TokenIndexing.Enabled && await store.PrepareTokenBatchAsync(ct).ConfigureAwait(false))
                    {
                        Interlocked.Exchange(ref _nameOrderPublished, 1);
                        Wake();
                        await Task.Yield();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (ObjectDisposedException) { }
                catch (Exception failure) { Trace.TraceWarning("Katalog arka plan hazırlığı tamamlanamadı; yeniden denenecek: {0}", failure.Message); }
            }
        });
    }

    private SharedCatalogBuilder Builder() => new(_caller, _directory, _layout.StoreRoot, UpdateProgress, buildOptions: _buildOptions);
    private ChangeFeedSubscribedRoot[] Subscriptions(IEnumerable<string> roots)
    {
        using var held = _store.EnterOwnerScope(_stop.Token);
        var subscribed = _store.ReadSubscription()?.Roots ?? [];
        return roots.Select(root => subscribed.Single(item => Same(item.RootPath, root))).ToArray();
    }

    private void Watch(string root, bool fallback)
    {
        if (_watchers.ContainsKey(root)) return;
        var watcher = _caller.Run(_ => new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Security,
            InternalBufferSize = 64 * 1024
        });
        void Changed(string path)
        {
            if (IsPrivate(path)) return;
            if (fallback && !Enqueue(path, false)) RequireRepair(root);
            else Wake();
        }
        watcher.Created += (_, e) => Changed(e.FullPath);
        watcher.Changed += (_, e) => Changed(e.FullPath);
        watcher.Deleted += (_, e) => Changed(e.FullPath);
        watcher.Renamed += (_, e) => { Changed(e.OldFullPath); Changed(e.FullPath); };
        watcher.Error += (_, _) => { if (fallback) RequireRepair(root); else Wake(); };
        try { _caller.Run(_ => { watcher.EnableRaisingEvents = true; return true; }); }
        catch { watcher.Dispose(); throw; }
        _watchers.Add(root, watcher);
    }
    private bool TryWatchLink(string root)
    {
        try { Watch(root, true); return true; }
        catch (Exception failure) when (failure is ArgumentException || SharedCatalogBuilder.MissingOrDenied(failure)) { return false; }
    }
    private void RequireRepair(string root)
    {
        _pendingRepairs[root] = 0;
        Wake();
    }

    public Task CommitContinuousAsync(IReadOnlyList<ChangeFeedRootPageDto> roots, string deliveryId,
        long completedThroughSequence, CancellationToken ct)
    {
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var group in roots.GroupBy(root => root.RootPath, StringComparer.OrdinalIgnoreCase))
        {
            if (!_roots.Any(root => Same(root, group.Key))) throw new InvalidDataException("USN teslim kapsamı farklı.");
            if (group.Any(root => root.ProducerFault != ChangeFeedFaultReason.None)) throw new IOException("USN üreticisi geçici hata bildirdi.");
            var repair = group.Any(root => root.ProducerGap != ChangeFeedGapReason.None || root.PayloadTooLarge ||
                root.AuthorizationGap && root.AuthorizationScopesUtf16 is not { Count: > 0 });
            var scopes = group.SelectMany(root => root.AuthorizationScopesUtf16 ?? []).Select(ChangeFeedDeliveryContract.DecodeScope).ToArray();
            var changes = group.SelectMany(root => root.Events).ToArray();
            if (!repair && scopes.Length == 0 && changes.Length == 0) continue;
            if (!Status.Ready) UpdateProgress(repair ? "USN kesintisi sonrası katalog onarılıyor" :
                $"USN teslimi uygulanıyor: {changes.Length} olay, {scopes.Length} onarım kapsamı", Count());
            changed = true;
            foreach (var change in changes.Where(change => change.IsDirectory))
            {
                links.Add(change.Path);
                if (change.OldPath is { } oldPath) links.Add(oldPath);
            }
            foreach (var part in _parts.Where(part => part.ExcludeRoot))
            {
                using var linked = part.Store.Acquire();
                var linkedRoot = linked.Data.RootPath;
                if (repair || scopes.Any(scope => UsnRootScope.TryCanonicalize(scope, linkedRoot, out _))) links.Add(linkedRoot);
                foreach (var change in changes.Where(change => change.IsDirectory))
                {
                    if (UsnRootScope.TryCanonicalize(change.Path, linkedRoot, out _)) links.Add(linkedRoot);
                    if (change.OldPath is { } old && UsnRootScope.TryCanonicalize(old, linkedRoot, out _))
                    {
                        links.Add(linkedRoot);
                        links.Add(Path.GetFullPath(Path.Combine(change.Path, Path.GetRelativePath(old, linkedRoot))));
                    }
                }
            }
            foreach (var part in _parts)
            {
                var progressClock = Stopwatch.StartNew();
                var reconciler = new SharedCatalogReconciler(_caller, _layout.StoreRoot, path => links.Add(path), part.NativeIdentity, part.AllNames, IsLinkedDirectory,
                    (index, total, change) =>
                    {
                        if (Status.Ready || index != 1 && index != total && progressClock.ElapsedMilliseconds < 1000) return;
                        progressClock.Restart();
                        UpdateProgress($"USN: {_startupProcessedEvents + index:N0} olay • teslim {completedThroughSequence} • {Path.GetFileName(change.Path)}", Count());
                    }, _profile);
                using var current = part.Store.Acquire();
                if (!UsnRootScope.TryCanonicalize(group.Key, current.Data.RootPath, out _) || part.ExcludeRoot) continue;
                if (current.Data.DeliveryId == deliveryId)
                {
                    var repairedPaths = repair ? new[] { current.Data.RootPath } : scopes.Concat(changes
                        .Where(change => change.IsDirectory && (change.Kind != ChangeFeedEventKind.Modified || change.RefreshSubtree))
                        .Select(change => change.Path));
                    foreach (var path in repairedPaths) CollectLinks(current, path, links, ct);
                    continue;
                }
                using var commitTiming = _profile.Measure("commit-total");
                part.Store.Commit(catalog =>
                {
                    using var applyTiming = _profile.Measure("reconcile-total");
                    if (repair) reconciler.Repair(catalog, catalog.Data.RootPath, ct);
                    else
                    {
                        reconciler.Apply(catalog, changes, ct);
                        foreach (var scope in scopes)
                        {
                            if (!UsnRootScope.TryCanonicalize(group.Key, scope, out _)) throw new InvalidDataException("Onarım kapsamı kök dışında.");
                            reconciler.Repair(catalog, scope, ct);
                        }
                    }
                    if (!Status.Ready) UpdateProgress("USN kataloğu diske kaydediliyor", Count());
                }, completedThroughSequence, deliveryId);
            }
        }
        if (!changed) return Task.CompletedTask;
        if (!Status.Ready) UpdateProgress("USN klasör bağlantıları güncelleniyor", Count());
        using (_profile.Measure("links")) RefreshLinks(links, ct);
        using (_profile.Measure("manifest")) Save();
        if (completedThroughSequence > _startupThroughSequence)
        {
            _startupProcessedEvents += roots.Sum(root => root.Events.Count);
            _startupThroughSequence = completedThroughSequence;
        }
        if (!Status.Ready) UpdateProgress($"USN: {_startupProcessedEvents:N0} olay kaydedildi; teslim {completedThroughSequence} onaylanıyor", Count());
        _profile.Save(_directory, "delivery-committed", completedThroughSequence, _startupProcessedEvents);
        _revision++;
        return Task.CompletedTask;
    }
    private static void CollectLinks(Catalog catalog, string path, ISet<string> links, CancellationToken ct)
    {
        var id = new Reader(catalog).FindPath(path);
        if (id == 0 || !catalog.IsDirectory(id)) return;
        var pending = new Stack<uint>();
        pending.Push(id);
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            if (((FileAttributes)catalog.Meta(current).Attributes & FileAttributes.ReparsePoint) != 0 &&
                UsnDirectoryWalk.IsNameSurrogate(catalog.FullPath(current)))
                links.Add(catalog.FullPath(current));
            else foreach (var child in catalog.Children(current))
                if (catalog.IsDirectory(child)) pending.Push(child);
        }
    }

    private void ProcessCommands(CancellationToken ct, long through)
    {
        var commands = new List<(string Path, bool Open, long Operation, bool SynchronizeOnly)>();
        while (_commands.TryPeek(out var next) && next.Operation <= through && _commands.TryDequeue(out var command)) commands.Add(command);
        var completed = commands.Count == 0 ? _completedOperation : commands[^1].Operation;
        foreach (var root in _pendingRepairs.Keys)
            if (_pendingRepairs.TryRemove(root, out _)) commands.Add((root, false, 0, false));
        if (commands.Count == 0) return;
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linkRoots = _parts.Where(part => part.ExcludeRoot).Select(part =>
        {
            using var linked = part.Store.Acquire();
            return linked.Data.RootPath;
        }).ToArray();
        foreach (var part in _parts)
        {
            var reconciler = new SharedCatalogReconciler(_caller, _layout.StoreRoot, path => links.Add(path), part.NativeIdentity, part.AllNames, IsLinkedDirectory);
            using var current = part.Store.Acquire();
            if (part.ExcludeRoot && commands.Any(command => !command.Open && !command.SynchronizeOnly &&
                UsnRootScope.TryCanonicalize(command.Path, current.Data.RootPath, out _))) links.Add(current.Data.RootPath);
            var selected = commands.Where(command => !command.SynchronizeOnly &&
                (!part.ExcludeRoot || !Same(command.Path, current.Data.RootPath)) &&
                UsnRootScope.TryCanonicalize(current.Data.RootPath, command.Path, out _) &&
                !InsideDeeperLink(linkRoots, current.Data.RootPath, command.Path)).ToArray();
            if (selected.Length == 0) continue;
            part.Store.Commit(catalog =>
            {
                foreach (var (path, opened, _, _) in selected)
                {
                    if (!opened) { reconciler.Repair(catalog, path, ct); continue; }
                    var id = new Reader(catalog).FindPath(path);
                    if (id == 0 || id == Catalog.RootId) continue;
                    var row = catalog.Row(id); var metadata = catalog.Meta(id);
                    var entry = new Indexing.Ntfs.NtfsMftEntry(row.Frn, catalog.Row((uint)row.Parent).Frn, catalog.Name(id), catalog.IsDirectory(id),
                        (FileAttributes)metadata.Attributes, metadata.Size, metadata.Created, metadata.Modified);
                    catalog.Apply(new(row.Frn, entry.ParentFileReference, entry.Name, entry, checked(metadata.OpenCount + 1)));
                }
            }, current.Data.Cursor);
        }
        RefreshLinks(links, ct);
        _completedOperation = completed;
        if (commands.Any(command => !command.SynchronizeOnly)) { Save(); _revision++; }
    }

    private static bool InsideDeeperLink(string[] linkRoots, string root, string path)
    {
        foreach (var linkRoot in linkRoots)
        {
            if (Same(linkRoot, root) || !UsnRootScope.TryCanonicalize(root, linkRoot, out _)) continue;
            if (!Same(linkRoot, path) && UsnRootScope.TryCanonicalize(linkRoot, path, out _)) return true;
        }
        return false;
    }

    private void RefreshLinks(IEnumerable<string> paths, CancellationToken ct)
    {
        var pending = new Queue<string>(paths.OrderBy(path => path.Length).Distinct(StringComparer.OrdinalIgnoreCase));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var path))
        {
            if (!seen.Add(path)) continue;
            ct.ThrowIfCancellationRequested();
            var containing = _roots.FirstOrDefault(root => UsnRootScope.TryCanonicalize(root, path, out _));
            if (containing is null || Same(containing, path)) continue;
            var visible = _caller.Run(_ =>
            {
                try { return ChangeFeedPathAuthorizer.ForCurrentCaller(containing).CanReturnExistingPath(path) &&
                    (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == (FileAttributes.Directory | FileAttributes.ReparsePoint) &&
                    Indexing.Ntfs.NtfsDirectoryReader.IsNameSurrogate(path); }
                catch (Exception failure) when (SharedCatalogBuilder.MissingOrDenied(failure)) { return false; }
            });
            SharedCatalogBuilder.Part? existing = null;
            foreach (var part in _parts.Where(part => part.ExcludeRoot))
            {
                using var catalog = part.Store.Acquire();
                if (Same(catalog.Data.RootPath, path)) { existing = part; break; }
            }
            var reuse = false;
            if (visible && existing is not null)
            {
                try
                {
                    using var opened = _caller.Run(_ => new Indexing.Ntfs.NtfsDirectoryReader(path));
                    using var current = existing.Store.Acquire();
                    reuse = current.Data.RootFrn == opened.Root.FileReference && current.Data.VolumeIdentity == opened.VolumeIdentity;
                    if (reuse) existing.Store.Commit(catalog => new SharedCatalogReconciler(_caller, _layout.StoreRoot,
                        discovered => pending.Enqueue(discovered), existing.NativeIdentity).Repair(catalog, path, ct), current.Data.Cursor);
                }
                catch (Exception failure) when (SharedCatalogBuilder.MissingOrDenied(failure)) { visible = false; }
            }
            if (reuse) continue;
            var replacements = visible ? Builder().ReadLink(path, ct) : [];
            foreach (var part in _parts.Where(part => part.ExcludeRoot).ToArray())
            {
                using var current = part.Store.Acquire();
                if (!UsnRootScope.TryCanonicalize(path, current.Data.RootPath, out _)) continue;
                if (_watchers.Remove(current.Data.RootPath, out var watcher)) watcher.Dispose();
                _parts.Remove(part);
                part.Store.Dispose();
            }
            _parts.AddRange(replacements);
            foreach (var part in replacements)
            {
                string linkRoot;
                using (var catalog = part.Store.Acquire()) linkRoot = catalog.Data.RootPath;
                if (TryWatchLink(linkRoot)) continue;
                _parts.Remove(part);
                part.Store.Dispose();
            }
        }
    }

    private bool IsLinkedDirectory(string path) => _parts.Where(part => part.ExcludeRoot).Any(part =>
    {
        using var catalog = part.Store.Acquire();
        return Same(catalog.Data.RootPath, path);
    });

    private bool TryLoad()
    {
        var path = Path.Combine(_directory, "current.json");
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Katalog bildirimi büyük.");
            var manifest = JsonSerializer.Deserialize<Manifest>(stream);
            if (manifest is null || manifest.Signature != _signature) return false;
            foreach (var part in manifest.Parts)
            {
                if (Path.GetFileName(part.Folder) != part.Folder || !part.Folder.StartsWith("part-", StringComparison.Ordinal)) throw new InvalidDataException("Katalog dizini geçersiz.");
                var store = CatalogStore.Open(Path.Combine(_directory, part.Folder), true);
                using var catalog = store.Acquire();
                if (!_roots.Any(root => UsnRootScope.TryCanonicalize(root, catalog.Data.RootPath, out _))) { store.Dispose(); throw new InvalidDataException("Katalog kapsamı geçersiz."); }
                _parts.Add(new(store, part.ExcludeRoot, part.NativeIdentity, part.AllNames));
            }
            _allNames = _parts.Any(part => part.AllNames);
            return _parts.Count > 0;
        }
        catch (Exception failure) when (failure is IOException or JsonException)
        {
            foreach (var part in _parts) part.Store.Dispose();
            _parts.Clear();
            return false;
        }
    }

    private void Save()
    {
        var manifest = new Manifest(_signature, _parts.Select(part =>
        {
            using var catalog = part.Store.Acquire();
            return new SavedPart(Path.GetFileName(catalog.Data.Directory), part.ExcludeRoot, part.NativeIdentity, part.AllNames);
        }).ToArray(), _build);
        var temporary = Path.Combine(_directory, "current.tmp");
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, manifest); stream.Flush(true); }
        File.Move(temporary, Path.Combine(_directory, "current.json"), true);
    }

    private void Publish()
    {
        if (!_allNames && !_caller.Run(_ => _roots.All(SharedCatalogService.CanList)))
            throw new UnauthorizedAccessException("İndeks köklerinden birine erişim değişti.");
        var files = 0; var directories = 0; var tokens = 0; var tokenReady = TokenIndexing.Enabled;
        var parts = _parts.Select(part =>
        {
            using var catalog = part.Store.Acquire();
            directories += catalog.DirectoryCount - (part.ExcludeRoot ? 1 : 0);
            files += catalog.Data.EntryCount - catalog.DirectoryCount;
            if (TokenIndexing.Enabled) tokens += new Reader(catalog).TokenCount;
            tokenReady &= catalog.Data.TermsReady;
            return new SharedCatalogPart(ChangeFeedDeliveryContract.EncodeScope(catalog.Data.RootPath), catalog.Data.Directory,
                catalog.Data.Frame, catalog.Data.Generation, catalog.Data.EntryCount, part.ExcludeRoot);
        }).ToArray();
        UpdateStatus(_ => new(_id, tokenReady ? "Hazır" : "Arama hazır; AI indeksi arka planda hazırlanıyor", files + directories, _elapsed.ElapsedMilliseconds,
            true, ++_revision, parts, Files: files, Directories: directories, Tokens: tokens,
            CompletedOperation: _completedOperation, Build: _build, AppliedBuildOptions: _buildOptions, TokenIndexReady: tokenReady));
    }
    private int Count() => _parts.Sum(part => { using var catalog = part.Store.Acquire(); return catalog.Data.EntryCount; });
    private void UpdateProgress(string phase, int count) => UpdateStatus(status =>
        status with { Phase = phase, Items = count, ElapsedMilliseconds = _elapsed.ElapsedMilliseconds });
    private bool IsPrivate(string path) => UsnRootScope.TryCanonicalize(_layout.StoreRoot, path, out _);
    private static bool Same(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);
    public bool Apply(IReadOnlyList<FileChangeEvent> changes) => false;
    public Task<bool> ResynchronizeAsync(string rootPath, bool withinLifecycle, CancellationToken ct) => Task.FromResult(Enqueue(rootPath, false));
    private sealed record Manifest(string Signature, SavedPart[] Parts, SharedCatalogBuildMetrics? Build = null);
    private sealed record SavedPart(string Folder, bool ExcludeRoot, bool NativeIdentity, bool AllNames = false);
    private sealed class LocalChannel(ChangeFeedAdmissionService admission, ChangeFeedCaller caller, Func<bool> allNames, SharedCatalogProfile profile) : IChangeFeedRequestChannel
    {
        public Task<ChangeFeedResponse> SendAsync(ChangeFeedRequest request, CancellationToken ct)
        {
            using var timing = profile.Measure("ipc-" + request.Kind);
            return Task.FromResult(admission.HandleCatalog(caller, request, allNames(), ct));
        }
    }
}
