using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerLease.Application.Kernel;
using PowerLease.Ipc.Contracts;

namespace PowerLease.Service;

internal sealed class PipeServerWorker : BackgroundService
{
    private const int MaximumServerInstances = 16;
    private static readonly TimeSpan ConnectionDeadline = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null);

    private static readonly Action<ILogger, string, Exception?> LogConnectionFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, "PipeConnectionFailure"),
            "A named-pipe request failed: {Message}");

    private readonly IpcRequestRouter _router;
    private readonly ILogger<PipeServerWorker> _logger;
    private readonly string _pipeName;

    public PipeServerWorker(IpcRequestRouter router, ILogger<PipeServerWorker> logger)
        : this(router, logger, IpcProtocol.PipeName)
    {
    }

    internal PipeServerWorker(IpcRequestRouter router, ILogger<PipeServerWorker> logger, string pipeName)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _router = router;
        _logger = logger;
        _pipeName = pipeName;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connections = new HashSet<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            connections.RemoveWhere(connection => connection.IsCompleted);
            if (connections.Count >= MaximumServerInstances)
            {
                _ = await Task.WhenAny(connections).ConfigureAwait(false);
                continue;
            }

            var pipe = CreatePipe(_pipeName);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                connections.Add(HandleOwnedConnectionAsync(pipe, stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                pipe.Dispose();
                break;
            }
#pragma warning disable CA1031 // A malformed or disconnected client must not stop the service endpoint.
            catch (Exception error)
#pragma warning restore CA1031
            {
                pipe.Dispose();
                LogConnectionFailure(_logger, error.Message, error);
            }
        }

        await Task.WhenAll(connections).ConfigureAwait(false);
    }

    private async Task HandleOwnedConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using (pipe)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            // The deadline starts before impersonation, so an authenticated client cannot occupy an instance by
            // connecting and then withholding its frame. Disposal below tears down every expired connection.
            deadline.CancelAfter(ConnectionDeadline);
            try
            {
                await HandleConnectionAsync(pipe, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
            }
#pragma warning disable CA1031 // One malformed or disconnected client must not affect other pipe instances.
            catch (Exception error)
#pragma warning restore CA1031
            {
                LogConnectionFailure(_logger, error.Message, error);
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var caller = CaptureCaller(pipe);
        cancellationToken.ThrowIfCancellationRequested();
        var request = await ReadRequestAsync(pipe, cancellationToken).ConfigureAwait(false);
        var response = request is null
            ? ResponseEnvelope.Refused(Guid.Empty, "The request frame was invalid or too large.")
            : await _router.HandleAsync(request, caller, cancellationToken).ConfigureAwait(false);
        await WriteResponseAsync(pipe, response, cancellationToken).ConfigureAwait(false);
    }

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        var security = new PipeSecurity();

        // Authenticated Users (S-1-5-11) may connect for status and own-lease operations. Local System
        // (S-1-5-18) and Builtin Administrators (S-1-5-32-544) get full control so service management and
        // elevated administrative calls cannot be locked out by the pipe ACL.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, domainSid: null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            AdministratorsSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            MaximumServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            IpcProtocol.MaxMessageBytes,
            IpcProtocol.MaxMessageBytes,
            security);
    }

    private static CallerSnapshot CaptureCaller(NamedPipeServerStream pipe)
    {
        CallerSnapshot? caller = null;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var sid = identity.User?.Value
                ?? throw new UnauthorizedAccessException("The pipe caller has no security identifier.");
            var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

            // Only plain values leave this callback. The impersonation token and WindowsIdentity are disposed
            // before any request is queued or any await occurs.
            caller = new CallerSnapshot
            {
                Sid = sid,
                AccountName = identity.Name,
                IsElevated = elevated,
                // IsInRole performs an access check against enabled token groups. Reading identity.Groups here
                // would count the Administrators SID even when UAC has made it deny-only.
                IsAdministrator = elevated
            };
        });

        return caller ?? throw new UnauthorizedAccessException("The pipe caller could not be identified.");
    }

    private static async Task<RequestEnvelope?> ReadRequestAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        try
        {
            var prefix = new byte[sizeof(int)];
            await pipe.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if (length <= 0 || length > IpcProtocol.MaxMessageBytes)
            {
                return null;
            }

            var payload = new byte[length];
            await pipe.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            try
            {
                return JsonSerializer.Deserialize<RequestEnvelope>(payload, Json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        catch (EndOfStreamException)
        {
            // A partial prefix or payload is malformed, not a reason to let this connection stop the endpoint.
            return null;
        }
        catch (IOException)
        {
            // Windows may report a client disconnect as a broken pipe instead of end-of-stream.
            return null;
        }
    }

    private static async Task WriteResponseAsync(
        NamedPipeServerStream pipe,
        ResponseEnvelope response,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(response, Json);
        if (payload.Length > IpcProtocol.MaxMessageBytes)
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(
                ResponseEnvelope.Refused(response.RequestId, "The response is larger than the protocol limit."),
                Json);
        }

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await pipe.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
