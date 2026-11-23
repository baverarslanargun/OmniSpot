using System.Buffers.Binary;
using System.Text.Json;
using SmartFileLauncher.Core.Index;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public static partial class ChangeFeedMessageChannel
{
    private static async Task WriteBinaryVolumeAsync(Stream stream, ChangeFeedResponse response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var binary = response.Volume!.BinaryEntries!.Value;
        ValidateBinaryVolume(response, binary.Length);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(response, SerializerOptions);
        var length = sizeof(int) + (long)metadata.Length + binary.Length;
        if (metadata.Length > ChangeFeedProtocol.MaximumRequestBytes || length > ChangeFeedProtocol.MaximumResponseBytes)
            throw new ChangeFeedProtocolException("İkili indeks iletisi azami boyutu aşıyor.");
        var prefix = new byte[2 * sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, -(int)length);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(sizeof(int)), metadata.Length);
        await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(metadata, ct).ConfigureAwait(false);
        await stream.WriteAsync(binary, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<ChangeFeedResponse> ReadBinaryVolumeAsync(Stream stream, int length, CancellationToken ct)
    {
        var prefix = new byte[sizeof(int)];
        await ReadExactAsync(stream, prefix, ct).ConfigureAwait(false);
        var metadataLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (metadataLength <= 0 || metadataLength > ChangeFeedProtocol.MaximumRequestBytes || metadataLength > length - sizeof(int))
            throw new ChangeFeedProtocolException("İkili indeks başlığı uzunluğu geçersiz.");
        var metadata = new byte[metadataLength];
        await ReadExactAsync(stream, metadata, ct).ConfigureAwait(false);
        ChangeFeedResponse? response;
        try { response = JsonSerializer.Deserialize<ChangeFeedResponse>(metadata, SerializerOptions); }
        catch (JsonException failure) { throw new ChangeFeedProtocolException("İkili indeks başlığı geçerli JSON değil.", failure); }
        var binaryLength = length - sizeof(int) - metadataLength;
        ValidateBinaryVolume(response, binaryLength);
        var binary = new byte[binaryLength];
        await ReadExactAsync(stream, binary, ct).ConfigureAwait(false);
        return response! with { Volume = response!.Volume! with { BinaryEntries = binary } };
    }

    private static void ValidateBinaryVolume(ChangeFeedResponse? response, int binaryLength)
    {
        if (response is not { Status: ChangeFeedResponseStatus.Ok, Volume: { Entries: null, States: null } volume } ||
            volume.Count < 0 || volume.Count > binaryLength / IndexEntryCodec.BinaryEntryHeaderBytes ||
            (volume.Count == 0 && binaryLength != 0))
            throw new ChangeFeedProtocolException("İkili indeks sayfası başlığı geçersiz.");
    }
}
