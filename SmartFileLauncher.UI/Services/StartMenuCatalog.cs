using System.Diagnostics;
using System.Globalization;

namespace SmartFileLauncher.UI.Services;

public sealed class StartMenuCatalog : IDisposable
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(10);
    private const int MaximumPendingChanges = 64;

    private readonly Func<CancellationToken, IReadOnlyList<StartMenuApplication>> _readApplications;
    private readonly Func<string, StartMenuApplication?>? _readItem;
    private readonly TimeProvider _time;
    private readonly CultureInfo _culture;
    private readonly Action<Exception>? _reportError;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();

    private StartMenuApplications? _current;
    private IReadOnlyList<StartMenuApplication>? _sourceEntries;
    private List<ResolveStartMenuChange>? _pendingChanges;
    private TaskCompletionSource? _refreshCompletion;
    private long? _lastSuccessfulRead;
    private bool _lastReadFailed;
    private bool _refreshPending;
    private bool _readingFullCatalog;
    private bool _disposed;

    public StartMenuCatalog(
        Func<CancellationToken, IReadOnlyList<StartMenuApplication>> readApplications,
        TimeProvider? timeProvider = null,
        CultureInfo? culture = null,
        Action<Exception>? reportError = null)
    {
        _readApplications = readApplications ?? throw new ArgumentNullException(nameof(readApplications));
        _time = timeProvider ?? TimeProvider.System;
        _culture = culture ?? CultureInfo.CurrentCulture;
        _reportError = reportError;
    }

    public StartMenuApplications Current => Volatile.Read(ref _current) ?? StartMenuApplications.Empty;

    internal StartMenuCatalog(
        Func<CancellationToken, IReadOnlyList<StartMenuApplication>> readApplications,
        Func<string, StartMenuApplication?> readItem,
        TimeProvider? timeProvider = null,
        CultureInfo? culture = null,
        Action<Exception>? reportError = null)
        : this(readApplications, timeProvider, culture, reportError) => _readItem = readItem;
    public bool IsReady => Volatile.Read(ref _current) is not null;

    public event Action? SnapshotChanged;

    internal Task RefreshCompletion
    {
        get
        {
            lock (_gate)
                return _refreshCompletion?.Task ?? Task.CompletedTask;
        }
    }

    public void OnActivated() => OnActivated(hasCompleteWatch: false);

    internal void OnActivated(bool hasCompleteWatch)
    {
        lock (_gate)
        {
            if (_disposed || _readingFullCatalog || _refreshPending)
                return;

            if (_lastReadFailed || _lastSuccessfulRead is not long timestamp ||
                (!hasCompleteWatch && _time.GetElapsedTime(timestamp) >= MaximumAge))
            {
                QueueRead();
            }
        }
    }

    public void RequestRefresh()
        => RequestChange(null);

    internal void RequestChange(ResolveStartMenuChange? change)
    {
        lock (_gate)
        {
            if (!_disposed)
                QueueRead(change);
        }
    }

    public void Dispose()
    {
        Task cancellation;
        Task completion;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _refreshPending = false;
            _pendingChanges = null;
            SnapshotChanged = null;
            completion = _refreshCompletion?.Task ?? Task.CompletedTask;
            cancellation = _shutdown.CancelAsync();
        }

        _ = DisposeAfterCancellationAsync(cancellation, completion);
    }

    private void QueueRead(ResolveStartMenuChange? change = null)
    {
        if (change == null || _pendingChanges?.Count >= MaximumPendingChanges)
        {
            _refreshPending = true;
            _pendingChanges = null;
        }
        else if (!_refreshPending)
        {
            (_pendingChanges ??= []).Add(change);
        }
        if (_refreshCompletion is not null)
            return;

        var worker = new Thread(RefreshLoop)
        {
            IsBackground = true,
            Name = "OmniSpot Start Menu Catalog"
        };
        worker.SetApartmentState(ApartmentState.STA);
        _refreshCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            worker.Start();
        }
        catch
        {
            _refreshCompletion = null;
            _refreshPending = false;
            _pendingChanges = null;
            _lastReadFailed = true;
            throw;
        }
    }

    private void RefreshLoop()
    {
        var cancellation = _shutdown.Token;
        while (TryBeginRead(out var changes))
            ReadAndPublish(changes, cancellation);
    }

    private bool TryBeginRead(out List<ResolveStartMenuChange>? changes)
    {
        lock (_gate)
        {
            _readingFullCatalog = false;
            changes = _pendingChanges;
            _pendingChanges = null;
            if (_refreshPending || changes != null)
            {
                _refreshPending = false;
                return true;
            }

            _refreshCompletion!.SetResult();
            _refreshCompletion = null;
            return false;
        }
    }

    private void ReadAndPublish(List<ResolveStartMenuChange>? changes, CancellationToken cancellation)
    {
        StartMenuApplications snapshot;
        IReadOnlyList<StartMenuApplication>? entries = null;
        HashSet<string>? changedIds = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (changes != null && _sourceEntries != null && !_lastReadFailed && _readItem != null)
            {
                try { entries = ApplyChanges(_sourceEntries, changes, cancellation, out changedIds); }
                catch (Exception error) when (!cancellation.IsCancellationRequested) { Debug.WriteLine(error); }
            }
            if (entries == null)
            {
                lock (_gate)
                {
                    _readingFullCatalog = true;
                    _refreshPending = false;
                    _pendingChanges = null;
                }
                cancellation.ThrowIfCancellationRequested();
                entries = _readApplications(cancellation);
                changedIds = null;
            }
            cancellation.ThrowIfCancellationRequested();
            snapshot = new StartMenuApplications(entries, _culture) { ChangedIds = changedIds };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _lastReadFailed = true;
            }

            ReportError(error);
            return;
        }

        Action? changed;
        lock (_gate)
        {
            if (_disposed)
                return;

            if (changedIds == null) _lastSuccessfulRead = _time.GetTimestamp();
            _sourceEntries = entries;
            _lastReadFailed = false;
            Volatile.Write(ref _current, snapshot);
            changed = SnapshotChanged;
        }

        try
        {
            if (!cancellation.IsCancellationRequested)
                changed?.Invoke();
        }
        catch (Exception error)
        {
            ReportError(error);
        }
    }

    private IReadOnlyList<StartMenuApplication>? ApplyChanges(IReadOnlyList<StartMenuApplication> source,
        List<ResolveStartMenuChange> changes, CancellationToken cancellation, out HashSet<string> changedIds)
    {
        var entries = new Dictionary<string, StartMenuApplication>(source.Count, StringComparer.Ordinal);
        foreach (var entry in source) entries.TryAdd(entry.Id, entry);

        changedIds = new(StringComparer.Ordinal);
        var names = StringComparer.Create(_culture, ignoreCase: true);
        var updatedNames = new HashSet<string>(names);
        foreach (var resolve in changes)
        {
            cancellation.ThrowIfCancellationRequested();
            var change = resolve();
            if (change == null || (change.RemovedId == null && change.Item == null)) return null;
            if (change.RemovedId is { } oldId)
            {
                if (!entries.Remove(oldId)) return null;
                changedIds.Add(oldId);
            }
            if (change.Item is { } item)
            {
                if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name)) return null;
                if (change.RemovedId is { } previousId && previousId != item.Id && entries.ContainsKey(item.Id))
                    return null;
                entries[item.Id] = item;
                changedIds.Add(item.Id);
                updatedNames.Add(item.Name);
            }
        }

        foreach (var group in entries.Values.Where(app => updatedNames.Contains(app.Name)).GroupBy(app => app.Name, names))
        {
            if (group.Count() < 2 || source.Count(app => names.Equals(app.Name, group.Key)) != 1) continue;
            foreach (var peer in group)
            {
                if (changedIds.Contains(peer.Id)) continue;
                cancellation.ThrowIfCancellationRequested();
                var detail = _readItem!(peer.Id);
                if (detail?.Id != peer.Id || detail.Name != peer.Name) return null;
                entries[peer.Id] = detail;
            }
        }
        return entries.Values.ToArray();
    }

    private async Task DisposeAfterCancellationAsync(Task cancellation, Task completion)
    {
        try
        {
            await Task.WhenAll(cancellation, completion).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ReportError(error);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private void ReportError(Exception error)
    {
        try
        {
            _reportError?.Invoke(error);
        }
        catch (Exception reportingError)
        {
            Debug.WriteLine(reportingError);
        }
    }
}
