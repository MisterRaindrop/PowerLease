using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using PowerLease.Ipc.Contracts;

namespace PowerLease.Cli;

public sealed class PowerLeaseClient : IPowerLeaseClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>> _exchange;

    public PowerLeaseClient()
        : this(ExchangeOverPipeAsync)
    {
    }

    internal PowerLeaseClient(Func<RequestEnvelope, CancellationToken, Task<ResponseEnvelope>> exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        _exchange = exchange;
    }

    public Task<StatusResponse> GetStatusAsync(CancellationToken cancellationToken) =>
        QueryAsync<StatusResponse>(IpcMethods.GetStatus, payload: null, cancellationToken);

    public Task<ListLeasesResponse> ListLeasesAsync(CancellationToken cancellationToken) =>
        QueryAsync<ListLeasesResponse>(IpcMethods.ListLeases, payload: null, cancellationToken);

    public Task<CommandResponse> CreateLeaseAsync(
        TimeSpan duration,
        string? reason,
        CancellationToken cancellationToken) =>
        CommandAsync(IpcMethods.CreateLease, new CreateLeasePayload(duration, reason), cancellationToken);

    public Task<CommandResponse> ReleaseLeaseAsync(
        string? leaseId,
        CancellationToken cancellationToken) =>
        CommandAsync(IpcMethods.ReleaseLease, new ReleaseLeasePayload(leaseId), cancellationToken);

    public Task<WakeStatusResponse> GetWakeStatusAsync(CancellationToken cancellationToken) =>
        QueryAsync<WakeStatusResponse>(IpcMethods.GetWakeStatus, payload: null, cancellationToken);

    private async Task<T> QueryAsync<T>(string method, object? payload, CancellationToken cancellationToken)
    {
        var request = CreateRequest(method, payload);
        var response = await ExchangeSafelyAsync(request, cancellationToken).ConfigureAwait(false);
        ValidateReply(request, response);

        if (!response.Accepted)
        {
            throw new ServiceUnavailableException(response.Error ?? "The service refused the query.");
        }

        return ReadAcceptedPayload<T>(response);
    }

    private async Task<CommandResponse> CommandAsync(
        string method,
        object payload,
        CancellationToken cancellationToken)
    {
        var request = CreateRequest(method, payload);
        var response = await ExchangeSafelyAsync(request, cancellationToken).ConfigureAwait(false);
        ValidateReply(request, response);

        return response.Accepted
            ? ReadAcceptedPayload<CommandResponse>(response)
            : new CommandResponse(
                "Refused",
                null,
                response.Error ?? "The service refused the request without an explanation.");
    }

    private async Task<ResponseEnvelope> ExchangeSafelyAsync(
        RequestEnvelope request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _exchange(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServiceUnavailableException("Timed out while connecting to the service.", error);
        }
        catch (IOException error)
        {
            throw new ServiceUnavailableException($"The named-pipe exchange failed: {error.Message}", error);
        }
        catch (JsonException error)
        {
            throw new ServiceUnavailableException($"The service returned unreadable JSON: {error.Message}", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new ServiceUnavailableException($"The named pipe could not be accessed: {error.Message}", error);
        }
        catch (TimeoutException error)
        {
            throw new ServiceUnavailableException($"The named-pipe exchange timed out: {error.Message}", error);
        }
        catch (ObjectDisposedException error)
        {
            throw new ServiceUnavailableException($"The named-pipe connection closed unexpectedly: {error.Message}", error);
        }
    }

    private static RequestEnvelope CreateRequest(string method, object? payload) =>
        new(
            IpcProtocol.Version,
            Guid.NewGuid(),
            method,
            payload is null ? null : JsonSerializer.Serialize(payload, Json));

    private static T ReadAcceptedPayload<T>(ResponseEnvelope response)
    {
        if (response.PayloadJson is null)
        {
            throw new ServiceUnavailableException("The service accepted the request but returned no payload.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(response.PayloadJson, Json)
                ?? throw new ServiceUnavailableException(
                    "The service accepted the request but returned an empty payload.");
        }
        catch (JsonException error)
        {
            throw new ServiceUnavailableException(
                $"The service accepted the request but returned an unreadable payload: {error.Message}",
                error);
        }
    }

    private static void ValidateReply(RequestEnvelope request, ResponseEnvelope response)
    {
        if (response.ProtocolVersion != IpcProtocol.Version)
        {
            throw new ServiceUnavailableException(
                $"The service replied with protocol version {response.ProtocolVersion}; " +
                $"this client requires {IpcProtocol.Version}.");
        }

        if (response.RequestId != request.RequestId)
        {
            throw new ServiceUnavailableException("The service reply did not match the request identifier.");
        }
    }

    private static async Task<ResponseEnvelope> ExchangeOverPipeAsync(
        RequestEnvelope request,
        CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            IpcProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await IpcMessageFraming.WriteRequestAsync(pipe, request, cancellationToken).ConfigureAwait(false);
        return await IpcMessageFraming.ReadResponseAsync(pipe, cancellationToken).ConfigureAwait(false);
    }
}
