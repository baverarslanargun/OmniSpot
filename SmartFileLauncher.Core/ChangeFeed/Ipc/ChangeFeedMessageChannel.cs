using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed class ChangeFeedProtocolException : Exception
{
    public ChangeFeedProtocolException(string message)
        : base(message)
    {
    }

    public ChangeFeedProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class ChangeFeedMessageChannel
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        if (payload.Length > ChangeFeedProtocol.MaximumMessageBytes)
        {
            throw new ChangeFeedProtocolException(
                $"İleti azami boyutu aşıyor: {payload.Length} > {ChangeFeedProtocol.MaximumMessageBytes}");
        }

        var frame = new byte[ChangeFeedProtocol.LengthPrefixBytes + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, ChangeFeedProtocol.LengthPrefixBytes);

        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var prefix = new byte[ChangeFeedProtocol.LengthPrefixBytes];
        await ReadExactAsync(stream, prefix, cancellationToken).ConfigureAwait(false);

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > ChangeFeedProtocol.MaximumMessageBytes)
        {
            throw new ChangeFeedProtocolException(
                $"İleti uzunluğu geçersiz: {length}");
        }

        var payload = new byte[length];
        await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false);

        T? message;
        try
        {
            message = JsonSerializer.Deserialize<T>(payload, SerializerOptions);
        }
        catch (JsonException failure)
        {
            throw new ChangeFeedProtocolException("İleti geçerli JSON değil.", failure);
        }

        return message ?? throw new ChangeFeedProtocolException("İleti boş.");
    }

    private static async Task ReadExactAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                throw new ChangeFeedProtocolException(
                    $"İleti eksik geldi: {offset}/{buffer.Length} bayt.");
            }

            offset += read;
        }
    }
}
