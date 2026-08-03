using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PowerLease.Application.Hosting;
using PowerLease.Application.Inhibitors;
using PowerLease.Application.Kernel;
using PowerLease.Domain;
using PowerLease.Infrastructure.Windows.Power;
using PowerLease.Ipc.Contracts;
using PowerLease.Persistence.Configuration;
using PowerLease.Persistence.History;
using PowerLease.Service;
using Xunit;

namespace PowerLease.IntegrationTests;

public sealed class ServiceHostTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Expected_sources_match_the_registered_producer_services()
    {
        var config = new PowerLeaseConfig();
        var catalog = ProducerCatalog.Create(config);
        var services = new ServiceCollection();

        ServiceComposition.AddProducerServices(services, catalog);
        var registered = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => SourceId(descriptor.ImplementationType))
            .ToArray();
        var options = ServiceComposition.BuildKernelOptions(config, catalog);

        Assert.Equal(registered, options.ExpectedSources);
    }

    [Fact]
    public async Task Status_is_answered_from_the_published_snapshot_without_dispatching_a_command()
    {
        var snapshotReads = 0;
        var commandDispatches = 0;
        var router = new IpcRequestRouter(
            () =>
            {
                snapshotReads++;
                return KernelSnapshot.Initial;
            },
            () => new LeaseLoadResult([], []),
            () => new PowerCapabilitySnapshot(null, null, null, null, []),
            (_, _) =>
            {
                commandDispatches++;
                throw new InvalidOperationException("A status request must not queue a command.");
            },
            new FakeClock());
        var requestId = Guid.NewGuid();

        var response = await router.HandleAsync(
            new RequestEnvelope(IpcProtocol.Version, requestId, IpcMethods.GetStatus),
            Caller(),
            TestContext.Current.CancellationToken);

        Assert.True(response.Accepted);
        Assert.Equal(1, snapshotReads);
        Assert.Equal(0, commandDispatches);
        var status = JsonSerializer.Deserialize<StatusResponse>(response.PayloadJson!, Json);
        Assert.NotNull(status);
        Assert.Equal(KernelSnapshot.Initial.Revision, status!.Revision);
    }

    [Fact]
    public async Task Insufficient_access_is_refused_before_handler_availability_is_considered()
    {
        var handlerReads = 0;
        var router = new IpcRequestRouter(
            () =>
            {
                handlerReads++;
                return KernelSnapshot.Initial;
            },
            () =>
            {
                handlerReads++;
                return new LeaseLoadResult([], []);
            },
            () =>
            {
                handlerReads++;
                return new PowerCapabilitySnapshot(null, null, null, null, []);
            },
            (_, _) =>
            {
                handlerReads++;
                return Task.FromResult(new LeaseCommandResult("unused", LeaseCommandStatus.Rejected));
            },
            new FakeClock());

        var response = await router.HandleAsync(
            new RequestEnvelope(IpcProtocol.Version, Guid.NewGuid(), IpcMethods.UpdateRules),
            Caller(),
            TestContext.Current.CancellationToken);

        Assert.False(response.Accepted);
        Assert.Equal(0, handlerReads);
        Assert.NotNull(response.Error);
        Assert.Contains("not allowed", response.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_bare_release_resolves_the_callers_only_active_hold()
    {
        LeaseCommand? dispatched = null;
        var router = Router(
            [Lease("mine", Caller().Sid), Lease("somebody-elses", "S-1-5-21-2000")],
            command => dispatched = command);

        var response = await router.HandleAsync(
            ReleaseRequest(leaseId: null),
            Caller(),
            TestContext.Current.CancellationToken);

        Assert.True(response.Accepted, response.Error);
        Assert.NotNull(dispatched);
        Assert.Equal("mine", dispatched!.LeaseId);
    }

    [Fact]
    public async Task A_bare_release_explains_when_the_caller_has_no_active_hold()
    {
        var dispatched = false;
        var router = Router([Lease("somebody-elses", "S-1-5-21-2000")], _ => dispatched = true);

        var response = await router.HandleAsync(
            ReleaseRequest(leaseId: null),
            Caller(),
            TestContext.Current.CancellationToken);

        Assert.False(response.Accepted);
        Assert.False(dispatched);
        Assert.Contains("no active holds", response.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_bare_release_asks_for_an_identifier_when_the_caller_has_several_holds()
    {
        var dispatched = false;
        var router = Router(
            [Lease("first", Caller().Sid), Lease("second", Caller().Sid)],
            _ => dispatched = true);

        var response = await router.HandleAsync(
            ReleaseRequest(leaseId: null),
            Caller(),
            TestContext.Current.CancellationToken);

        Assert.False(response.Accepted);
        Assert.False(dispatched);
        Assert.Contains("more than one", response.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("identifier", response.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unreasonably_long_create_and_renew_requests_are_refused_before_dispatch()
    {
        var dispatched = false;
        var router = Router([], _ => dispatched = true);
        var create = new RequestEnvelope(
            IpcProtocol.Version,
            Guid.NewGuid(),
            IpcMethods.CreateLease,
            JsonSerializer.Serialize(new CreateLeasePayload(TimeSpan.FromDays(8), null), Json));
        var renew = new RequestEnvelope(
            IpcProtocol.Version,
            Guid.NewGuid(),
            IpcMethods.RenewLease,
            JsonSerializer.Serialize(new RenewLeasePayload("lease-1", TimeSpan.FromDays(8)), Json));

        var createResponse = await router.HandleAsync(create, Caller(), TestContext.Current.CancellationToken);
        var renewResponse = await router.HandleAsync(renew, Caller(), TestContext.Current.CancellationToken);

        Assert.False(createResponse.Accepted);
        Assert.False(renewResponse.Accepted);
        Assert.False(dispatched);
        Assert.Contains("7 days", createResponse.Error!, StringComparison.Ordinal);
        Assert.Contains("7 days", renewResponse.Error!, StringComparison.Ordinal);
    }

    private static IpcRequestRouter Router(
        IReadOnlyList<KeepAwakeLease> leases,
        Action<LeaseCommand> onDispatch) =>
        new(
            () => KernelSnapshot.Initial,
            () => new LeaseLoadResult(leases, []),
            () => new PowerCapabilitySnapshot(null, null, null, null, []),
            (command, _) =>
            {
                onDispatch(command);
                return Task.FromResult(
                    new LeaseCommandResult(command.RequestId, LeaseCommandStatus.Released, command.LeaseId));
            },
            new FakeClock());

    private static RequestEnvelope ReleaseRequest(string? leaseId) =>
        new(
            IpcProtocol.Version,
            Guid.NewGuid(),
            IpcMethods.ReleaseLease,
            JsonSerializer.Serialize(new ReleaseLeasePayload(leaseId), Json));

    private static KeepAwakeLease Lease(string id, string ownerSid) => new()
    {
        Id = id,
        Source = LeaseSource.Cli,
        OwnerSid = ownerSid,
        StartedAtUtc = DateTimeOffset.UnixEpoch,
        ExpiresAtUtc = DateTimeOffset.UnixEpoch + TimeSpan.FromHours(1),
        Status = LeaseStatus.Active,
        EpochId = Guid.Empty,
        OriginalDuration = TimeSpan.FromHours(1)
    };

    private static CallerSnapshot Caller() => new()
    {
        Sid = "S-1-5-21-1000",
        AccountName = "POWERLEASE\\developer",
        IsAdministrator = false,
        IsElevated = false
    };

    private static string SourceId(Type? implementationType) => implementationType switch
    {
        { } type when type == typeof(SshProducerWorker) => SshSessionCorrelator.SourceId,
        { } type when type == typeof(ActivityProducerWorker) => SystemActivityEvaluator.SourceId,
        { } type when type == typeof(ProcessProducerWorker) => ProtectedProcessEvaluator.SourceId,
        { } type when type == typeof(LockFileProducerWorker) => LockFileEvaluator.SourceId,
        { } type when type == typeof(ScheduleProducerWorker) => ScheduleEvaluator.SourceId,
        _ => throw new InvalidOperationException($"Unknown producer service '{implementationType}'.")
    };

    [Fact]
    public async Task A_retry_of_the_same_request_waits_for_the_one_answer()
    {
        // A client that lost its reply and reconnects must get the original outcome, not a second lease.
        var dispatcher = new LeaseCommandDispatcher(NewLoop());

        var first = dispatcher.PostAndWaitAsync(
            Command("req-1", LeaseCommandKind.Create, "hash-a"), TestContext.Current.CancellationToken);
        var retry = dispatcher.PostAndWaitAsync(
            Command("req-1", LeaseCommandKind.Create, "hash-a"), TestContext.Current.CancellationToken);

        dispatcher.Complete(new LeaseCommandResult("req-1", LeaseCommandStatus.Created, "lease-1"));

        Assert.Equal(LeaseCommandStatus.Created, (await first).Status);
        Assert.Equal("lease-1", (await retry).LeaseId);
    }

    [Fact]
    public async Task A_different_request_reusing_an_identifier_in_flight_is_refused_its_predecessors_answer()
    {
        // The database refuses a reused identifier carrying different content, but only once the first request
        // is durable. While one is still in flight, joining it would hand this caller somebody else's answer --
        // for a release, a success naming a lease it never asked about.
        var dispatcher = new LeaseCommandDispatcher(NewLoop());

        var original = dispatcher.PostAndWaitAsync(
            Command("req-1", LeaseCommandKind.Create, "hash-a"), TestContext.Current.CancellationToken);
        var impostor = await dispatcher.PostAndWaitAsync(
            Command("req-1", LeaseCommandKind.Release, "hash-b"), TestContext.Current.CancellationToken);

        Assert.Equal(LeaseCommandStatus.Rejected, impostor.Status);
        Assert.Contains("different request", impostor.Error!, StringComparison.OrdinalIgnoreCase);

        // The request already in flight is untouched, and still gets its own answer.
        Assert.False(original.IsCompleted);
        dispatcher.Complete(new LeaseCommandResult("req-1", LeaseCommandStatus.Created, "lease-1"));
        Assert.Equal(LeaseCommandStatus.Created, (await original).Status);
    }

    private static LeaseCommand Command(string requestId, LeaseCommandKind kind, string payloadHash) => new()
    {
        RequestId = requestId,
        Caller = Caller(),
        Kind = kind,
        LeaseId = "lease-1",
        Duration = TimeSpan.FromHours(1),
        Deadline = new MonotonicStamp(Guid.NewGuid(), TimeSpan.FromHours(1)),
        PayloadHash = payloadHash
    };

    private static KernelLoop NewLoop()
    {
        var kernel = new InhibitKernel(
            new KernelOptions { ExpectedSources = [] },
            new PowerInhibitCoordinator(new NoOpInhibitor()),
            new FakeClock());

        return new KernelLoop(kernel, new NoOpEffectExecutor(), new FakeTimeZones());
    }

    private sealed class NoOpInhibitor : IPowerInhibitor
    {
        public PowerInhibitResult Acquire(long generation) => PowerInhibitResult.Held;

        public void Close(long generation)
        {
        }
    }

    private sealed class NoOpEffectExecutor : IEffectExecutor
    {
        public Task<EffectCompletion> ExecuteAsync(KernelEffect effect, CancellationToken cancellationToken) =>
            Task.FromResult(new EffectCompletion(effect.EffectId, EffectOutcome.Succeeded));
    }

    private sealed class FakeTimeZones : ITimeZoneProvider
    {
        public TimeZoneInfo Current => TimeZoneInfo.Utc;

        public void Refresh()
        {
        }
    }

    private sealed class FakeClock : IClock
    {
        private static readonly Guid Epoch = new("9497ea39-5ac8-4922-8ce2-79d9440035ba");

        public DateTimeOffset UtcNow { get; } = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

        public MonotonicStamp Now { get; private set; } = new(Epoch, TimeSpan.FromMinutes(1));

        public int NewEpochs { get; private set; }

        /// <summary>
        /// Counted as well as applied, so a test can show the host really does start a new epoch on resume --
        /// the call the whole resume mechanism depends on, and the one that had no caller at all until it was
        /// wired up.
        /// </summary>
        public void BeginNewEpoch()
        {
            NewEpochs++;
            Now = new MonotonicStamp(Guid.NewGuid(), TimeSpan.Zero);
        }
    }
}
