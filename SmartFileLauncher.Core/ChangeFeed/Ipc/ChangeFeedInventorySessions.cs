using System.Threading.Channels;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.Indexing.Ntfs;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed class ChangeFeedInventorySessions : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Func<IReadOnlyList<ChangeFeedSubscribedRoot>, CancellationToken, NtfsInventorySecurityGuard?,
        IEnumerable<IndexInventoryEntry>> _read;
    private readonly Timer _cleanup;
    private readonly Action<Exception>? _onFailure;
    private readonly Func<NtfsInventorySecurityGuard>? _securityFactory;
    private bool _disposed;

    public ChangeFeedInventorySessions(Action<Exception>? onFailure = null)
        : this(new NtfsRootInventory().Read, () => new NtfsInventorySecurityGuard(), onFailure) { }

    internal ChangeFeedInventorySessions(Func<IReadOnlyList<ChangeFeedSubscribedRoot>,
        CancellationToken, IEnumerable<IndexInventoryEntry>> read, Action<Exception>? onFailure = null)
        : this((roots, ct, _) => read(roots, ct), null, onFailure) { }

    internal ChangeFeedInventorySessions(Func<IReadOnlyList<ChangeFeedSubscribedRoot>, CancellationToken,
        NtfsInventorySecurityGuard?, IEnumerable<IndexInventoryEntry>> read,
        Func<NtfsInventorySecurityGuard>? securityFactory, Action<Exception>? onFailure = null)
    {
        _read = read;
        _onFailure = onFailure;
        _securityFactory = securityFactory;
        _cleanup = new Timer(_ => Prune(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    internal string Start(ChangeFeedChainBinding binding, IReadOnlyList<ChangeFeedSubscribedRoot> roots)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Prune();
            if (_sessions.Remove(binding.OwnerSid, out var previous)) previous.Cancel();
            if (_sessions.Count >= ChangeFeedProtocol.MaximumConcurrentConnections)
                throw new IOException("Envanter oturum sınırına ulaşıldı.");
            var session = new Session(binding, roots.ToArray(), _securityFactory?.Invoke());
            _sessions.Add(binding.OwnerSid, session);
            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        foreach (var entry in _read(session.Roots, session.Cancellation.Token, session.Security))
                            await session.Entries.Writer.WriteAsync(entry, session.Cancellation.Token)
                                .ConfigureAwait(false);
                        session.Entries.Writer.TryComplete();
                    }
                    catch (Exception failure)
                    {
                        session.Failed = true;
                        session.Entries.Writer.TryComplete(failure);
                        if (failure is not OperationCanceledException) _onFailure?.Invoke(failure);
                    }
                    finally
                    {
                        session.Finish();
                    }
                });
            }
            return session.Token;
        }
    }

    internal IReadOnlyList<ChangeFeedSubscribedRoot>? Roots(string owner, string? token)
    {
        lock (_gate)
            return Find(owner, token)?.Roots;
    }

    internal ChangeFeedResponse Page(ChangeFeedChainBinding binding, string? token,
        Func<string, bool> canReturn, bool validateOnly, CancellationToken ct)
    {
        lock (_gate)
        {
            var session = Find(binding.OwnerSid, token);
            if (session is null || !session.Binding.MatchesInventory(binding, session.Security is not null) || session.Failed)
            {
                Cancel(binding.OwnerSid, token);
                return Failed(session is null ? "Oturum bulunamadı veya süresi doldu."
                    : session.Failed ? "MFT kaynak okuması başarısız."
                    : !session.Binding.Epoch.Matches(binding.Epoch) ? "MFT oturumunun kuyruk dönemi değişti."
                    : !session.Binding.Security.Matches(binding.Security) ? "MFT oturumunun güvenlik damgası değişti."
                    : "Abonelik veya güvenlik bağı değişti.");
            }
            session.LastAccess = DateTime.UtcNow;
            if (validateOnly)
                return session.Completed && ValidateSecurity(session, ct) ? ChangeFeedResponse.Ok() : Failed();
            if (session.Completed) return Failed();

            var entries = new List<ChangeFeedInventoryEntryDto>();
            long bytes = 1024;
            for (var count = 0; count < 512 && session.Entries.Reader.TryPeek(out var candidate); count++)
            {
                ct.ThrowIfCancellationRequested();
                var allowed = canReturn(candidate.Path);
                var dto = allowed ? ChangeFeedInventoryEntryDto.FromEntry(candidate) : null;
                var size = dto is null ? 0 : ChangeFeedMessageChannel.MeasureResponse(dto) + 1;
                if (size + 1024 > ChangeFeedProtocol.MaximumResponseBytes)
                {
                    Cancel(binding.OwnerSid, token);
                    return Failed();
                }
                if (entries.Count > 0 && bytes + size > 256 * 1024) break;
                session.Entries.Reader.TryRead(out _);
                if (dto is not null) entries.Add(dto);
                bytes += size;
            }
            if (session.Failed)
            {
                Cancel(binding.OwnerSid, token);
                return Failed("MFT kaynak okuması başarısız.");
            }
            session.Completed = session.Entries.Reader.Completion.IsCompletedSuccessfully;
            if (session.Completed && !ValidateSecurity(session, ct))
            {
                Cancel(binding.OwnerSid, token);
                return Failed("MFT kapsamının erişim güvenliği veya USN sürekliliği değişti.");
            }
            session.Token = Guid.NewGuid().ToString("N");
            return new ChangeFeedResponse(ChangeFeedProtocol.Version, ChangeFeedResponseStatus.Ok,
                Inventory: new ChangeFeedInventoryPageDto(session.Token, entries, session.Completed));
        }
    }

    private bool ValidateSecurity(Session session, CancellationToken ct)
    {
        try { return session.Security?.Validate(ct) ?? true; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            _onFailure?.Invoke(failure);
            return false;
        }
    }

    internal void Cancel(string owner, string? token)
    {
        lock (_gate)
        {
            if (Find(owner, token) is not { } session) return;
            _sessions.Remove(owner);
            session.Cancel();
        }
    }

    private Session? Find(string owner, string? token) =>
        token is not null && _sessions.TryGetValue(owner, out var session) &&
        session.Token == token && DateTime.UtcNow - session.LastAccess < TimeSpan.FromMinutes(5)
            ? session : null;

    private void Prune()
    {
        lock (_gate)
        {
            foreach (var (owner, session) in _sessions.ToArray())
            {
                if (DateTime.UtcNow - session.LastAccess < TimeSpan.FromMinutes(5)) continue;
                _sessions.Remove(owner);
                session.Cancel();
            }
        }
    }

    private static ChangeFeedResponse Failed(string reason = "Envanter tamamlanamadı veya geçerliliğini kaybetti.") =>
        ChangeFeedResponse.Failed(ChangeFeedResponseStatus.Unavailable, reason);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cleanup.Dispose();
            foreach (var session in _sessions.Values) session.Cancel();
            _sessions.Clear();
        }
    }

    private sealed class Session(ChangeFeedChainBinding binding, ChangeFeedSubscribedRoot[] roots,
        NtfsInventorySecurityGuard? security)
    {
        private readonly object _lifetime = new();
        private bool _finished;
        private bool _cancelled;
        public NtfsInventorySecurityGuard? Security { get; } = security;
        public ChangeFeedChainBinding Binding { get; } = binding;
        public ChangeFeedSubscribedRoot[] Roots { get; } = roots;
        public string Token { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime LastAccess { get; set; } = DateTime.UtcNow;
        public Channel<IndexInventoryEntry> Entries { get; } = Channel.CreateBounded<IndexInventoryEntry>(512);
        public CancellationTokenSource Cancellation { get; } = new();
        public volatile bool Failed;
        public bool Completed { get; set; }

        public void Cancel()
        {
            lock (_lifetime)
            {
                _cancelled = true;
                if (!_finished) Cancellation.Cancel();
                else Security?.Dispose();
            }
        }

        public void Finish()
        {
            lock (_lifetime)
            {
                _finished = true;
                Cancellation.Dispose();
                if (_cancelled) Security?.Dispose();
            }
        }
    }
}
