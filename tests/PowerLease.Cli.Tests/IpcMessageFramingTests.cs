using System.Buffers.Binary;
using System.Text;
using PowerLease.Ipc.Contracts;
using Xunit;

namespace PowerLease.Cli.Tests;

public sealed class IpcMessageFramingTests
{
    [Fact]
    public async Task A_request_round_trips_through_a_plain_stream()
    {
        var request = new RequestEnvelope(
            IpcProtocol.Version,
            Guid.NewGuid(),
            IpcMethods.CreateLease,
            "{\"duration\":\"03:00:00\",\"reason\":\"build\"}");
        await using var stream = new MemoryStream();

        await IpcMessageFraming.WriteRequestAsync(
            stream,
            request,
            TestContext.Current.CancellationToken);
        stream.Position = 0;
        var result = await IpcMessageFraming.ReadRequestAsync(
            stream,
            TestContext.Current.CancellationToken);

        Assert.Equal(request, result);
    }

    [Fact]
    public async Task Frames_use_the_servers_camel_case_json()
    {
        var request = new RequestEnvelope(IpcProtocol.Version, Guid.NewGuid(), IpcMethods.GetStatus);
        await using var stream = new MemoryStream();

        await IpcMessageFraming.WriteRequestAsync(
            stream,
            request,
            TestContext.Current.CancellationToken);
        var frame = stream.ToArray();
        var json = Encoding.UTF8.GetString(frame, sizeof(int), frame.Length - sizeof(int));

        Assert.Contains("\"protocolVersion\"", json, StringComparison.Ordinal);
        Assert.Contains("\"requestId\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ProtocolVersion\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_response_round_trips_through_a_plain_stream()
    {
        var response = new ResponseEnvelope(
            IpcProtocol.Version,
            Guid.NewGuid(),
            Accepted: true,
            "{\"revision\":7}");
        await using var stream = new MemoryStream();

        await IpcMessageFraming.WriteResponseAsync(
            stream,
            response,
            TestContext.Current.CancellationToken);
        stream.Position = 0;
        var result = await IpcMessageFraming.ReadResponseAsync(
            stream,
            TestContext.Current.CancellationToken);

        Assert.Equal(response, result);
    }

    [Fact]
    public async Task A_frame_larger_than_the_protocol_limit_is_refused_before_reading_it()
    {
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, IpcProtocol.MaxMessageBytes + 1);
        await using var stream = new MemoryStream(prefix);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageFraming.ReadResponseAsync(stream, TestContext.Current.CancellationToken));

        Assert.Contains("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_oversized_request_is_not_sent()
    {
        var request = new RequestEnvelope(
            IpcProtocol.Version,
            Guid.NewGuid(),
            IpcMethods.CreateLease,
            new string('x', IpcProtocol.MaxMessageBytes));
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageFraming.WriteRequestAsync(stream, request, TestContext.Current.CancellationToken));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task A_truncated_frame_is_reported_instead_of_being_parsed()
    {
        var frame = new byte[sizeof(int) + 2];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 20);
        frame[sizeof(int)] = (byte)'{';
        frame[sizeof(int) + 1] = (byte)'}';
        await using var stream = new MemoryStream(frame);

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            IpcMessageFraming.ReadResponseAsync(stream, TestContext.Current.CancellationToken));
    }
}
