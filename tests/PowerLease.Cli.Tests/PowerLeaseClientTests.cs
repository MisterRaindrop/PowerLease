using System.Text.Json;
using PowerLease.Ipc.Contracts;
using Xunit;

namespace PowerLease.Cli.Tests;

public sealed class PowerLeaseClientTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task A_refused_query_reports_the_servers_error_verbatim()
    {
        var client = Client(request => new ResponseEnvelope(
            IpcProtocol.Version,
            request.RequestId,
            Accepted: false,
            Error: "status is temporarily unavailable"));

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            client.GetStatusAsync(TestContext.Current.CancellationToken));

        Assert.Equal("status is temporarily unavailable", error.Message);
    }

    [Fact]
    public async Task A_refused_command_returns_a_command_response_with_the_error()
    {
        var client = Client(request => new ResponseEnvelope(
            IpcProtocol.Version,
            request.RequestId,
            Accepted: false,
            Error: "that hold belongs to another caller"));

        var result = await client.ReleaseLeaseAsync(
            "lease-9",
            TestContext.Current.CancellationToken);

        Assert.Equal("Refused", result.Status);
        Assert.Null(result.LeaseId);
        Assert.Equal("that hold belongs to another caller", result.Error);
    }

    [Fact]
    public async Task A_refused_command_without_an_error_cannot_look_successful()
    {
        var client = Client(request => new ResponseEnvelope(
            IpcProtocol.Version,
            request.RequestId,
            Accepted: false));

        var result = await client.CreateLeaseAsync(
            TimeSpan.FromHours(1),
            reason: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("Refused", result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task An_accepted_payload_is_deserialized()
    {
        var client = Client(request => new ResponseEnvelope(
            IpcProtocol.Version,
            request.RequestId,
            Accepted: true,
            """
            {"revision":42,"protectionState":1,"shouldHold":true}
            """));

        var result = await client.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(42, result.Revision);
        Assert.Equal(ReportedProtectionState.Protected, result.ProtectionState);
        Assert.True(result.ShouldHold);
    }

    [Fact]
    public async Task A_protocol_mismatch_is_reported_as_service_unavailable()
    {
        var client = Client(request => new ResponseEnvelope(
            IpcProtocol.Version + 1,
            request.RequestId,
            Accepted: true,
            "{}"));

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            client.GetWakeStatusAsync(TestContext.Current.CancellationToken));

        Assert.Contains("protocol version", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_accepted_reply_without_a_readable_payload_is_service_unavailable()
    {
        var client = Client(request => new ResponseEnvelope(
            IpcProtocol.Version,
            request.RequestId,
            Accepted: true,
            "not json"));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            client.ListLeasesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_uses_the_shared_payload_and_sends_no_identity()
    {
        RequestEnvelope? captured = null;
        var client = Client(request =>
        {
            captured = request;
            return AcceptedCommand(request, "cli-1");
        });

        await client.CreateLeaseAsync(
            TimeSpan.FromHours(3),
            "long build",
            TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(IpcMethods.CreateLease, captured.Method);
        var payload = JsonSerializer.Deserialize<CreateLeasePayload>(
            captured.PayloadJson!,
            Json);
        Assert.Equal(TimeSpan.FromHours(3), payload?.Duration);
        Assert.Equal("long build", payload?.Reason);
        Assert.DoesNotContain("user", captured.PayloadJson!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sid", captured.PayloadJson!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Each_call_gets_a_fresh_request_identifier()
    {
        var requestIds = new List<Guid>();
        var client = Client(request =>
        {
            requestIds.Add(request.RequestId);
            return AcceptedCommand(request, "cli-1");
        });

        await client.ReleaseLeaseAsync("lease-1", TestContext.Current.CancellationToken);
        await client.ReleaseLeaseAsync("lease-1", TestContext.Current.CancellationToken);

        Assert.Equal(2, requestIds.Count);
        Assert.NotEqual(requestIds[0], requestIds[1]);
    }

    private static PowerLeaseClient Client(Func<RequestEnvelope, ResponseEnvelope> answer) =>
        new((request, _) => Task.FromResult(answer(request)));

    private static ResponseEnvelope AcceptedCommand(RequestEnvelope request, string leaseId) =>
        new(
            IpcProtocol.Version,
            request.RequestId,
            Accepted: true,
            JsonSerializer.Serialize(new CommandResponse("Created", leaseId)));
}
