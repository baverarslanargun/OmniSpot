namespace SmartFileLauncher.Core.Indexing.Ntfs;

public sealed partial class NtfsMftReader
{
    internal long ProbeMftLength => _mft.Length;

    internal int ProbeRecordSize => _source.Geometry.BytesPerRecord;

    internal NtfsMftReader ProbeOpenWorker() =>
        new((_parallelSourceFactory ??
            throw new InvalidOperationException("Paralel MFT taraması için bağımsız birim kaynağı fabrikası gerekli."))());

    internal void ProbeReadRaw(long position, Span<byte> buffer, CancellationToken cancellationToken) =>
        ReadRawChunk(position, buffer, cancellationToken);

    internal int ProbeParse(byte[] buffer, long position, int count, CancellationToken cancellationToken) =>
        ReadEntriesFromChunk(buffer, position, count, cancellationToken).ToArray().Length;
}
