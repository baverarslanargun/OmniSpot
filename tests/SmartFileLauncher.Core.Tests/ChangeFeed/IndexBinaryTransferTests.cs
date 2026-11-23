using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Index;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class IndexBinaryTransferTests
{
    private static readonly IndexEntry[] Entries =
    [
        new(5, 5, ".", FileAttributes.Directory, 0, 0, 0),
        new(20, 5, "İstanbul-ışık-😀.txt", FileAttributes.Archive, long.MaxValue, 123456789, 234567890),
        new(20, 6, "second-link", FileAttributes.Hidden, 17, 0, 0),
        new(21, 5, "bad-\ud800-name", FileAttributes.System, 23, 42, 51),
        new(22, 5, new string('ç', 255), FileAttributes.ReadOnly, 1, 2, 3)
    ];

    [Fact]
    public void BinaryCodecKeepsTheExistingBytesAndAllRecords()
    {
        var payload = Pack(Entries);
        Assert.Equal(Convert.FromBase64String(IndexEntryCodec.PackEntries(Entries)), payload.ToArray());
        Assert.Equal(Entries, IndexEntryCodec.ReadBinaryEntries(payload, Entries.Length));
        Assert.Empty(IndexEntryCodec.ReadBinaryEntries(ReadOnlyMemory<byte>.Empty, 0));
    }

    [Fact]
    public void BinaryCodecRejectsTruncatedNamesHeadersAndIncorrectCounts()
    {
        var payload = Pack(Entries);
        Assert.Throws<InvalidDataException>(() => IndexEntryCodec.ReadBinaryEntries(payload[..^1], Entries.Length).ToArray());
        Assert.Throws<InvalidDataException>(() => IndexEntryCodec.ReadBinaryEntries(payload[..20], 1).ToArray());
        Assert.Throws<InvalidDataException>(() => IndexEntryCodec.ReadBinaryEntries(payload, Entries.Length - 1).ToArray());
        Assert.Throws<InvalidDataException>(() => IndexEntryCodec.ReadBinaryEntries(payload, Entries.Length + 1).ToArray());
        var oddWideName = Pack([Entries[0]]).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(oddWideName.AsSpan(44), 0x8001);
        Assert.Throws<InvalidDataException>(() => IndexEntryCodec.ReadBinaryEntries(oddWideName, 1).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseRoundTripKeepsMetadataAndSupportsBothEncodings(bool binary)
    {
        var sent = Response(binary);
        using var stream = new FragmentedStream();
        await ChangeFeedMessageChannel.WriteResponseAsync(stream, sent, default);
        Assert.Equal(ChangeFeedMessageChannel.MeasureResponse(sent), stream.Length - sizeof(int));
        Assert.Equal(binary, BitConverter.ToInt32(stream.ToArray()) < 0);
        stream.Position = 0;
        var received = await ChangeFeedMessageChannel.ReadResponseAsync<ChangeFeedResponse>(stream, default);
        Assert.Equal(sent.Version, received.Version);
        Assert.Equal(sent.Status, received.Status);
        Assert.Equal(sent.Volume! with { Entries = null, BinaryEntries = null }, received.Volume! with { Entries = null, BinaryEntries = null });
        var entries = received.Volume!.BinaryEntries is { } bytes
            ? IndexEntryCodec.ReadBinaryEntries(bytes, received.Volume.Count)
            : IndexEntryCodec.UnpackEntries(received.Volume.Entries);
        Assert.Equal(Entries, entries);
    }

    [Fact]
    public async Task EmptyFinalBinaryPageRoundTrips()
    {
        using var stream = new MemoryStream();
        var response = Response(true) with { Volume = Response(true).Volume! with { Count = 0, BinaryEntries = ReadOnlyMemory<byte>.Empty, Done = true } };
        await ChangeFeedMessageChannel.WriteResponseAsync(stream, response, default);
        stream.Position = 0;
        var received = await ChangeFeedMessageChannel.ReadResponseAsync<ChangeFeedResponse>(stream, default);
        Assert.True(received.Volume!.Done);
        Assert.True(received.Volume.BinaryEntries!.Value.IsEmpty);
    }

    [Fact]
    public async Task OptInIsOptionalAndAnOldRequestReaderCanIgnoreIt()
    {
        using var stream = new MemoryStream();
        var request = new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.IndexVolumeScan, BinaryVolumeEntries: true);
        await ChangeFeedMessageChannel.WriteRequestAsync(stream, request, default);
        var payload = stream.ToArray().AsMemory(sizeof(int));
        using var json = JsonDocument.Parse(payload);
        Assert.True(json.RootElement.GetProperty("BinaryVolumeEntries").GetBoolean());
        Assert.Equal(request.Version, JsonSerializer.Deserialize<LegacyRequest>(payload.Span)!.Version);
        stream.SetLength(0);
        stream.Position = 0;
        await ChangeFeedMessageChannel.WriteRequestAsync(stream, request with { BinaryVolumeEntries = false }, default);
        using var legacy = JsonDocument.Parse(stream.ToArray().AsMemory(sizeof(int)));
        Assert.False(legacy.RootElement.TryGetProperty("BinaryVolumeEntries", out _));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(-16777217)]
    public async Task BinaryFrameRejectsInvalidLengths(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length));
        await Assert.ThrowsAsync<ChangeFeedProtocolException>(() => ChangeFeedMessageChannel.ReadResponseAsync<ChangeFeedResponse>(stream, default));
    }

    [Fact]
    public async Task BinaryFrameRejectsTruncationAndOversizedPayload()
    {
        using var stream = new MemoryStream();
        await ChangeFeedMessageChannel.WriteResponseAsync(stream, Response(true), default);
        stream.SetLength(stream.Length - 1);
        stream.Position = 0;
        await Assert.ThrowsAsync<ChangeFeedProtocolException>(() => ChangeFeedMessageChannel.ReadResponseAsync<ChangeFeedResponse>(stream, default));
        using var target = new MemoryStream();
        var oversized = Response(true) with { Volume = Response(true).Volume! with { BinaryEntries = new byte[ChangeFeedProtocol.MaximumResponseBytes] } };
        await Assert.ThrowsAsync<ChangeFeedProtocolException>(() => ChangeFeedMessageChannel.WriteResponseAsync(target, oversized, default));
        Assert.Equal(0, target.Length);
    }

    [Fact]
    public async Task BinaryFrameHonorsCancellation()
    {
        using var stream = new MemoryStream();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChangeFeedMessageChannel.WriteResponseAsync(stream, Response(true), stop.Token));
    }

    private static ChangeFeedResponse Response(bool binary) => new(ChangeFeedProtocol.Version, ChangeFeedResponseStatus.Ok,
        Volume: new(@"C:\", 5, 42, 7, 10, 50, "next-page", binary ? null : IndexEntryCodec.PackEntries(Entries),
            Count: Entries.Length, BinaryEntries: binary ? Pack(Entries) : (ReadOnlyMemory<byte>?)null));

    private static ReadOnlyMemory<byte> Pack(IEnumerable<IndexEntry> entries)
    {
        var buffer = new ArrayBufferWriter<byte>();
        foreach (var entry in entries) IndexEntryCodec.WriteBinary(buffer, entry);
        return buffer.WrittenMemory;
    }

    private sealed record LegacyRequest(int Version);

    private sealed class FragmentedStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }
}
