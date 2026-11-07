namespace SmartFileLauncher.Core.Search.Paged;

internal sealed class CatalogStore : IDisposable
{
    private readonly object _snapshotGate = new();
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly string _directory;
    private readonly FileStream? _writerOwnership;
    private Catalog? _current;
    private bool _disposed;
    private int _layoutVersion;

    internal CatalogStore(Catalog catalog, bool writable)
    {
        if (catalog.Data.Mutable) throw new InvalidOperationException("Katalog yayımlanmadan açılamaz.");
        _directory = catalog.Data.Directory;
        if (writable)
            _writerOwnership = new FileStream(Path.Combine(_directory, ".writer"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _current = catalog.Retain();
    }

    internal static CatalogStore Open(string directory, bool writable = false, string? frame = null)
    {
        using var catalog = new Catalog(Image.Open(directory, frame));
        return new CatalogStore(catalog, writable);
    }

    internal Catalog Acquire()
    {
        lock (_snapshotGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _current!.Retain();
        }
    }

    internal Reader Query() => new(Acquire(), ownsCatalog: true);

    internal void Refresh()
    {
        if (_writerOwnership is not null) throw new InvalidOperationException("Yazıcı kendi yayımladığı sürümü kullanır.");
        _writerGate.Wait();
        try
        {
            using var baseline = Acquire();
            using var next = new Catalog(Image.Open(_directory, previous: baseline.Data));
            lock (_snapshotGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_current!.Data.Generation >= next.Data.Generation) return;
                if (_current.Data.RootFrn != next.Data.RootFrn ||
                    _current.Data.RootPath != next.Data.RootPath ||
                    _current.Data.VolumeIdentity != next.Data.VolumeIdentity)
                    throw new InvalidDataException("Katalog kök kimliği değişti.");
                var previous = _current;
                _current = next.Retain();
                previous.Dispose();
            }
        }
        finally { _writerGate.Release(); }
    }

    internal void Apply(IReadOnlyList<Change> changes, long cursor)
    {
        EnsureWriter();
        _writerGate.Wait();
        try
        {
            using var current = Acquire();
            if (cursor < current.Data.Cursor) throw new InvalidDataException("Katalog imleci geriye gidemez.");
            using var next = current.Fork();
            foreach (var change in changes) next.Apply(change);
            next.Data.Commit(cursor);
            Publish(next);
        }
        finally { _writerGate.Release(); }
    }

    internal void Commit(Action<Catalog> mutate, long cursor, string? deliveryId = null)
    {
        EnsureWriter();
        _writerGate.Wait();
        try
        {
            using var current = Acquire();
            if (deliveryId is not null && current.Data.DeliveryId == deliveryId) return;
            using var next = current.Fork();
            mutate(next);
            if (deliveryId is not null) next.Data.DeliveryId = deliveryId;
            next.Data.Commit(cursor);
            Publish(next);
        }
        finally { _writerGate.Release(); }
    }

    internal void Compact()
    {
        EnsureWriter();
        _writerGate.Wait();
        try
        {
            using var current = Acquire();
            using var next = current.Compact();
            Publish(next);
            _layoutVersion++;
            next.Data.Reclaim();
        }
        finally { _writerGate.Release(); }
    }

    internal async Task<bool> PrepareNameOrderAsync(CancellationToken ct, SmartFileLauncher.Core.Application.Settings.IndexBuildOptions? options = null)
    {
        EnsureWriter();
        Catalog baseline;
        int layoutVersion;
        await _writerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            baseline = Acquire();
            layoutVersion = _layoutVersion;
        }
        finally { _writerGate.Release(); }
        using (baseline)
        {
            if (baseline.Data.SortedCount > 0) return false;
            var plan = await Task.Run(() => baseline.CreateNameOrder(ct, options), ct).ConfigureAwait(false);
            await _writerGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var current = Acquire();
                if (current.Data.SortedCount > 0 || _layoutVersion != layoutVersion) return false;
                using var next = current.Fork();
                next.ApplyNameOrder(plan, ct);
                ct.ThrowIfCancellationRequested();
                next.Data.Commit(current.Data.Cursor);
                Publish(next);
                return true;
            }
            finally { _writerGate.Release(); }
        }
    }

    private void EnsureWriter()
    {
        if (_writerOwnership is null) throw new InvalidOperationException("Bu katalog salt okunur.");
    }

    internal async Task<bool> PrepareTokenBatchAsync(CancellationToken ct, int maximumSlots = 8192)
    {
        EnsureWriter();
        if (maximumSlots < 1) throw new ArgumentOutOfRangeException(nameof(maximumSlots));
        await _writerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var current = Acquire();
            if (current.Data.TermsReady) return false;
            using var next = current.Fork();
            next.PrepareTokenBatch(maximumSlots, ct);
            ct.ThrowIfCancellationRequested();
            next.Data.Commit(current.Data.Cursor);
            Publish(next);
            return true;
        }
        finally { _writerGate.Release(); }
    }

    private void Publish(Catalog next)
    {
        lock (_snapshotGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previous = _current!;
            _current = next.Retain();
            previous.Dispose();
        }
    }

    public void Dispose()
    {
        _writerGate.Wait();
        try
        {
            lock (_snapshotGate)
            {
                if (_disposed) return;
                _disposed = true;
                _current?.Dispose();
                _current = null;
                _writerOwnership?.Dispose();
            }
        }
        finally { _writerGate.Release(); }
    }
}
