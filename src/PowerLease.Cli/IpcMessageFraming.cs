using System.Buffers.Binary;
using System.Text.Json;
using PowerLease.Ipc.Contracts;

namespace PowerLease.Cli;

internal static class IpcMessageFraming
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    internal static Task WriteRequestAsync(
        Stream stream,
        RequestEnvelope request,
        CancellationToken cancellationToken) =>
        WriteAsync(stream, request, cancellationToken);

    internal static Task<RequestEnvelope> ReadRequestAsync(Stream stream, CancellationToken cancellationToken) =>
        ReadAsync<RequestEnvelope>(stream, cancellationToken);

    internal static Task WriteResponseAsync(
        Stream stream,
        ResponseEnvelope response,
        CancellationToken cancellationToken) =>
        WriteAsync(stream, response, cancellationToken);

    internal static Task<ResponseEnvelope> ReadResponseAsync(Stream stream, CancellationToken cancellationToken) =>
        ReadAsync<ResponseEnvelope>(stream, cancellationToken);

    private static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (payload.Length > IpcProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException(
                $"The message is {payload.Length} bytes, exceeding the {IpcProtocol.MaxMessageBytes}-byte limit.");
        }

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > IpcProtocol.MaxMessageBytes)
        {
            throw new InvalidDataException(
                $"The service returned an invalid {length}-byte message length.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(payload, Json)
            ?? throw new InvalidDataException("The service returned an empty JSON message.");
    }
}
