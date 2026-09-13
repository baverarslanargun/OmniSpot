namespace SmartFileLauncher.Core.Indexing.Ntfs;

public sealed class NtfsMftReader : IDisposable
{
    private const ulong SegmentMask = 0x0000FFFFFFFFFFFF;
    private const int MaxAttributeListBytes = 16 * 1024 * 1024;
    private readonly INtfsVolumeSource _source;
    private readonly NtfsDataExtent _mft;
    private bool _disposed;
    private int _enumerating;

    public NtfsMftReader(string volumeRootPath)
        : this(new WindowsNtfsVolumeSource(volumeRootPath))
    {
    }

    internal NtfsMftReader(INtfsVolumeSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        try
        {
            var record = ReadFileRecord(0, includeDataRuns: true);
            CompleteRecord(record, includeDataRuns: true, CancellationToken.None);
            var extents = record.DataExtents.OrderBy(extent => extent.LowestVcn).ToArray();
            if (extents.Length == 0 || extents[0].LowestVcn != 0)
                throw new InvalidDataException("MFT veri akışının başlangıcı bulunamadı.");
            var runs = extents.SelectMany(extent => extent.Runs).ToArray();
            ValidateRuns(runs);
            var length = _source.Geometry.MftValidDataLength;
            if (length <= 0 || length % _source.Geometry.BytesPerRecord != 0 ||
                length > extents[0].Length ||
                length > checked((runs[^1].StartVcn + runs[^1].ClusterCount) * _source.Geometry.BytesPerCluster))
                throw new InvalidDataException("MFT veri uzunluğu veya parça kapsamı geçersiz.");
            _mft = new NtfsDataExtent(0, length, runs);
        }
        catch
        {
            _source.Dispose();
            throw;
        }
    }

    public IEnumerable<NtfsMftEntry> ReadEntries(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _enumerating, 1, 0) != 0)
            throw new InvalidOperationException("MFT okuyucusu aynı anda yalnız bir tarama çalıştırabilir.");
        try
        {
            var recordSize = _source.Geometry.BytesPerRecord;
            var buffer = new byte[Math.Max(1024 * 1024, recordSize)];
            long position = 0;
            while (position < _mft.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                var count = (int)Math.Min(buffer.Length, _mft.Length - position);
                ReadMapped(_mft.Runs, position, buffer.AsSpan(0, count), cancellationToken);
                for (var offset = 0; offset < count; offset += recordSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var record = ReadRawRecord(buffer, offset, recordSize,
                        (ulong)((position + offset) / recordSize));
                    if (record is null || record.BaseReference != 0)
                        continue;
                    CompleteRecord(record, includeDataRuns: false, cancellationToken);
                    if (record.Names.Count == 0)
                        continue;
                    var metadata = record.BasicInformation ??
                        throw new InvalidDataException("MFT kaydının temel dosya bilgileri bulunamadı.");
                    foreach (var name in record.Names.Where(name => name.Namespace != 2)
                                 .DistinctBy(name => (name.ParentReference, name.Name)))
                    {
                        yield return new NtfsMftEntry(
                            record.Reference, name.ParentReference, name.Name,
                            record.IsDirectory,
                            metadata.Attributes | (record.IsDirectory ? FileAttributes.Directory : 0),
                            record.IsDirectory ? 0 : record.SizeBytes ?? 0,
                            metadata.CreatedTimeUtc, metadata.LastWriteTimeUtc);
                    }
                }
                position += count;
            }
        }
        finally
        {
            Volatile.Write(ref _enumerating, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _source.Dispose();
    }

    private NtfsMftRecord ReadFileRecord(ulong reference, bool includeDataRuns)
    {
        var bytes = _source.ReadFileRecord(reference);
        if (bytes is null)
            throw new InvalidDataException("MFT segmenti artık kullanımda değil.");
        var record = NtfsMftRecordParser.Parse(bytes, reference & SegmentMask,
            restoreSectorTails: false, includeDataRuns: includeDataRuns);
        if (record is null || (reference >> 48 != 0 && record.Reference != reference))
            throw new InvalidDataException("MFT segmenti silinmiş veya yeniden kullanılmış.");
        return record;
    }

    private NtfsMftRecord? ReadRawRecord(byte[] buffer, int offset, int size, ulong segment)
    {
        try
        {
            return NtfsMftRecordParser.Parse(buffer.AsSpan(offset, size), segment,
                restoreSectorTails: true);
        }
        catch (InvalidDataException)
        {
            var current = _source.ReadFileRecord(segment);
            return current is null ? null : NtfsMftRecordParser.Parse(current, segment,
                restoreSectorTails: false);
        }
    }

    private void CompleteRecord(
        NtfsMftRecord record, bool includeDataRuns, CancellationToken cancellationToken)
    {
        if (record.ResidentAttributeList is null && record.NonresidentAttributeList is null)
            return;
        var visited = new HashSet<ulong> { record.Reference };
        var pending = new Queue<NtfsMftRecord>();
        pending.Enqueue(record);
        while (pending.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = ReadAttributeList(current, cancellationToken);
            foreach (var reference in NtfsMftRecordParser.ReadAttributeReferences(bytes))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visited.Add(reference))
                    continue;
                if (visited.Count > 65536)
                    throw new InvalidDataException("MFT öznitelik segmenti sınırı aşıldı.");
                var extension = ReadFileRecord(reference, includeDataRuns);
                if (extension.BaseReference != record.Reference)
                    throw new InvalidDataException("MFT uzantısı farklı bir dosyaya ait.");
                record.Names.AddRange(extension.Names);
                record.DataExtents.AddRange(extension.DataExtents);
                if (extension.BasicInformation is not null)
                {
                    if (record.BasicInformation is not null && record.BasicInformation != extension.BasicInformation)
                        throw new InvalidDataException("MFT temel dosya bilgileri tutarsız.");
                    record.BasicInformation = extension.BasicInformation;
                }
                if (extension.SizeBytes is not null)
                {
                    if (record.SizeBytes is not null && record.SizeBytes != extension.SizeBytes)
                        throw new InvalidDataException("MFT dosya boyutu tutarsız.");
                    record.SizeBytes = extension.SizeBytes;
                }
                if (extension.ResidentAttributeList is not null || extension.NonresidentAttributeList is not null)
                    pending.Enqueue(extension);
            }
        }
    }

    private byte[] ReadAttributeList(NtfsMftRecord record, CancellationToken cancellationToken)
    {
        if (record.ResidentAttributeList is not null)
            return record.ResidentAttributeList;
        var extent = record.NonresidentAttributeList;
        if (extent is null)
            return [];
        if (extent.Length is < 0 or > MaxAttributeListBytes)
            throw new InvalidDataException("MFT öznitelik listesi boyutu sınır dışında.");
        ValidateRuns(extent.Runs);
        var bytes = new byte[(int)extent.Length];
        ReadMapped(extent.Runs, 0, bytes, cancellationToken);
        return bytes;
    }

    private void ValidateRuns(IReadOnlyList<NtfsDataRun> runs)
    {
        long nextVcn = 0;
        foreach (var run in runs)
        {
            if (run.StartVcn != nextVcn || run.ClusterCount <= 0 ||
                run.ClusterCount > _source.Geometry.TotalClusters - nextVcn ||
                (run.Lcn is long lcn &&
                 (lcn < 0 || run.ClusterCount > _source.Geometry.TotalClusters - lcn)))
                throw new InvalidDataException("MFT parçaları eksik, çakışıyor veya birim sınırını aşıyor.");
            nextVcn = checked(nextVcn + run.ClusterCount);
        }
        if (runs.Count == 0)
            throw new InvalidDataException("MFT veri parçaları bulunamadı.");
    }

    private void ReadMapped(
        IReadOnlyList<NtfsDataRun> runs, long position, Span<byte> destination,
        CancellationToken cancellationToken)
    {
        var clusterSize = _source.Geometry.BytesPerCluster;
        var first = 0;
        var last = runs.Count - 1;
        var vcn = position / clusterSize;
        while (first < last)
        {
            var middle = first + (last - first + 1) / 2;
            if (runs[middle].StartVcn <= vcn)
                first = middle;
            else
                last = middle - 1;
        }
        var runIndex = first;
        while (!destination.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (runIndex >= runs.Count)
                throw new InvalidDataException("MFT veri akışı tamamlanmadı.");
            var run = runs[runIndex++];
            var start = checked(run.StartVcn * clusterSize);
            var runBytes = checked(run.ClusterCount * clusterSize);
            var within = position - start;
            if (within < 0 || within >= runBytes)
                throw new InvalidDataException("MFT veri akışında boşluk var.");
            var count = (int)Math.Min(destination.Length, runBytes - within);
            if (run.Lcn is long lcn)
                _source.ReadAt(checked(lcn * clusterSize + within), destination[..count]);
            else
                destination[..count].Clear();
            destination = destination[count..];
            position += count;
        }
    }
}

internal sealed record NtfsVolumeGeometry(
    int BytesPerSector, int BytesPerCluster, int BytesPerRecord,
    long TotalClusters, long MftValidDataLength);

internal interface INtfsVolumeSource : IDisposable
{
    NtfsVolumeGeometry Geometry { get; }
    byte[]? ReadFileRecord(ulong reference);
    void ReadAt(long offset, Span<byte> destination);
}
