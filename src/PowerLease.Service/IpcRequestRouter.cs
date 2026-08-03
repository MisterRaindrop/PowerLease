using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerLease.Application.Kernel;
using PowerLease.Domain;
using PowerLease.Infrastructure.Windows.Power;
using PowerLease.Ipc.Contracts;
using PowerLease.Persistence.History;

namespace PowerLease.Service;

internal sealed class IpcRequestRouter
{
    private static readonly TimeSpan CommandQueueTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<KernelSnapshot> _snapshot;
    private readonly Func<LeaseLoadResult> _leases;
    private readonly Func<PowerCapabilitySnapshot> _wakeStatus;
    private readonly Func<LeaseCommand, CancellationToken, Task<LeaseCommandResult>> _dispatch;
    private readonly IClock _clock;

    public IpcRequestRouter(
        PowerLease.Application.Hosting.KernelLoop loop,
        LeaseCommandDispatcher commands,
        SqliteHistoryStore store,
        StoreGate storeGate,
        IPowerCapabilityProbe powerCapabilities,
        IClock clock)
        : this(
            () => loop.Snapshot,
            () => LoadActiveLeases(store, storeGate),
            powerCapabilities.Read,
            commands.PostAndWaitAsync,
            clock)
    {
    }

    internal IpcRequestRouter(
        Func<KernelSnapshot> snapshot,
        Func<LeaseLoadResult> leases,
        Func<PowerCapabilitySnapshot> wakeStatus,
        Func<LeaseCommand, CancellationToken, Task<LeaseCommandResult>> dispatch,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentNullException.ThrowIfNull(wakeStatus);
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(clock);
        _snapshot = snapshot;
        _leases = leases;
        _wakeStatus = wakeStatus;
        _dispatch = dispatch;
        _clock = clock;
    }

    public async Task<ResponseEnvelope> HandleAsync(
        RequestEnvelope request,
        CallerSnapshot caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);

        if (request.ProtocolVersion != IpcProtocol.Version)
        {
            return ResponseEnvelope.Refused(
                request.RequestId,
                $"Protocol version {request.ProtocolVersion} is not supported; use {IpcProtocol.Version}.");
        }

        // Authorization intentionally precedes the handler lookup. An unauthorized caller must not learn which
        // declared methods this service version happens to implement.
        if (!IpcMethods.IsAllowed(request.Method, caller.IsAdministrator))
        {
            return ResponseEnvelope.Refused(request.RequestId, "The caller is not allowed to use this method.");
        }

        return request.Method switch
        {
            IpcMethods.GetStatus => Accepted(request.RequestId, MapStatus(_snapshot())),
            IpcMethods.ListLeases => Accepted(request.RequestId, MapLeases(_leases(), _clock.UtcNow)),
            IpcMethods.GetWakeStatus => Accepted(request.RequestId, MapWakeStatus(_wakeStatus())),
            IpcMethods.CreateLease => await HandleCommandAsync(
                request,
                caller,
                LeaseCommandKind.Create,
                cancellationToken).ConfigureAwait(false),
            IpcMethods.RenewLease => await HandleCommandAsync(
                request,
                caller,
                LeaseCommandKind.Renew,
                cancellationToken).ConfigureAwait(false),
            IpcMethods.ReleaseLease => await HandleCommandAsync(
                request,
                caller,
                LeaseCommandKind.Release,
                cancellationToken).ConfigureAwait(false),
            _ => ResponseEnvelope.Refused(
                request.RequestId,
                "This method is not available in this version of PowerLease.")
        };
    }

    private async Task<ResponseEnvelope> HandleCommandAsync(
        RequestEnvelope request,
        CallerSnapshot caller,
        LeaseCommandKind kind,
        CancellationToken cancellationToken)
    {
        if (!TryReadCommand(request, kind, out var leaseId, out var duration, out var reason, out var error))
        {
            return ResponseEnvelope.Refused(request.RequestId, error!);
        }

        var now = _clock.Now;
        var command = new LeaseCommand
        {
            RequestId = $"ipc:{caller.Sid}:{request.RequestId:N}",
            Caller = caller,
            Kind = kind,
            LeaseId = leaseId,
            Duration = duration,
            Source = LeaseSource.Cli,
            Reason = reason,
            Deadline = new MonotonicStamp(now.EpochId, now.Elapsed + CommandQueueTimeout),
            PayloadHash = HashPayload(request.Method, request.PayloadJson)
        };

        var result = await _dispatch(command, cancellationToken).ConfigureAwait(false);
        return Accepted(
            request.RequestId,
            new CommandResponse(result.Status.ToString(), result.LeaseId, result.Error));
    }

    private static bool TryReadCommand(
        RequestEnvelope request,
        LeaseCommandKind kind,
        out string? leaseId,
        out TimeSpan duration,
        out string? reason,
        out string? error)
    {
        leaseId = null;
        duration = TimeSpan.Zero;
        reason = null;
        error = null;

        try
        {
            switch (kind)
            {
                case LeaseCommandKind.Create:
                    {
                        var payload = JsonSerializer.Deserialize<CreateLeasePayload>(request.PayloadJson ?? "{}", Json);
                        if (payload is null || payload.Duration <= TimeSpan.Zero)
                        {
                            error = "CreateLease requires a positive duration.";
                            return false;
                        }

                        leaseId = $"cli:{request.RequestId:N}";
                        duration = payload.Duration;
                        reason = payload.Reason;
                        return true;
                    }

                case LeaseCommandKind.Renew:
                    {
                        var payload = JsonSerializer.Deserialize<RenewLeasePayload>(request.PayloadJson ?? "{}", Json);
                        if (payload is null || string.IsNullOrWhiteSpace(payload.LeaseId) || payload.Duration <= TimeSpan.Zero)
                        {
                            error = "RenewLease requires a leaseId and a positive duration.";
                            return false;
                        }

                        leaseId = payload.LeaseId;
                        duration = payload.Duration;
                        return true;
                    }

                case LeaseCommandKind.Release:
                    {
                        var payload = JsonSerializer.Deserialize<ReleaseLeasePayload>(request.PayloadJson ?? "{}", Json);
                        if (payload is null || string.IsNullOrWhiteSpace(payload.LeaseId))
                        {
                            error = "ReleaseLease requires a leaseId.";
                            return false;
                        }

                        leaseId = payload.LeaseId;
                        return true;
                    }

                default:
                    error = "The command kind is not supported.";
                    return false;
            }
        }
        catch (JsonException exception)
        {
            error = $"The request payload is not valid JSON: {exception.Message}";
            return false;
        }
    }

    private static StatusResponse MapStatus(KernelSnapshot snapshot) => new()
    {
        Revision = snapshot.Revision,
        ProtectionState = snapshot.ProtectionState switch
        {
            ProtectionState.Released => ReportedProtectionState.Released,
            ProtectionState.Protected => ReportedProtectionState.Protected,
            ProtectionState.Unprotected => ReportedProtectionState.Unprotected,
            _ => throw new InvalidOperationException($"Unknown protection state '{snapshot.ProtectionState}'.")
        },
        ShouldHold = snapshot.ShouldHold,
        Inhibitors = snapshot.Decision.Inhibitors
            .Select(inhibitor => new ReportedInhibitor(
                inhibitor.Kind.ToString(),
                inhibitor.Reason,
                inhibitor.SinceUtc,
                inhibitor.Detail))
            .ToArray(),
        UncoveredKinds = snapshot.Decision.UncoveredKinds.Select(kind => kind.ToString()).ToArray(),
        Sources = snapshot.Sources
            .Where(source => !source.Trusted)
            .Select(source => new ReportedSource(source.SourceId, source.Trusted, source.Detail))
            .ToArray(),
        UnhealthySources = snapshot.UnhealthySources,
        Faults = snapshot.Faults.Select(fault => $"{fault.Key}: {fault.Message}").ToArray(),
        EmergencyInhibitRaised = snapshot.EmergencyInhibitRaised,
        EmergencyInhibitReason = snapshot.EmergencyInhibitReason,
        GracePeriodActive = snapshot.GracePeriodActive,
        HistoryWriteFailures = snapshot.HistoryWriteFailures
    };

    private static ListLeasesResponse MapLeases(LeaseLoadResult loaded, DateTimeOffset nowUtc)
    {
        return new ListLeasesResponse
        {
            Leases = loaded.Leases.Select(lease => new ReportedLease(
                lease.Id,
                lease.Source.ToString(),
                lease.Reason,
                lease.OwnerUser,
                lease.StartedAtUtc,
                Remaining(lease.ExpiresAtUtc, nowUtc),
                lease.Status.ToString())).ToArray()
        };
    }

    private static WakeStatusResponse MapWakeStatus(PowerCapabilitySnapshot snapshot) => new()
    {
        SystemRequiredHonouredOnMains = snapshot.SystemRequiredHonouredOnMains,
        SystemRequiredHonouredOnBattery = snapshot.SystemRequiredHonouredOnBattery,
        ModernStandby = snapshot.ModernStandby,
        RunningOnBattery = snapshot.RunningOnBattery,
        WakeTimersAllowed = null,
        Unavailable =
        [
            .. snapshot.Unavailable,
            "Wake timer policy is not available from this service version."
        ]
    };

    private static ResponseEnvelope Accepted<T>(Guid requestId, T payload) =>
        new(IpcProtocol.Version, requestId, Accepted: true, JsonSerializer.Serialize(payload, Json));

    private static TimeSpan Remaining(DateTimeOffset? expiresAtUtc, DateTimeOffset nowUtc) =>
        expiresAtUtc is { } expiry && expiry > nowUtc ? expiry - nowUtc : TimeSpan.Zero;

    private static string HashPayload(string method, string? payloadJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(method + "\n" + (payloadJson ?? string.Empty))));

    private static LeaseLoadResult LoadActiveLeases(SqliteHistoryStore store, StoreGate gate)
    {
        lock (gate.SyncRoot)
        {
            return store.LoadLeases(LeaseStatus.Active);
        }
    }

    private sealed record CreateLeasePayload(TimeSpan Duration, string? Reason);

    private sealed record RenewLeasePayload(string? LeaseId, TimeSpan Duration);

    private sealed record ReleaseLeasePayload(string? LeaseId);
}
