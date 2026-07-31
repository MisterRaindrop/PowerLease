using Xunit;

namespace PowerLease.Ipc.Contracts.Tests;

/// <summary>
/// These are wire-protocol constants shared by the service and the CLI. Changing any of them
/// breaks an installed CLI talking to a running service, so they are pinned deliberately:
/// a failing test here means "you are making a compatibility-breaking change", not "update me".
/// </summary>
public sealed class IpcProtocolTests
{
    [Fact]
    public void Protocol_version_is_pinned()
    {
        Assert.Equal(1, IpcProtocol.Version);
    }

    [Fact]
    public void Pipe_name_is_pinned_and_version_qualified()
    {
        Assert.Equal("PowerLease.Service.v1", IpcProtocol.PipeName);
        Assert.EndsWith($"v{IpcProtocol.Version}", IpcProtocol.PipeName, StringComparison.Ordinal);
    }

    [Fact]
    public void Max_message_size_is_pinned_at_256_kib()
    {
        Assert.Equal(256 * 1024, IpcProtocol.MaxMessageBytes);
        Assert.Equal(262144, IpcProtocol.MaxMessageBytes);
    }
}

public sealed class RequestEnvelopeTests
{
    [Fact]
    public void Payload_json_defaults_to_null()
    {
        var envelope = new RequestEnvelope(IpcProtocol.Version, Guid.NewGuid(), "GetStatus");

        Assert.Null(envelope.PayloadJson);
    }

    [Fact]
    public void Envelopes_differing_only_by_request_id_are_not_equal()
    {
        var first = new RequestEnvelope(IpcProtocol.Version, Guid.NewGuid(), "GetStatus");
        var second = new RequestEnvelope(IpcProtocol.Version, Guid.NewGuid(), "GetStatus");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Envelopes_with_identical_members_are_equal()
    {
        var requestId = Guid.NewGuid();
        var first = new RequestEnvelope(IpcProtocol.Version, requestId, "CreateLease", "{\"hours\":3}");
        var same = new RequestEnvelope(IpcProtocol.Version, requestId, "CreateLease", "{\"hours\":3}");

        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
    }

    [Fact]
    public void Members_round_trip_through_the_constructor()
    {
        var requestId = Guid.NewGuid();

        var envelope = new RequestEnvelope(7, requestId, "ReleaseLease", "{}");

        Assert.Equal(7, envelope.ProtocolVersion);
        Assert.Equal(requestId, envelope.RequestId);
        Assert.Equal("ReleaseLease", envelope.Method);
        Assert.Equal("{}", envelope.PayloadJson);
    }
}
