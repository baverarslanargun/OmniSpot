using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Indexing.Folders;
using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Application.Settings;

namespace SmartFileLauncher.Core.Application.Indexing;

public sealed class VolumeIndexLifecycleService : IIndexLifecycleService, IIndexHealthSource
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SaveQuietPeriod = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FolderSaveInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RescanBackoff = TimeSpan.FromMinutes(10);
    private readonly IChangeFeedRequestChannel _channel;
    private readonly IIndexedLocationProvider _locations;
    private readonly string _directory;
    private readonly IndexBuildOptions _buildOptions;
    private readonly bool _removeUnusedSnapshots;
    private readonly IIndexVolumePolicy? _policy;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private List<Volume> _volumes = [];
    private List<PendingVolume> _pending = [];
    private DateTime _nextRetry;
    private Task? _loop;
    private DateTime? _lastChange;
    private long _publishes;
    private bool _disposed;
    private IReadOnlyList<IndexIssue> _reportedIssues = [];
    private Dictionary<string, Task<(FileIndex? Index, IndexCheckpoint Checkpoint)>>? _preloads;

    private sealed class Volume(string root, IReadOnlyList<string>? scope, string snapshotPath)
    {
        internal readonly string Root = root;
        internal readonly IReadOnlyList<string>? Scope = scope;
        internal readonly string SnapshotPath = snapshotPath;
        internal FileIndex Index = null!;
        internal IndexCheckpoint Checkpoint;
        internal bool Dirty;
        internal DateTime LastChange = DateTime.UtcNow;
        internal DateTime LastSaved = DateTime.UtcNow;
        internal long SavedUsn;
        internal long FirstUsn;
        internal DateTime LastRescan = DateTime.MinValue;
        internal IndexIssue? SaveIssue;
        internal FolderState? Folder;
    }

    private sealed class FolderState(FolderVolumeWatcher watcher, IReadOnlyList<string> roots)
    {
        internal readonly FolderVolumeWatcher Watcher = watcher;
        internal readonly IReadOnlyList<string> Roots = roots;
        internal FolderReferences References = new(FolderVolumeScanner.FirstReference);
        internal bool RescanPending;
        internal Task<(FileIndex Index, FolderReferences References)>? Rescan;
    }

    private sealed record PendingVolume(string Root, IReadOnlyList<string>? Scope, IndexIssue Issue);

    public VolumeIndexLifecycleService(IChangeFeedRequestChannel channel, IIndexedLocationProvider locations, string directory,
        IndexBuildOptions? buildOptions = null, bool removeUnusedSnapshots = false, IIndexVolumePolicy? policy = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _buildOptions = IndexBuildOptions.Normalize(buildOptions);
        _removeUnusedSnapshots = removeUnusedSnapshots;
        _policy = policy;
    }

    public event Action<IndexProgress>? ProgressChanged;
    public event Action<FileChangeEvent>? FileChanged;
    public event Action<string>? Error { add { } remove { } }
    public event Action<string>? Notice;
    public event Action<IndexHealth>? HealthChanged;
    public event Action<int, int, int>? ReconciliationProgressChanged { add { } remove { } }
    public event Action<bool>? ReconciliationStateChanged { add { } remove { } }

    internal TimeSpan RetryInterval { get; init; } = TimeSpan.FromMinutes(2);
    public bool IsInitialized { get; private set; }
    public bool IsTokenIndexReady => IsInitialized;
    public string DatabasePath => Path.Combine(_directory, "control.bin");
    public IndexReconciliationStatus ReconciliationStatus { get; private set; } = new(false, 100, 0, 0);

    public void Preload()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_preloads is not null || _loop is not null) return;
            var preloads = new Dictionary<string, Task<(FileIndex? Index, IndexCheckpoint Checkpoint)>>(StringComparer.OrdinalIgnoreCase);
            _preloads = preloads;
            try
            {
                if (!File.Exists(DatabasePath) || File.Exists(Path.Combine(_directory, "rebuild.request"))) return;
                foreach (var line in File.ReadAllLines(DatabasePath))
                {
                    var path = line.Trim();
                    if (path.Length == 0 || preloads.ContainsKey(path) ||
                        !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), _directory, StringComparison.OrdinalIgnoreCase)) continue;
                    preloads[path] = Task.Run(() =>
                    {
                        var index = FileIndex.Load(path, out var checkpoint);
                        return (index, checkpoint);
                    });
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }
    }

    private Task<(FileIndex? Index, IndexCheckpoint Checkpoint)>? TakePreload(string path)
    {
        lock (_gate)
        {
            if (_preloads is null || !_preloads.Remove(path, out var preload)) return null;
            return preload;
        }
    }

    private void ReleasePreloads()
    {
        Dictionary<string, Task<(FileIndex? Index, IndexCheckpoint Checkpoint)>>? preloads;
        lock (_gate)
        {
            preloads = _preloads;
            _preloads = [];
        }
        if (preloads is null) return;
        foreach (var preload in preloads.Values)
            preload.ContinueWith(static finished =>
            {
                if (finished.IsCompletedSuccessfully) finished.Result.Index?.Dispose();
                else _ = finished.Exception;
            }, TaskScheduler.Default);
    }

    public async Task<IndexStartupResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loop is not null) throw new InvalidOperationException("İndeks zaten başlatıldı.");
        try { return await StartAsync(cancellationToken).ConfigureAwait(false); }
        finally { ReleasePreloads(); }
    }

    private async Task<IndexStartupResult> StartAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var locations = _locations.Resolve();
        if (locations.RootPaths.Count == 0)
        {
            HealthChanged?.Invoke(new([new(IndexIssueReason.InvalidLocation, null, "İlk açılış", "İndekslenecek konum yok.")],
                SearchAvailable: false, InitialStartup: true));
            return new IndexStartupResult(locations.DesktopPath, locations.RootPaths, GetStats());
        }
        Directory.CreateDirectory(_directory);
        var marker = Path.Combine(_directory, "rebuild.request");
        var rebuild = File.Exists(marker);
        var clock = Stopwatch.StartNew();
        var volumes = new List<Volume>();
        var failures = new List<PendingVolume>();
        try
        {
            foreach (var (root, scope) in Group(locations.RootPaths, failures))
            {
                try
                {
                    volumes.Add(await Task.Run(() => OpenVolumeAsync(root, scope, rebuild, clock, linked.Token), linked.Token)
                        .ConfigureAwait(false));
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    failures.Add(new(root, scope, Skipped(failure, root)));
                }
            }
            if (volumes.Count == 0) throw new IndexIssueException(failures[0].Issue with { LocationSkipped = false });
            var listed = volumes.Select(volume => volume.SnapshotPath).ToList();
            if (failures.Count > 0) listed = ReadControl().Concat(listed).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            await File.WriteAllTextAsync(DatabasePath, string.Join(Environment.NewLine, listed), linked.Token).ConfigureAwait(false);
            if (rebuild && failures.Count == 0) File.Delete(marker);
            if (_removeUnusedSnapshots && failures.Count == 0)
            {
                linked.Token.ThrowIfCancellationRequested();
                RemoveUnusedSnapshots(volumes);
            }
        }
        catch
        {
            foreach (var volume in volumes) Close(volume);
            throw;
        }

        lock (_gate)
        {
            _volumes = volumes;
            _pending = failures;
            _nextRetry = DateTime.UtcNow + RetryInterval;
        }
        IndexMemory.Release();
        IsInitialized = true;
        ReportHealth(CurrentIssues([], failures, volumes));
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

    private static List<(string Root, IReadOnlyList<string>? Scope)> Group(IReadOnlyList<string> paths, List<PendingVolume> failures)
    {
        var groups = new List<(string Root, List<string> Roots)>();
        foreach (var path in paths)
        {
            string full, volume;
            try
            {
                full = Path.GetFullPath(path);
                volume = SettingsIndexedLocationProvider.VolumeRoot(full);
            }
            catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
            {
                failures.Add(new(path, null, new(IndexIssueReason.InvalidLocation, path, "Konum doğrulama", failure.Message,
                    LocationSkipped: true)));
                continue;
            }
            var index = groups.FindIndex(group => string.Equals(group.Root, volume, StringComparison.OrdinalIgnoreCase));
            if (index < 0) groups.Add((volume, [full]));
            else groups[index].Roots.Add(full);
        }
        return groups.Select(group => (group.Root, group.Roots.Any(root => Path.TrimEndingDirectorySeparator(root)
            .Equals(Path.TrimEndingDirectorySeparator(group.Root), StringComparison.OrdinalIgnoreCase))
            ? null : (IReadOnlyList<string>?)group.Roots.ToArray())).ToList();
    }

    private static IndexIssue Skipped(Exception failure, string root) =>
        IndexIssue.FromException(failure, root, "İndeks açılışı") with { LocationSkipped = true };

    private IEnumerable<string> ReadControl()
    {
        try
        {
            return File.Exists(DatabasePath)
                ? File.ReadAllLines(DatabasePath).Select(line => line.Trim()).Where(line => line.Length != 0).ToArray()
                : [];
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return []; }
    }

    private void RemoveUnusedSnapshots(List<Volume> volumes)
    {
        var active = volumes.Select(volume => volume.SnapshotPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] paths;
        try { paths = Directory.GetFiles(_directory, "*", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice?.Invoke($"Eski indeks dosyaları temizlenemedi: {ex.Message}");
            return;
        }
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name);
            if (name.Length != 20 || !name.EndsWith(".idx", StringComparison.OrdinalIgnoreCase) ||
                !ulong.TryParse(name.AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _) ||
                active.Contains(Path.Combine(_directory, name))) continue;
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Notice?.Invoke($"Eski indeks dosyası temizlenemedi ({Path.GetFileName(path)}): {ex.Message}");
            }
        }
    }

    private IndexVolumeMethod MethodFor(string root)
    {
        var method = _policy?.MethodFor(root) ?? IndexVolumeMethod.Mft;
        if (method == IndexVolumeMethod.Folders) return method;
        var kind = root.StartsWith(@"\\", StringComparison.Ordinal) ? IndexVolumeKind.Other
            : _policy?.KindOf(root) ?? IndexVolumeKind.Unknown;
        if (kind != IndexVolumeKind.Other) return method;
        Notice?.Invoke($"{root} NTFS değil; klasör taraması kullanılıyor.");
        return IndexVolumeMethod.Folders;
    }

    private async Task<Volume> OpenVolumeAsync(string root, IReadOnlyList<string>? scope, bool rebuild, Stopwatch? clock, CancellationToken ct)
    {
        var method = MethodFor(root);
        if (method == IndexVolumeMethod.Folders) return await OpenFolderVolumeAsync(root, scope, rebuild, clock, ct).ConfigureAwait(false);
        try { return await OpenMftVolumeAsync(root, scope, rebuild, clock, ct).ConfigureAwait(false); }
        catch (IndexIssueException failure) when (method == IndexVolumeMethod.Automatic &&
            failure.Issue.Reason is IndexIssueReason.JournalInactive or IndexIssueReason.UnsupportedFileSystem)
        {
            Notice?.Invoke(failure.Issue.Reason == IndexIssueReason.JournalInactive
                ? $"{root} için Windows değişiklik günlüğü kapalı; klasör taraması kullanılıyor."
                : $"{root} MFT ile taranamıyor; klasör taraması kullanılıyor.");
            return await OpenFolderVolumeAsync(root, scope, rebuild, clock, ct).ConfigureAwait(false);
        }
    }

    private async Task<Volume> OpenMftVolumeAsync(string root, IReadOnlyList<string>? scope, bool rebuild, Stopwatch? clock, CancellationToken ct)
    {
        var info = await SendAsync(ChangeFeedRequestKind.IndexVolumeOpen, scope?[0] ?? root, null, null, ct).ConfigureAwait(false);
        if (scope is not null)
            foreach (var location in scope.Skip(1))
                await SendAsync(ChangeFeedRequestKind.IndexVolumeOpen, location, null, null, ct).ConfigureAwait(false);
        var volume = new Volume(root, scope, Path.Combine(_directory, $"{info.VolumeSerial:X16}.idx"));
        if (!rebuild)
        {
            Report($"İndeks yükleniyor: {root}", 0, clock);
            FileIndex? loaded;
            IndexCheckpoint checkpoint;
            if (TakePreload(volume.SnapshotPath) is { } preload) (loaded, checkpoint) = await preload.ConfigureAwait(false);
            else loaded = FileIndex.Load(volume.SnapshotPath, out checkpoint);
            if (loaded is not null && loaded.RootReference == info.RootReference && checkpoint.VolumeSerial == info.VolumeSerial &&
                checkpoint.JournalId == info.JournalId && checkpoint.NextUsn >= info.FirstUsn && checkpoint.NextUsn <= info.NextUsn &&
                string.Equals(loaded.RootPath, WithSeparator(root), StringComparison.OrdinalIgnoreCase))
            {
                loaded.NameSort = _buildOptions;
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

    private async Task<Volume> OpenFolderVolumeAsync(string root, IReadOnlyList<string>? scope, bool rebuild, Stopwatch? clock,
        CancellationToken ct)
    {
        var roots = FolderVolumeScanner.Roots(root, scope);
        var snapshotId = FolderSnapshotId(root);
        var watcher = new FolderVolumeWatcher(roots);
        var folder = new FolderState(watcher, roots);
        var volume = new Volume(root, scope, Path.Combine(_directory, $"{snapshotId:X16}.idx")) { Folder = folder };
        try
        {
            if (!rebuild)
            {
                Report($"İndeks yükleniyor: {root}", 0, clock);
                FileIndex? loaded;
                IndexCheckpoint checkpoint;
                if (TakePreload(volume.SnapshotPath) is { } preload) (loaded, checkpoint) = await preload.ConfigureAwait(false);
                else loaded = FileIndex.Load(volume.SnapshotPath, out checkpoint);
                if (loaded is not null && loaded.RootReference == FolderVolumeScanner.RootReference &&
                    checkpoint.VolumeSerial == snapshotId && checkpoint.JournalId == 0 &&
                    string.Equals(loaded.RootPath, WithSeparator(root), StringComparison.OrdinalIgnoreCase))
                {
                    loaded.NameSort = _buildOptions;
                    volume.Index = loaded;
                    volume.Checkpoint = checkpoint;
                    folder.References = new(loaded.MaxReference() + 1);
                    folder.RescanPending = true;
                    return volume;
                }
                loaded?.Dispose();
            }
            (volume.Index, folder.References) = await ScanFolderAsync(root, scope, clock, ct).ConfigureAwait(false);
            volume.Checkpoint = new(snapshotId, 0, 0);
            volume.LastRescan = DateTime.UtcNow;
            Save(volume);
            return volume;
        }
        catch
        {
            watcher.Dispose();
            volume.Index?.Dispose();
            throw;
        }
    }

    private async Task<(FileIndex Index, FolderReferences References)> ScanFolderAsync(string root, IReadOnlyList<string>? scope,
        Stopwatch? clock, CancellationToken ct)
    {
        var references = new FolderReferences(FolderVolumeScanner.FirstReference);
        var read = 0;
        var index = await Task.Run(() =>
        {
            var entries = FolderVolumeScanner.Scan(root, scope, references, clock is null ? null : count =>
            {
                Volatile.Write(ref read, count);
                Report($"Klasörler taranıyor: {root}", count, clock);
            }, ct);
            Volatile.Write(ref read, entries.Count);
            return FileIndex.Build(root, FolderVolumeScanner.RootReference, entries, ct, _buildOptions, clock is null ? null : (elapsed, workers) =>
                Report($"{SortMethodName(_buildOptions.SortMethod)} ad sıralaması: {elapsed.TotalMilliseconds:N0} ms · en çok {workers} işçi",
                    Volatile.Read(ref read), clock));
        }, ct).ConfigureAwait(false);
        return (index, references);
    }

    private static ulong FolderSnapshotId(string root)
    {
        var identity = "";
        try
        {
            using var reader = new NtfsDirectoryReader(root);
            identity = reader.VolumeIdentity;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or Win32Exception or
            ArgumentException or NotSupportedException)
        {
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant() + "|" + identity));
        return 0xF000_0000_0000_0000UL | (BinaryPrimitives.ReadUInt64LittleEndian(hash) & 0x0FFF_FFFF_FFFF_FFFFUL);
    }

    private static string WithSeparator(string root) => root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

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
                    var entries = page.BinaryEntries is { } binary
                        ? IndexEntryCodec.ReadBinaryEntries(binary, page.Count)
                        : IndexEntryCodec.UnpackEntries(page.Entries);
                    foreach (var entry in entries) yield return entry;
                    read += page.Count;
                    Report($"Disk taranıyor: {root}", read, clock);
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
        var index = await Task.Run(() => FileIndex.Build(root, info.RootReference, Entries(), ct,
            _buildOptions, clock is null ? null : (elapsed, workers) =>
                Report($"{SortMethodName(_buildOptions.SortMethod)} ad sıralaması: {elapsed.TotalMilliseconds:N0} ms · en çok {workers} işçi", read, clock)),
            ct).ConfigureAwait(false);
        return (index, checkpoint);
    }

    private static string SortMethodName(IndexSortMethod method) => method switch
    {
        IndexSortMethod.Ps5 => "pS⁵",
        IndexSortMethod.Merge => "Birleştir",
        _ => "Pivot"
    };

    private void Report(string status, int items, Stopwatch? clock)
    {
        if (clock is null) return;
        ProgressChanged?.Invoke(new() { Status = status, Phase = status, ItemCount = items, ElapsedMs = clock.ElapsedMilliseconds,
            IsIndeterminate = true, IsCatalogBuild = true });
    }

    private async Task<IndexVolumePageDto> SendAsync(ChangeFeedRequestKind kind, string root, string? token,
        IndexJournalPositionDto? journal, CancellationToken ct)
    {
        ChangeFeedResponse response;
        try
        {
            response = await _channel.SendAsync(new ChangeFeedRequest(ChangeFeedProtocol.Version, kind, RootPath: root,
                Token: token, Journal: journal, IndexBuild: kind == ChangeFeedRequestKind.IndexVolumeScan && token is null ? _buildOptions.ForService() : null,
                BinaryVolumeEntries: kind == ChangeFeedRequestKind.IndexVolumeScan), ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is IOException or ChangeFeedPipeException)
        {
            throw new IndexIssueException(IndexIssue.FromException(failure, root, kind.ToString()), failure);
        }
        if (response.Status != ChangeFeedResponseStatus.Ok || response.Volume is null)
            throw new IndexIssueException(IndexIssue.FromResponse(response, root, kind.ToString()));
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
                await PollAsync(ct).ConfigureAwait(false);
                await SaveIfQuietAsync(ct).ConfigureAwait(false);
                await RetryPendingAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task<IReadOnlyList<IndexIssue>> PollAsync(CancellationToken ct)
    {
        await _pollGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<Volume> volumes;
            List<PendingVolume> pending;
            lock (_gate)
            {
                volumes = _volumes;
                pending = _pending;
            }
            var changed = false;
            HashSet<string>? affectedNames = new(StringComparer.Ordinal);
            var includesDirectories = false;
            var issues = new List<IndexIssue>();
            void Track(IndexChangeImpact? impact)
            {
                changed = true;
                includesDirectories |= impact?.IncludesDirectories == true;
                if (impact?.Names is not { } names) affectedNames = null;
                else if (affectedNames != null)
                {
                    affectedNames.UnionWith(names);
                    if (affectedNames.Count > 4096) affectedNames = null;
                }
            }
            foreach (var volume in volumes)
            {
                if (volume.Folder is { } folder)
                {
                    try
                    {
                        if (PollFolder(volume, folder, issues, Track, ct))
                        {
                            changed = true;
                            includesDirectories = true;
                            affectedNames = null;
                        }
                    }
                    catch (Exception failure) when (failure is not OperationCanceledException)
                    {
                        issues.Add(IndexIssue.FromException(failure, volume.Root, "Arka plan güncellemesi"));
                    }
                    continue;
                }
                try
                {
                    while (true)
                    {
                        var page = await SendAsync(ChangeFeedRequestKind.IndexVolumeChanges, volume.Root, null,
                            new IndexJournalPositionDto(volume.Checkpoint.JournalId, volume.Checkpoint.NextUsn), ct).ConfigureAwait(false);
                        if (page.RescanRequired)
                        {
                            var reason = RescanReason(volume.Checkpoint, page);
                            if (DateTime.UtcNow - volume.LastRescan < RescanBackoff)
                            {
                                issues.Add(new(IndexIssueReason.JournalHistoryLost, volume.Root, "Yeniden tarama", reason));
                                break;
                            }
                            await RescanAsync(volume, reason, ct).ConfigureAwait(false);
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
                                Track(impact);
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
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    issues.Add(IndexIssue.FromException(failure, volume.Root, "Arka plan güncellemesi"));
                }
            }
            ReportHealth(CurrentIssues(issues, pending, volumes));
            if (!changed) return [.. issues, .. pending.Select(item => item.Issue)];
            _lastChange = DateTime.UtcNow;
            _publishes++;
            FileChanged?.Invoke(new()
            {
                ChangeType = FileChangeType.Modified,
                FullPath = "Katalog güncellendi",
                IsDirectory = includesDirectories,
                AffectedNames = affectedNames?.ToArray()
            });
            return [.. issues, .. pending.Select(item => item.Issue)];
        }
        finally { _pollGate.Release(); }
    }

    private bool PollFolder(Volume volume, FolderState folder, List<IndexIssue> issues, Action<IndexChangeImpact?> track,
        CancellationToken ct)
    {
        var replaced = false;
        if (folder.Rescan is { IsCompleted: true } finished)
        {
            folder.Rescan = null;
            if (finished.IsCompletedSuccessfully)
            {
                var (index, references) = finished.Result;
                FileIndex previous;
                lock (_gate)
                {
                    previous = volume.Index;
                    volume.Index = index;
                }
                folder.References = references;
                volume.Dirty = true;
                Save(volume);
                previous.Dispose();
                IndexMemory.Release();
                PrepareWords(index);
                volume.LastChange = DateTime.UtcNow;
                replaced = true;
            }
            else if (finished.Exception?.GetBaseException() is { } failure and not OperationCanceledException)
            {
                folder.RescanPending = true;
                issues.Add(IndexIssue.FromException(failure, volume.Root, "Klasör taraması"));
            }
        }
        if (folder.Watcher.TakeOverflow()) folder.RescanPending = true;
        if (folder.Rescan is null && folder.RescanPending && DateTime.UtcNow - volume.LastRescan >= RescanBackoff)
        {
            folder.RescanPending = false;
            volume.LastRescan = DateTime.UtcNow;
            var root = volume.Root;
            var scope = volume.Scope;
            var stop = _stop.Token;
            folder.Rescan = Task.Run(() => ScanFolderAsync(root, scope, null, stop), stop);
        }
        if (folder.Rescan is not null) return replaced;
        var dirty = folder.Watcher.TakeDirty();
        if (dirty.Count == 0) return replaced;
        var states = FolderVolumeSync.Collect(volume.Index, folder.Roots, dirty, folder.References, ct);
        if (states.Count == 0) return replaced;
        var result = volume.Index.Reconcile(states, out var impact, ct);
        if (result.Applied == 0) return replaced;
        track(impact);
        volume.Dirty = true;
        volume.LastChange = DateTime.UtcNow;
        return replaced;
    }

    private static IndexIssue[] CurrentIssues(IEnumerable<IndexIssue> issues, IEnumerable<PendingVolume> pending, IEnumerable<Volume> volumes) =>
        [.. issues, .. pending.Select(item => item.Issue), .. volumes.Select(volume => volume.SaveIssue).OfType<IndexIssue>(),
            .. volumes.SelectMany(WatchIssues)];

    private static IEnumerable<IndexIssue> WatchIssues(Volume volume) =>
        volume.Folder?.Watcher.Unwatched.Select(item => new IndexIssue(IndexIssueReason.LocationUnavailable, item.Root,
            "Klasör izleme", item.Reason)) ?? [];

    private async Task RetryPendingAsync(CancellationToken ct)
    {
        List<PendingVolume> pending;
        lock (_gate) pending = _pending;
        if (pending.Count == 0 || DateTime.UtcNow < _nextRetry) return;
        _nextRetry = DateTime.UtcNow + RetryInterval;
        var remaining = new List<PendingVolume>();
        var opened = new List<Volume>();
        foreach (var item in pending)
        {
            if (item.Issue.Reason == IndexIssueReason.InvalidLocation)
            {
                remaining.Add(item);
                continue;
            }
            try { opened.Add(await Task.Run(() => OpenVolumeAsync(item.Root, item.Scope, false, null, ct), ct).ConfigureAwait(false)); }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                remaining.Add(item with { Issue = Skipped(failure, item.Root) });
            }
        }
        if (opened.Count == 0)
        {
            lock (_gate) _pending = remaining;
            return;
        }
        await _pollGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    foreach (var volume in opened) Close(volume);
                    return;
                }
                _volumes = [.. _volumes, .. opened];
                _pending = remaining;
            }
            try
            {
                var listed = ReadControl().Concat(opened.Select(volume => volume.SnapshotPath)).Distinct(StringComparer.OrdinalIgnoreCase);
                File.WriteAllText(DatabasePath, string.Join(Environment.NewLine, listed));
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
        finally { _pollGate.Release(); }
        IndexMemory.Release();
        foreach (var volume in opened)
        {
            PrepareWords(volume.Index);
            Notice?.Invoke($"{volume.Root} indekse eklendi.");
        }
        _lastChange = DateTime.UtcNow;
        _publishes++;
        FileChanged?.Invoke(new()
        {
            ChangeType = FileChangeType.Modified,
            FullPath = "Katalog güncellendi",
            IsDirectory = true,
            AffectedNames = null
        });
    }

    private void ReportHealth(IReadOnlyList<IndexIssue> issues)
    {
        if (_reportedIssues.SequenceEqual(issues)) return;
        _reportedIssues = issues;
        HealthChanged?.Invoke(new(issues, SearchAvailable: true));
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
                    (volume.SaveIssue is not null || (volume.Folder is not null
                        ? now - volume.LastSaved >= FolderSaveInterval
                        : volume.SavedUsn < volume.FirstUsn + (volume.Checkpoint.NextUsn - volume.FirstUsn) / 4)))
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
            volume.SaveIssue = null;
            volume.SavedUsn = volume.Checkpoint.NextUsn;
            volume.LastSaved = DateTime.UtcNow;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            var issue = IndexIssue.FromException(failure, volume.Root, "İndeksi diske kaydetme");
            volume.SaveIssue = issue.Reason == IndexIssueReason.StorageFull ? issue : issue with { Reason = IndexIssueReason.IndexNotSaved };
            volume.Dirty = true;
            volume.LastChange = DateTime.UtcNow;
        }
    }

    public async Task<bool> EnsureSyncedAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsInitialized) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var issues = await PollAsync(linked.Token).ConfigureAwait(false);
        var root = Path.GetPathRoot(path);
        List<Volume> volumes;
        lock (_gate) volumes = _volumes;
        if (volumes.Any(volume => volume.Folder?.Rescan is { IsCompleted: false } &&
            string.Equals(Path.GetPathRoot(volume.Root), root, StringComparison.OrdinalIgnoreCase))) return false;
        return !issues.Any(issue => issue.Location is null ||
            string.Equals(Path.GetPathRoot(issue.Location), root, StringComparison.OrdinalIgnoreCase));
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

    private static void Close(Volume volume)
    {
        volume.Folder?.Watcher.Dispose();
        if (volume.Folder?.Rescan is { } rescan)
        {
            try { rescan.Wait(TimeSpan.FromSeconds(5)); }
            catch (AggregateException) { }
            if (rescan.IsCompletedSuccessfully) rescan.Result.Index.Dispose();
        }
        volume.Index?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _stop.Cancel();
        ReleasePreloads();
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        List<Volume> volumes;
        lock (_gate) volumes = _volumes;
        foreach (var volume in volumes)
        {
            volume.Folder?.Watcher.Dispose();
            if (volume.Dirty) Save(volume);
            Close(volume);
        }
    }
}
