using System.Buffers.Binary;
using System.IO;
using System.Text.Encodings.Web;
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
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static Task WriteRequestAsync<T>(Stream stream, T message, CancellationToken cancellationToken) =>
        WriteAsync(stream, message, ChangeFeedProtocol.MaximumRequestBytes, cancellationToken);

    public static Task<T> ReadRequestAsync<T>(Stream stream, CancellationToken cancellationToken) =>
        ReadAsync<T>(stream, ChangeFeedProtocol.MaximumRequestBytes, cancellationToken);

    public static Task WriteResponseAsync<T>(Stream stream, T message, CancellationToken cancellationToken) =>
        WriteAsync(stream, message, ChangeFeedProtocol.MaximumResponseBytes, cancellationToken);

    public static Task<T> ReadResponseAsync<T>(Stream stream, CancellationToken cancellationToken) =>
        ReadAsync<T>(stream, ChangeFeedProtocol.MaximumResponseBytes, cancellationToken);

    private static async Task WriteAsync<T>(
        Stream stream,
        T message,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        if (payload.Length > maximumBytes)
        {
            throw new ChangeFeedProtocolException(
                $"İleti azami boyutu aşıyor: {payload.Length} > {maximumBytes}");
        }

        var prefix = new byte[ChangeFeedProtocol.LengthPrefixBytes];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);

        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var prefix = new byte[ChangeFeedProtocol.LengthPrefixBytes];
        await ReadExactAsync(stream, prefix, cancellationToken).ConfigureAwait(false);

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > maximumBytes)
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
