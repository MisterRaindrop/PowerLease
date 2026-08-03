using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    private sealed class FakeClock : IClock
    {
        private static readonly Guid Epoch = new("9497ea39-5ac8-4922-8ce2-79d9440035ba");

        public DateTimeOffset UtcNow { get; } = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

        public MonotonicStamp Now { get; } = new(Epoch, TimeSpan.FromMinutes(1));
    }
}
