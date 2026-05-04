using System.Buffers.Binary;
using System.Text.Json;

namespace WpfToolsMcp.AgentProtocol;

public static class PipeProtocol
{
    private const int MaxMessageBytes = 25 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > MaxMessageBytes)
        {
            throw new InvalidOperationException($"Message exceeds max size of {MaxMessageBytes} bytes.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken);

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0)
        {
            throw new InvalidOperationException($"Invalid message length {length}.");
        }

        if (length > MaxMessageBytes)
        {
            throw new InvalidOperationException($"Message exceeds max size of {MaxMessageBytes} bytes.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);

        var message = JsonSerializer.Deserialize<T>(payload, JsonOptions);
        if (message is null)
        {
            throw new InvalidOperationException("Failed to deserialize message.");
        }

        return message;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected end of stream.");
            }

            offset += read;
        }
    }
}
