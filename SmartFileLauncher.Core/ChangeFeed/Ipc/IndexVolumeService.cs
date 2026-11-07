using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Indexing.Ntfs;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

[SupportedOSPlatform("windows")]
internal sealed class IndexVolumeService : IDisposable
{
    private const int PageBudgetBytes = 8 * 1024 * 1024;
    private const int MaximumStatesPerPage = 20_000;
    private const int MaximumJournalPagesPerRequest = 64;
    private static readonly TimeSpan ScanIdleLimit = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ResolveBudget = TimeSpan.FromSeconds(1);
    private readonly object _gate = new();
    private readonly Dictionary<string, ScanSession> _scans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NtfsMftReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly UsnFileSystemIdentityProbe _identity = new();
    private bool _disposed;

    private sealed class ScanSession(string owner, string volume, NtfsMftReader reader, IEnumerator<NtfsMftEntry> entries,
        CancellationTokenSource stop) : IDisposable
    {
        internal readonly string Owner = owner;
        internal readonly string Volume = volume;
        internal readonly IEnumerator<NtfsMftEntry> Entries = entries;
        internal readonly object Gate = new();
        internal DateTime LastSeen = DateTime.UtcNow;

        public void Dispose()
        {
            stop.Cancel();
            try { Entries.Dispose(); } catch (OperationCanceledException) { }
            reader.Dispose();
            stop.Dispose();
        }
    }

    internal ChangeFeedResponse Handle(ChangeFeedCaller caller, ChangeFeedRequest request, CancellationToken cancellationToken)
    {
        var owner = caller.Run(sid => sid.Value);
        if (string.IsNullOrWhiteSpace(request.RootPath) || !Path.IsPathFullyQualified(request.RootPath) ||
            request.RootPath.StartsWith(@"\\", StringComparison.Ordinal))
            return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.InvalidRequest, "İndeks kökü geçersiz.");
        string volume;
        try
        {
            volume = UsnVolumeJournalReader.ResolveVolumeRoot(request.RootPath);
            if (!new DriveInfo(volume).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.UnsupportedFileSystem, "Bu dosya sisteminde MFT/USN desteklenmiyor.");
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or IOException)
        { return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.UnsupportedFileSystem, failure.Message); }
        if (!caller.Run(_ => SharedCatalogService.CanList(request.RootPath)))
            return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.RootUnauthorized, "İndeks kökü okunamıyor.");
        try
        {
            return request.Kind switch
            {
                ChangeFeedRequestKind.IndexVolumeOpen => Open(volume),
                ChangeFeedRequestKind.IndexVolumeScan => Scan(owner, volume, request.Token, request.IndexBuild),
                ChangeFeedRequestKind.IndexVolumeChanges => Changes(volume, request.Journal, cancellationToken),
                _ => ChangeFeedResponse.Failed(ChangeFeedResponseStatus.InvalidRequest, "Bilinmeyen birim isteği.")
            };
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException or
            UsnJournalUnavailableException or UsnProtocolRejectedException or System.ComponentModel.Win32Exception)
        {
            return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.Unavailable, failure.Message);
        }
    }

    private ChangeFeedResponse Open(string volume)
    {
        if (!_identity.TryReadIdentity(volume, out var identity))
            return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.Unavailable, "Birim kökü kimliği okunamadı.");
        using var journal = new UsnVolumeJournalReader(volume);
        var descriptor = journal.QueryJournal();
        return Page(new IndexVolumePageDto(volume, identity.FileReference.Low, identity.VolumeSerialNumber,
            descriptor.JournalId, descriptor.FirstUsn, descriptor.NextUsn));
    }

    private ChangeFeedResponse Scan(string owner, string volume, string? token, IndexBuildOptions? options)
    {
        ScanSession session;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ExpireScans();
            if (token is null)
            {
                if (options is null || !options.IsValid)
                    return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.InvalidRequest, "İndeks tarama ayarları geçersiz.");
                var stop = new CancellationTokenSource();
                var reader = new NtfsMftReader(volume);
                var entries = reader.ReadEntries(options, stop.Token).GetEnumerator();
                token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                session = new ScanSession(owner, volume, reader, entries, stop);
                _scans[token] = session;
            }
            else if (!_scans.TryGetValue(token, out session!) || session.Owner != owner ||
                     !session.Volume.Equals(volume, StringComparison.OrdinalIgnoreCase))
                return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.StaleChain, "Tarama oturumu bulunamadı.");
        }
        lock (session.Gate)
        {
            session.LastSeen = DateTime.UtcNow;
            var batch = new List<IndexEntry>();
            var bytes = 0L;
            var done = false;
            while (bytes < PageBudgetBytes)
            {
                if (!session.Entries.MoveNext()) { done = true; break; }
                var entry = IndexEntry.FromMft(session.Entries.Current);
                batch.Add(entry);
                bytes += 48 + entry.Name.Length * 3;
            }
            if (done)
            {
                lock (_gate)
                    if (_scans.Remove(token)) session.Dispose();
                ReleaseLater();
            }
            return Page(new IndexVolumePageDto(volume, 0, 0, 0, 0, 0, done ? null : token,
                IndexEntryCodec.PackEntries(batch), Count: batch.Count, Done: done));
        }
    }

    private ChangeFeedResponse Changes(string volume, IndexJournalPositionDto? position, CancellationToken cancellationToken)
    {
        if (position is null)
            return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.InvalidRequest, "USN konumu eksik.");
        using var journal = new UsnVolumeJournalReader(volume);
        var descriptor = journal.QueryJournal();
        if (descriptor.JournalId != position.JournalId || position.NextUsn < descriptor.FirstUsn || position.NextUsn > descriptor.NextUsn)
            return Page(new IndexVolumePageDto(volume, 0, 0, descriptor.JournalId, descriptor.FirstUsn, descriptor.NextUsn,
                RescanRequired: true));
        var order = new List<ulong>();
        var firstSeen = new List<long>();
        var seen = new HashSet<ulong>();
        var cursor = position.NextUsn;
        var done = true;
        var records = new List<UsnRecord>();
        for (var page = 0; page < MaximumJournalPagesPerRequest && cursor < descriptor.NextUsn; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = journal.ReadPage(cursor, descriptor.JournalId);
            records.Clear();
            UsnRecordParser.Parse(read.Records.Span, records);
            var stopped = false;
            foreach (var record in records)
            {
                if (order.Count >= MaximumStatesPerPage && !seen.Contains(record.FileReference.Low))
                {
                    cursor = record.Usn;
                    stopped = true;
                    break;
                }
                if (!seen.Add(record.FileReference.Low)) continue;
                order.Add(record.FileReference.Low);
                firstSeen.Add(record.Usn);
            }
            if (stopped) { done = false; break; }
            if (read.NextUsn <= cursor) break;
            cursor = read.NextUsn;
            if (page == MaximumJournalPagesPerRequest - 1 && cursor < descriptor.NextUsn) done = false;
        }
        var states = new List<IndexRecordState>(order.Count);
        if (order.Count > 0)
        {
            var reader = Reader(volume);
            var clock = Stopwatch.StartNew();
            lock (reader)
                for (var i = 0; i < order.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (i > 0 && clock.Elapsed > ResolveBudget)
                    {
                        cursor = firstSeen[i];
                        done = false;
                        break;
                    }
                    try
                    {
                        var links = reader.ReadCurrent(order[i], cancellationToken).Select(IndexEntry.FromMft).ToList();
                        states.Add(new IndexRecordState(order[i], links));
                    }
                    catch (Exception failure) when (failure is InvalidDataException or IOException or Win32Exception or UnauthorizedAccessException) { }
                }
        }
        return Page(new IndexVolumePageDto(volume, 0, 0, descriptor.JournalId, descriptor.FirstUsn, cursor,
            States: states.Count == 0 ? null : IndexEntryCodec.PackStates(states), Count: states.Count, Done: done));
    }

    private NtfsMftReader Reader(string volume)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_readers.TryGetValue(volume, out var reader)) _readers[volume] = reader = new NtfsMftReader(volume);
            return reader;
        }
    }

    private void ExpireScans()
    {
        foreach (var (token, session) in _scans.ToArray())
        {
            if (DateTime.UtcNow - session.LastSeen < ScanIdleLimit) continue;
            _scans.Remove(token);
            session.Dispose();
            ReleaseLater();
        }
    }

    private static void ReleaseLater() =>
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => IndexMemory.Release(), TaskScheduler.Default);

    private static ChangeFeedResponse Page(IndexVolumePageDto page) =>
        new(ChangeFeedProtocol.Version, ChangeFeedResponseStatus.Ok, Volume: page);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var session in _scans.Values) session.Dispose();
            _scans.Clear();
            foreach (var reader in _readers.Values) reader.Dispose();
            _readers.Clear();
        }
    }
}
